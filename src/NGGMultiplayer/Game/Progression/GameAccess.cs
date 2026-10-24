using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NGGMultiplayer.Game.Progression
{
    /// <summary>
    /// Game members the stubs mark ACCESS-GUESS / SIG-GUESS / TYPE-GUESS (or that are private), reached by name so
    /// the plugin compiles and runs whatever their real access is. Resolved once at install; a missing member
    /// disables the feature that needs it (one log line) and the accessor then returns a neutral value. Members the
    /// decompiled game confirmed public (docs/confirm answers 10, 12, 18, 24, 77, 87, 91) are used directly.
    /// </summary>
    internal sealed class GameAccess
    {
        public const string TrackFeature = "pg.track";
        public const string BountyFeature = "pg.bounty";
        public const string BountyPayFeature = "pg.bounty.pay";
        public const string BountyBoardsFeature = "pg.bounty.boards";
        public const string BankFeature = "pg.bank";
        public const string ObjectStatesFeature = "pg.objectStates";
        public const string ChallengeTextFeature = "pg.challenge.text";
        public const string ClubPanelFeature = "pg.unlock.clubPanel";
        public const string StreakFeature = "pg.track.streak";
        public const string ActiveFeature = "pg.track.active";
        public const string SpinFeature = "pg.unlock.spin";
        public const string ElevatorFeature = "pg.elevator";
        public const string LmugcSetFeature = "pg.lmugc.set";

        private readonly GameContext _ctx;
        private AccessTools.FieldRef<ChallengeSet, Challenge[]> _challenges;
        private AccessTools.FieldRef<ChallengeSet, bool> _isBounty;
        private AccessTools.FieldRef<Challenge, bool> _complete;
        private AccessTools.FieldRef<Challenge, string> _shortString;
        private AccessTools.FieldRef<Challenge, string> _functionOnStart;
        private AccessTools.FieldRef<Challenge, string> _functionOnComplete;
        private AccessTools.FieldRef<Challenge, bool> _active;
        private AccessTools.FieldRef<Challenge, int> _streaksRequired;
        private FieldInfo _difficulty;
        private AccessTools.FieldRef<ProgressManager, ObjectStates> _objectStates;
        private MethodInfo _spinUnlock;
        private FieldInfo _lmugcChallenge;
        private int _assetCacheScene = -1;
        private readonly Dictionary<string, ChallengeSet> _assetCache = new Dictionary<string, ChallengeSet>(StringComparer.Ordinal);
        private int _clubCacheScene = -1;
        private Club[] _clubs;

        public GameAccess(GameContext ctx)
        {
            _ctx = ctx;
        }

        private GamePatcher Patcher => _ctx.Patcher;

        public void Resolve()
        {
            _challenges = Patcher.FieldRef<ChallengeSet, Challenge[]>(TrackFeature, "m_challenges");
            _complete = Patcher.FieldRef<Challenge, bool>(TrackFeature, "m_complete");
            _isBounty = Patcher.FieldRef<ChallengeSet, bool>(BountyFeature, "isBounty");
            if (_challenges == null)
                Patcher.Disable(BountyFeature, "ChallengeSet.m_challenges is missing");

            _shortString = Patcher.FieldRef<Challenge, string>(ChallengeTextFeature, "m_shortString");
            _functionOnStart = Patcher.FieldRef<Challenge, string>(ChallengeTextFeature, "functionOnStart");
            _functionOnComplete = Patcher.FieldRef<Challenge, string>(ChallengeTextFeature, "functionOnComplete");
            // The catch-up of the shared track (FactApplier): which challenge the game activated, and the streak gate.
            _active = Patcher.FieldRef<Challenge, bool>(ActiveFeature, "m_active");
            _streaksRequired = Patcher.FieldRef<Challenge, int>(StreakFeature, "m_streaksRequired");

            _difficulty = Patcher.Field(BountyPayFeature, typeof(ChallengeSet), "difficulty");
            _objectStates = Patcher.FieldRef<ProgressManager, ObjectStates>(ObjectStatesFeature, "m_objectStates");
            _spinUnlock = Patcher.Method(SpinFeature, typeof(Spin), "Unlock", new[] { typeof(bool) });
            _lmugcChallenge = Patcher.Field(LmugcSetFeature, typeof(LMUGC), "LMUGCChallenge");
        }

        public bool TrackEnabled => _challenges != null && _complete != null && Patcher.IsEnabled(TrackFeature);

        public bool BountyEnabled => _challenges != null && _isBounty != null && Patcher.IsEnabled(BountyFeature);

        // ---- ChallengeSet / Challenge ---------------------------------------------------------------

        public Challenge[] Challenges(ChallengeSet set)
        {
            if (set == null || _challenges == null)
                return null;
            return _challenges(set);
        }

        public int ChallengeCount(ChallengeSet set) => Challenges(set)?.Length ?? 0;

        public bool IsBounty(ChallengeSet set) => set != null && _isBounty != null && _isBounty(set);

        public bool IsComplete(Challenge challenge) => challenge != null && _complete != null && _complete(challenge);

        /// <summary>The game activated this challenge (RefreshChallenges) and it is still to do.</summary>
        public bool IsActiveIncomplete(Challenge challenge) =>
            challenge != null && _active != null && _active(challenge) && !IsComplete(challenge);

        /// <summary>A challenge of the set is active and incomplete; null when that cannot be read.</summary>
        public bool? HasActiveChallenge(ChallengeSet set)
        {
            var challenges = Challenges(set);
            if (challenges == null || _active == null)
                return null;
            foreach (var challenge in challenges)
            {
                if (IsActiveIncomplete(challenge))
                    return true;
            }
            return false;
        }

        /// <summary>The challenge's m_streaksRequired (1 when it cannot be read).</summary>
        public int StreaksRequired(Challenge challenge) =>
            challenge == null || _streaksRequired == null || !Patcher.IsEnabled(StreakFeature) ? 1 : _streaksRequired(challenge);

        /// <summary>StreaksPanel.instance.m_streak.m_currentStreak, read and written by name; null when unreadable.</summary>
        public int? CurrentStreak
        {
            get
            {
                var streak = StreakField();
                if (streak == null)
                    return null;
                object value = streak.GetValue();
                return value is int number ? number : (int?)null;
            }
        }

        public bool SetCurrentStreak(int value)
        {
            var field = StreakField();
            if (field == null || field.GetValueType() != typeof(int))
                return false;
            field.SetValue(value);
            return true;
        }

        private Traverse StreakField()
        {
            var panel = StreaksPanel.instance;
            if (panel == null || panel.m_streak == null || !Patcher.IsEnabled(StreakFeature))
                return null;
            var field = Traverse.Create(panel.m_streak).Field("m_currentStreak");
            return field.FieldExists() ? field : null;
        }

        public string ShortString(Challenge challenge) =>
            challenge == null || _shortString == null ? string.Empty : _shortString(challenge) ?? string.Empty;

        /// <summary>The challenge's functionOnStart / functionOnComplete equals <paramref name="method"/>.</summary>
        public bool UsesFunction(Challenge challenge, string method)
        {
            if (challenge == null || _functionOnStart == null || _functionOnComplete == null)
                return false;
            return string.Equals(_functionOnStart(challenge), method, StringComparison.Ordinal)
                || string.Equals(_functionOnComplete(challenge), method, StringComparison.Ordinal);
        }

        /// <summary>Index of the first incomplete challenge (or the length when all are done); -1 without challenges.</summary>
        public int FirstIncomplete(ChallengeSet set)
        {
            var challenges = Challenges(set);
            if (challenges == null)
                return -1;
            for (int i = 0; i < challenges.Length; i++)
            {
                if (challenges[i] != null && !IsComplete(challenges[i]))
                    return i;
            }
            return challenges.Length;
        }

        /// <summary>The challenge set asset with this clean name, or null.</summary>
        public ChallengeSet FindChallengeSet(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (_assetCacheScene != _ctx.SceneLoadId)
            {
                _assetCache.Clear();
                _assetCacheScene = _ctx.SceneLoadId;
            }
            if (_assetCache.TryGetValue(name, out var cached) && cached != null)
                return cached;
            var found = LookUpChallengeSet(name);
            if (found != null)
                _assetCache[name] = found;
            return found;
        }

        private ChallengeSet LookUpChallengeSet(string name)
        {
            // GreatConnector.GetChallengeSet compares the same clean names over m_allChallenges, but logs an error
            // for a name it does not know (answers 12, 87); claims bring arbitrary names, so the list is searched
            // here instead. The loaded assets are the fallback.
            var listed = GameIds.FindConnectorSet(name);
            if (listed != null)
                return listed;
            foreach (var set in Resources.FindObjectsOfTypeAll<ChallengeSet>())
            {
                // Prefer the asset over a runtime copy.
                if (set != null && !set.name.EndsWith("(Clone)", StringComparison.Ordinal) && GameIds.ChallengeSetName(set) == name)
                    return set;
            }
            return null;
        }

        /// <summary>The reward the game pays for a bounty set, or null when it cannot be computed.</summary>
        /// <remarks>The same on easy mode (answers 36): easy only also records m_completedOnEasy.</remarks>
        public int? BountyReward(ChallengeSet set)
        {
            var balancer = BountyBalancer.instance;
            if (set == null || balancer == null || _difficulty == null || !Patcher.IsEnabled(BountyPayFeature))
                return null;
            try
            {
                if (!(_difficulty.GetValue(set) is Difficulty difficulty))
                    return null;
                return balancer.GetDifficultyValue(difficulty);
            }
            catch (Exception e)
            {
                Patcher.ReportFailure(BountyPayFeature, e);
                return null;
            }
        }

        public void UpdateBountyBoards()
        {
            var balancer = BountyBalancer.instance;
            if (balancer == null || !Patcher.IsEnabled(BountyBoardsFeature))
                return;
            try
            {
                balancer.UpdateBountyBoards();
            }
            catch (Exception e)
            {
                Patcher.ReportFailure(BountyBoardsFeature, e);
            }
        }

        /// <summary>The LMUGC round's own challenge set (per player, never the shared track), or null.</summary>
        public ChallengeSet LmugcChallengeSet()
        {
            var panel = PanelManager.instance != null ? PanelManager.instance.m_LMUGCPanel : null;
            if (panel == null || _lmugcChallenge == null)
                return null;
            return _lmugcChallenge.GetValue(panel) as ChallengeSet;
        }

        // ---- ProgressManager ------------------------------------------------------------------------

        public Bank Bank()
        {
            var manager = ProgressManager.instance;
            return manager == null || !Patcher.IsEnabled(BankFeature) ? null : manager.m_bank;
        }

        public ObjectStates ObjectStates()
        {
            var manager = ProgressManager.instance;
            return manager == null || _objectStates == null ? null : _objectStates(manager);
        }

        // ---- Panels ---------------------------------------------------------------------------------

        /// <summary>
        /// PanelManager.instance.m_clubPanel.Unlock(animate): the club panel, which goes with RunSave.m_putterUnlocked
        /// (SetUpScene, buying the putter, CompletedDemo; answers 37, 94). Idempotent apart from its PFX.
        /// </summary>
        public bool UnlockClubPanel(bool animate)
        {
            var panels = PanelManager.instance;
            if (panels == null || panels.m_clubPanel == null || !Patcher.IsEnabled(ClubPanelFeature))
                return false;
            panels.m_clubPanel.Unlock(animate);
            return true;
        }

        public bool UnlockSpin(bool showPfx)
        {
            var panels = PanelManager.instance;
            if (panels == null || panels.m_spinPanel == null || _spinUnlock == null || !Patcher.IsEnabled(SpinFeature))
                return false;
            _spinUnlock.Invoke(panels.m_spinPanel, new object[] { showPfx });
            return true;
        }

        // ---- Clubs ----------------------------------------------------------------------------------

        public Club FindClub(string clubName)
        {
            if (_clubCacheScene != _ctx.SceneLoadId || _clubs == null)
            {
                _clubs = Resources.FindObjectsOfTypeAll<Club>();
                _clubCacheScene = _ctx.SceneLoadId;
            }
            Club loose = null;
            foreach (var club in _clubs)
            {
                if (club == null)
                    continue;
                if (string.Equals(club.clubName, clubName, StringComparison.Ordinal))
                    return club;
                if (loose == null && string.Equals(club.clubName, clubName, StringComparison.OrdinalIgnoreCase))
                    loose = club;
            }
            return loose;
        }

        // ---- Elevator -------------------------------------------------------------------------------

        /// <summary>
        /// The elevator's m_bunker object (GreatConnector.instance.m_elevator; there is no ElevatorManager), which
        /// RideElevator toggles for that player's view only (answers 24, 91), or null.
        /// </summary>
        public GameObject ElevatorBunker()
        {
            var connector = GreatConnector.instance;
            if (connector == null || connector.m_elevator == null || !Patcher.IsEnabled(ElevatorFeature))
                return null;
            return connector.m_elevator.m_bunker;
        }
    }
}
