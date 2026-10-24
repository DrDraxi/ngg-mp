using System;
using System.Collections.Generic;
using System.Text;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Save;
using UnityEngine;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Reads the facts of docs/GAME_INTEGRATION.md 2.3 from the running game: from the live world when "Main" is
    /// ready, else (host in Menu2 / CREDITS) from the loaded RunSave. Never changes game state. Main thread only.
    /// </summary>
    internal sealed class LiveFacts
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly GameContext _ctx;
        private readonly GameAccess _access;
        private readonly HashSet<string> _excludedObjs = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _scratch = new HashSet<string>(StringComparer.Ordinal);
        private int _excludedScene = -1;
        private TrackValue _lastTrack;
        private int _lastTrackScene = -1;
        private bool _lmugcNoted;
        private Authenticator[] _authenticators;
        private int _authenticatorScene = -1;
        private SaveManager _cardManager;
        private object _cardScores;
        private int _cardCount = -1;
        private byte[] _cardValue;
        private double _cardAt = double.NegativeInfinity;
        private const double CardMaxAgeSeconds = 10.0;

        public LiveFacts(GameContext ctx, GameAccess access)
        {
            _ctx = ctx;
            _access = access;
        }

        /// <summary>"Main" is set up and the save manager exists.</summary>
        public bool InWorld => _ctx.Scene == GameScene.Main && _ctx.WorldReady && SaveManager.instance != null;

        public bool IsJustGolf
        {
            get
            {
                var manager = SaveManager.instance;
                return manager != null && manager.m_gamemodeState != null && manager.m_gamemodeState.mode == GameMode.JustGolf;
            }
        }

        // ---- Track ----------------------------------------------------------------------------------

        /// <summary>
        /// The main track's live value. <paramref name="onMainTrack"/>: the main set is the loaded one and the player
        /// is in no bounty and no LMUGC round (otherwise the track is neither claimed nor changed here).
        /// </summary>
        public bool TryGetTrack(out TrackValue value, out bool onMainTrack)
        {
            value = default;
            onMainTrack = false;
            // JustGolf and LMUGC rounds are per player: the shared track does not exist there.
            if (!_access.TrackEnabled || IsJustGolf)
                return false;
            var system = ChallengeSystem.instance;
            if (system == null)
                return false;
            var main = system.m_mainChallengeSet;
            if (InLmugc())
                NoteLmugcSets(system);
            if (main == null || IsLmugcSet(main))
                return false;
            int index = _access.FirstIncomplete(main);
            string name = GameIds.ChallengeSetName(main);
            if (index < 0 || name.Length == 0)
                return false;
            value = new TrackValue(name, index);
            onMainTrack = system.m_challengeSet == main && !InBounty() && !InLmugc();
            _lastTrack = value;
            _lastTrackScene = _ctx.SceneLoadId;
            return true;
        }

        /// <summary>
        /// The main track when <see cref="TryGetTrack"/> cannot read it (should an LMUGC round leave its set as the main
        /// one; see <see cref="NoteLmugcSets"/>):
        /// the last value read in this world, else the run's saved challenge set if that is a main-track set. Null
        /// when neither is known.
        /// </summary>
        public byte[] FallbackTrack()
        {
            if (_lastTrackScene == _ctx.SceneLoadId && !string.IsNullOrEmpty(_lastTrack.Set))
                return FactCodec.EncodeTrack(_lastTrack);
            var manager = SaveManager.instance;
            if (manager == null || IsJustGolf)
                return null;
            string name = GameIds.Clean(manager.m_run.m_challengeSetLoaded);
            int index = manager.m_run.m_currentChallenge;
            if (name.Length == 0 || index < 0)
                return null;
            var lmugc = _access.LmugcChallengeSet();
            if (lmugc != null && GameIds.ChallengeSetName(lmugc) == name)
                return null;
            var set = _access.FindChallengeSet(name);
            if (set == null || _access.IsBounty(set))
                return null;
            return FactCodec.EncodeTrack(new TrackValue(name, index));
        }

        public bool InBounty()
        {
            var controller = MoveAndHitController.instance;
            return controller != null && controller.m_activeBountyPad != null;
        }

        public bool InLmugc()
        {
            var manager = SaveManager.instance;
            return manager != null && manager.m_run.isInLMUGC;
        }

        /// <summary>What this player is playing right now (its pad, the loaded and the main set, an LMUGC round).</summary>
        public LocalPlay CurrentPlay()
        {
            var controller = MoveAndHitController.instance;
            var pad = controller != null ? controller.m_activeBountyPad : null;
            string padSet = pad != null ? GameIds.ChallengeSetName(pad.m_bountyChallengeSet) : string.Empty;
            // A pad without a set still means "in a bounty".
            if (pad != null && padSet.Length == 0)
                padSet = "?";
            var system = ChallengeSystem.instance;
            string current = system != null ? GameIds.ChallengeSetName(system.m_challengeSet) : string.Empty;
            string main = system != null ? GameIds.ChallengeSetName(system.m_mainChallengeSet) : string.Empty;
            return new LocalPlay(padSet, current, main, InLmugc());
        }

        /// <summary>In a bounty, an LMUGC round or another set of its own (see <see cref="LocalPlay.InOwnChallenge"/>).</summary>
        public bool InOwnChallenge() => CurrentPlay().InOwnChallenge;

        /// <summary>
        /// Other players' pre-hit targets recorded while this player was in a challenge of its own; part of the live
        /// pht value (so the session's union keeps them) until <see cref="FactApplier.ResolvePreHit"/> hands them to
        /// the game.
        /// </summary>
        public HashSet<string> DeferredPreHit { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Answers 143 (likely, to confirm at runtime): the LMUGC round's set is a bounty set, so LoadNewChallengeSet
        /// keeps the story track as m_mainChallengeSet and only m_challengeSet is the LMUGC set; ChallengeSystem.Update
        /// returns early while isInLMUGC. Either way is handled (an LMUGC set as main reads as no track); logged once.
        /// </summary>
        private void NoteLmugcSets(ChallengeSystem system)
        {
            if (_lmugcNoted)
                return;
            var lmugc = _access.LmugcChallengeSet();
            if (lmugc == null)
                return;
            _lmugcNoted = true;
            bool mainIsLmugc = system.m_mainChallengeSet != null && IsLmugcSet(system.m_mainChallengeSet);
            bool currentIsLmugc = system.m_challengeSet != null && IsLmugcSet(system.m_challengeSet);
            _ctx.Info($"[Progression] LMUGC round: LMUGCChallenge '{GameIds.ChallengeSetName(lmugc)}' isBounty={_access.IsBounty(lmugc)}; "
                + $"m_challengeSet is it: {currentIsLmugc}; m_mainChallengeSet {(mainIsLmugc ? "is the LMUGC set (no shared track readable)" : "kept the story track '" + (system.m_mainChallengeSet != null ? GameIds.ChallengeSetName(system.m_mainChallengeSet) : "none") + "'")}.");
        }

        private bool IsLmugcSet(ChallengeSet set)
        {
            var lmugc = _access.LmugcChallengeSet();
            return lmugc != null && (set == lmugc || GameIds.ChallengeSetName(lmugc) == GameIds.ChallengeSetName(set));
        }

        // ---- Whole collections ----------------------------------------------------------------------

        /// <summary>Every fact of the live world. <paramref name="hostFacts"/> adds the host-only kinds.</summary>
        public void CollectWorld(FactTable into, bool hostFacts, int? seed, byte moneyState)
        {
            var manager = SaveManager.instance;
            if (manager == null)
                return;
            ref RunSave run = ref manager.m_run;

            if (TryGetTrack(out var track, out _))
                Put(into, FactKinds.Track, FactKinds.TrackKey, FactCodec.EncodeTrack(track));
            CollectLists(into, ref run);
            CollectRunFields(into, ref run, hostFacts);
            CollectObjects(into);
            Put(into, FactKinds.PreHit, string.Empty, PreHitValue(ref run));

            if (!hostFacts)
                return;
            var bank = _access.Bank();
            if (bank != null)
                Put(into, FactKinds.Bank, string.Empty, FactTable.EncodeInt(bank.m_balance));
            CollectUnlocksAndCard(into, manager);
            CollectAuthenticators(into);
            CollectSessionFacts(into, seed, moneyState);
        }

        /// <summary>
        /// Host outside "Main": the facts the loaded RunSave holds. World-only facts (track, objects, codes) are
        /// kept from <paramref name="current"/>, the session's table, as the menu cannot see the world.
        /// </summary>
        public void CollectRun(FactTable into, FactTable current, int? seed, byte moneyState)
        {
            var manager = SaveManager.instance;
            if (manager == null)
            {
                into.ReplaceWith(current);
                CollectSessionFacts(into, seed, moneyState);
                return;
            }
            ref RunSave run = ref manager.m_run;

            var trackValue = current?.Get(FactKinds.Track, FactKinds.TrackKey);
            if (trackValue != null)
                Put(into, FactKinds.Track, FactKinds.TrackKey, trackValue);
            else if (!IsJustGolf && !string.IsNullOrEmpty(run.m_challengeSetLoaded) && run.m_currentChallenge >= 0)
                Put(into, FactKinds.Track, FactKinds.TrackKey,
                    FactCodec.EncodeTrack(new TrackValue(GameIds.Clean(run.m_challengeSetLoaded), run.m_currentChallenge)));

            CollectLists(into, ref run);
            CollectRunFields(into, ref run, true);
            Put(into, FactKinds.Bank, string.Empty, FactTable.EncodeInt(run.m_bankBalance));
            Put(into, FactKinds.PreHit, string.Empty, FactCodec.EncodeStringSet(run.m_preHitTargets));

            var objects = current?.GetEntries(FactKinds.Obj);
            if (objects != null && objects.Count > 0)
            {
                foreach (var entry in objects)
                    Put(into, entry.Kind, entry.Key, entry.Value);
            }
            else if (run.m_savedObjects != null)
            {
                foreach (var data in run.m_savedObjects)
                {
                    if (data == null || string.IsNullOrEmpty(data.m_id))
                        continue;
                    var rotation = ToNet(data.m_rotation);
                    bool hasRotation = FactCodec.AngleDegrees(rotation, NetQuaternion.Identity) > FactCodec.RotationToleranceDegrees;
                    Put(into, FactKinds.Obj, data.m_id, FactCodec.EncodeObj(new ObjState(data.m_active, hasRotation, rotation)));
                }
            }
            if (current != null)
            {
                foreach (var entry in current.GetEntries(FactKinds.Auth))
                    Put(into, entry.Kind, entry.Key, entry.Value);
            }
            CollectUnlocksAndCard(into, manager);
            CollectSessionFacts(into, seed, moneyState);
        }

        // ---- Single facts ---------------------------------------------------------------------------

        /// <summary>The live value of one fact in "Main"; false when the game has none.</summary>
        public bool TryRead(string kind, string key, out byte[] value)
        {
            value = null;
            var manager = SaveManager.instance;
            if (manager == null || kind == null || key == null)
                return false;
            ref RunSave run = ref manager.m_run;
            switch (kind)
            {
                case FactKinds.Track:
                    if (key != FactKinds.TrackKey || !TryGetTrack(out var track, out _))
                        return false;
                    value = FactCodec.EncodeTrack(track);
                    return true;
                case FactKinds.Bounty:
                    if (!Contains(run.m_completedBounties, key))
                        return false;
                    value = FactCodec.EncodeFlags(BountyFlags(ref run, key));
                    return true;
                case FactKinds.DoNotReload:
                    if (!Contains(run.m_doNotReloadChallengeList, key))
                        return false;
                    value = FactCodec.EncodeFlags(1);
                    return true;
                case FactKinds.Easy:
                    if (!Contains(run.m_completedOnEasy, key) || Contains(run.m_completedBounties, key))
                        return false;
                    value = FactCodec.EncodeFlags(1);
                    return true;
                case FactKinds.RunBool:
                    if (!SharedRunFields.IsSharedBool(key) || !RunFields.TryGetBool(ref run, key, out bool flag))
                        return false;
                    value = FactTable.EncodeBool(flag);
                    return true;
                case FactKinds.RunInt:
                case FactKinds.RunHostInt:
                    if (!(SharedRunFields.IsSharedInt(key) || SharedRunFields.IsHostInt(key))
                        || !RunFields.TryGetInt(ref run, key, out int number))
                        return false;
                    value = FactTable.EncodeInt(number);
                    return true;
                case FactKinds.Bank:
                    var bank = _access.Bank();
                    if (bank == null)
                        return false;
                    value = FactTable.EncodeInt(bank.m_balance);
                    return true;
                case FactKinds.Obj:
                    var saveable = GameIds.ResolveSaveable(key);
                    return saveable != null && TryReadObj(saveable, out value);
                case FactKinds.PreHit:
                    value = PreHitValue(ref run);
                    return true;
                case FactKinds.Unlocks:
                    value = FactCodec.EncodeBoolList(manager.m_unlocks.unlockedBalls);
                    return true;
                case FactKinds.ScoreCard:
                    value = ScoreCardValue(manager);
                    return value != null;
                case FactKinds.Auth:
                    var authenticator = GameIds.Resolve<Authenticator>(key);
                    string code = authenticator != null ? ReadCode(authenticator) : null;
                    if (code == null)
                        return false;
                    value = FactTable.EncodeString(code);
                    return true;
                default:
                    return false;
            }
        }

        public bool TryReadObj(SaveableObject saveable, out byte[] value)
        {
            value = null;
            if (saveable == null)
                return false;
            var data = saveable.GetSaveData();
            if (data == null)
                return false;
            value = FactCodec.EncodeObj(new ObjState(data.m_active, saveable.m_saveRotation, ToNet(data.m_rotation)));
            return true;
        }

        /// <summary>The Authenticator's code (public string, made by NewCode from Start; answers 21), or null.</summary>
        public static string ReadCode(Authenticator authenticator) =>
            authenticator == null || string.IsNullOrEmpty(authenticator.code) ? null : authenticator.code;

        public static NetQuaternion ToNet(Quaternion q) => new NetQuaternion(q.x, q.y, q.z, q.w);

        public static Quaternion ToUnity(NetQuaternion q) => new Quaternion(q.x, q.y, q.z, q.w);

        /// <summary>The scorecard changed (SaveManager.AddScoreToCard ran): read it again on the next collect.</summary>
        public void InvalidateScoreCard() => _cardValue = null;

        // ---- Internals ------------------------------------------------------------------------------

        private void CollectLists(FactTable into, ref RunSave run)
        {
            if (run.m_completedBounties != null)
            {
                foreach (var name in run.m_completedBounties)
                {
                    if (!string.IsNullOrEmpty(name))
                        Put(into, FactKinds.Bounty, name, FactCodec.EncodeFlags(BountyFlags(ref run, name)));
                }
            }
            if (run.m_completedOnEasy != null)
            {
                foreach (var name in run.m_completedOnEasy)
                {
                    if (!string.IsNullOrEmpty(name) && !Contains(run.m_completedBounties, name))
                        Put(into, FactKinds.Easy, name, FactCodec.EncodeFlags(1));
                }
            }
            if (run.m_doNotReloadChallengeList != null)
            {
                foreach (var name in run.m_doNotReloadChallengeList)
                {
                    if (!string.IsNullOrEmpty(name))
                        Put(into, FactKinds.DoNotReload, name, FactCodec.EncodeFlags(1));
                }
            }
        }

        private void CollectRunFields(FactTable into, ref RunSave run, bool hostFacts)
        {
            foreach (var name in SharedRunFields.Bools)
            {
                if (RunFields.TryGetBool(ref run, name, out bool value))
                    Put(into, FactKinds.RunBool, name, FactTable.EncodeBool(value));
            }
            foreach (var name in SharedRunFields.Ints)
            {
                if (RunFields.TryGetInt(ref run, name, out int value))
                    Put(into, FactKinds.RunInt, name, FactTable.EncodeInt(value));
            }
            if (!hostFacts)
                return;
            foreach (var name in SharedRunFields.HostInts)
            {
                if (RunFields.TryGetInt(ref run, name, out int value))
                    Put(into, FactKinds.RunHostInt, name, FactTable.EncodeInt(value));
            }
        }

        private void CollectObjects(FactTable into)
        {
            RefreshExclusions();
            foreach (var pair in GameIds.Saveables)
            {
                if (pair.Value == null || _excludedObjs.Contains(pair.Key))
                    continue;
                try
                {
                    if (TryReadObj(pair.Value, out var value))
                        Put(into, FactKinds.Obj, pair.Key, value);
                }
                catch (Exception e)
                {
                    _ctx.WarnOnce("pg.obj.read:" + pair.Key, $"[Progression] Reading SaveableObject {pair.Key} failed: {e.Message}");
                }
            }
        }

        /// <summary>Objects whose state is per player's view (the elevator's bunker) are not shared.</summary>
        public bool IsExcluded(string objId)
        {
            RefreshExclusions();
            return objId != null && _excludedObjs.Contains(objId);
        }

        private void RefreshExclusions()
        {
            if (_excludedScene == _ctx.SceneLoadId)
                return;
            _excludedScene = _ctx.SceneLoadId;
            _excludedObjs.Clear();
            try
            {
                var bunker = _access.ElevatorBunker();
                var saveable = bunker != null ? bunker.GetComponent<SaveableObject>() : null;
                if (saveable != null && !string.IsNullOrEmpty(saveable.m_id))
                    _excludedObjs.Add(saveable.m_id);
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("pg.elevator", $"[Progression] Reading the elevator's bunker object failed: {e.Message}");
            }
        }

        private void CollectUnlocksAndCard(FactTable into, SaveManager manager)
        {
            Put(into, FactKinds.Unlocks, FactKinds.UnlocksKey, FactCodec.EncodeBoolList(manager.m_unlocks.unlockedBalls));
            var card = ScoreCardValue(manager);
            if (card != null)
                Put(into, FactKinds.ScoreCard, string.Empty, card);
        }

        private void CollectAuthenticators(FactTable into)
        {
            // Listed once per world: the host diffs every second, and a full scene scan each time costs frames.
            if (_authenticators == null || _authenticatorScene != _ctx.SceneLoadId)
            {
                try
                {
                    _authenticators = Compat.FindAll<Authenticator>(true);
                    _authenticatorScene = _ctx.SceneLoadId;
                }
                catch (Exception e)
                {
                    _authenticators = null;
                    _ctx.WarnOnce("pg.auth.find", $"[Progression] Listing Authenticators failed: {e.Message}");
                    return;
                }
            }
            foreach (var authenticator in _authenticators)
            {
                if (authenticator == null)
                    continue;
                string code = ReadCode(authenticator);
                if (code != null)
                    Put(into, FactKinds.Auth, GameIds.ForObject(authenticator), FactTable.EncodeString(code));
            }
        }

        private static void CollectSessionFacts(FactTable into, int? seed, byte moneyState)
        {
            if (seed.HasValue)
                into.Set(FactKinds.Seed, string.Empty, FactTable.EncodeInt(seed.Value));
            into.Set(FactKinds.Money, FactKinds.MoneyKey, FactCodec.EncodeFlags(moneyState));
        }

        private byte[] ScoreCardValue(SaveManager manager)
        {
            // Serialized again only when the card changed (AddScoreToCard, a new card loaded or applied), and at
            // least every few seconds in case the game edits an entry in place.
            var scores = manager.m_scoreCardSave.scores;
            int count = scores != null ? scores.Count : -1;
            double now = _ctx.Now;
            if (_cardValue != null && ReferenceEquals(_cardManager, manager) && ReferenceEquals(_cardScores, scores) && _cardCount == count
                && now - _cardAt < CardMaxAgeSeconds)
                return _cardValue;
            _cardAt = now;
            _cardManager = manager;
            _cardScores = scores;
            _cardCount = count;
            _cardValue = SerializeScoreCard(manager);
            return _cardValue;
        }

        private byte[] SerializeScoreCard(SaveManager manager)
        {
            string json;
            try
            {
                json = JsonUtility.ToJson(manager.m_scoreCardSave);
            }
            catch (Exception e)
            {
                _ctx.WarnOnce("pg.sc", $"[Progression] Serializing the scorecard failed: {e.Message}");
                return null;
            }
            var bytes = Utf8.GetBytes(json ?? string.Empty);
            if (bytes.Length > SaveDelta.MaxValueBytes)
            {
                _ctx.WarnOnce("pg.sc.size", $"[Progression] The scorecard ({bytes.Length} bytes) is too large to share.");
                return null;
            }
            return bytes;
        }

        private byte[] PreHitValue(ref RunSave run)
        {
            _scratch.Clear();
            if (run.m_preHitTargets != null)
                _scratch.UnionWith(run.m_preHitTargets);
            var system = ChallengeSystem.instance;
            if (system != null && system.m_preBrokenShots != null)
                _scratch.UnionWith(system.m_preBrokenShots);
            _scratch.UnionWith(DeferredPreHit);
            _scratch.Remove(null);
            var value = FactCodec.EncodeStringSet(_scratch);
            _scratch.Clear();
            return value;
        }

        private static byte BountyFlags(ref RunSave run, string name) =>
            (byte)(FactCodec.BountyDone | (Contains(run.m_completedOnEasy, name) ? FactCodec.BountyOnEasy : 0));

        private static bool Contains(List<string> list, string value) => list != null && list.Contains(value);

        private void Put(FactTable into, string kind, string key, byte[] value)
        {
            try
            {
                into.Set(kind, key, value);
            }
            catch (ArgumentException e)
            {
                _ctx.WarnOnce("pg.fact.size:" + kind + "/" + key, $"[Progression] Fact {kind}/{key} cannot be shared: {e.Message}");
            }
        }
    }
}
