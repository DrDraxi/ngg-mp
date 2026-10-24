using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Lifecycle;
using NGGMultiplayer.Game.Progression;
using NGGMultiplayer.Runtime;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// <see cref="IHarnessGame"/> over the running plugin and the live game. Reads only: the harness never changes the
    /// game's state itself (the bots do that through the network, like real clients). Private game members are read by
    /// name (AccessTools); every read tolerates a missing member, scene or object.
    /// </summary>
    internal sealed class HarnessGame : IHarnessGame
    {
        private const string RemoteRootName = "NGGMultiplayer Remotes";
        private const float GroundProbeUp = 1.5f;
        private const float GroundProbeDepth = 6f;

        private static readonly FieldInfo DesiredWindField = AccessTools.Field(typeof(WindPanel), "m_desiredWind");
        private static readonly FieldInfo IsBountyField = AccessTools.Field(typeof(ChallengeSet), "isBounty");
        private static readonly FieldInfo ChallengesField = AccessTools.Field(typeof(ChallengeSet), "m_challenges");
        private static readonly FieldInfo DoorsField = AccessTools.Field(typeof(ObjectStates), "m_doors");
        private static readonly FieldInfo SlidingDoorsField = AccessTools.Field(typeof(ObjectStates), "m_slidingDoors");
        private static readonly FieldInfo GatesField = AccessTools.Field(typeof(ObjectStates), "m_gates");
        private static readonly FieldInfo ObjectStatesField = AccessTools.Field(typeof(ProgressManager), "m_objectStates");
        private static readonly MethodInfo CurrentChallengeMethod = AccessTools.Method(typeof(ChallengeSystem), "GetCurrentChallenge", Type.EmptyTypes);

        private readonly IMultiplayerService _service;
        private readonly IGameAdapter _adapter;
        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private readonly Func<float, float, float, float> _ground;

        public HarnessGame(IMultiplayerService service, IGameAdapter adapter)
        {
            _service = service;
            _adapter = adapter;
            _ground = Ground;
        }

        // ---- Service ------------------------------------------------------------------------------------

        public string ServiceState => _service != null ? _service.State.ToString() : "none";

        public bool InSession => _service?.Net != null && _service.Net.IsActive;

        public bool IsHosting => _service != null && _service.IsHost;

        public PeerId LocalId => _service?.Net?.LocalId ?? PeerId.None;

        public PeerId HostId => InSession ? _service.Net.Session.HostId : PeerId.None;

        public ulong LobbyId => _service?.LobbyId ?? 0;

        public string StatusMessage => _service?.StatusMessage ?? string.Empty;

        public IReadOnlyList<PlayerInfo> Players => _service != null ? _service.Players : (IReadOnlyList<PlayerInfo>)Array.Empty<PlayerInfo>();

        public bool IsPeerReady(PeerId peer) => _service?.Net != null && _service.Net.IsPeerReady(peer);

        public bool Host(out string message)
        {
            if (_service == null || !_service.IsAvailable)
            {
                message = _service?.UnavailableReason ?? "no multiplayer service";
                return false;
            }
            bool ok = _service.Host();
            message = _service.StatusMessage;
            return ok;
        }

        public void Leave() => _service?.Leave();

        public FakePeerOptions BotTemplate()
        {
            var options = new FakePeerOptions { ShotType = ShotTypeByName("Perfect") };
            var net = _service?.Net;
            if (net != null)
            {
                var config = net.Session.Config;
                options.GameBuild = config.GameBuild ?? string.Empty;
                options.TimeoutSeconds = config.TimeoutSeconds;
                if (net.Options.Entities != null)
                    options.SendRate = net.Options.Entities.SendRate;
            }
            return options;
        }

        // ---- World --------------------------------------------------------------------------------------

        public bool TryGetHostPose(out NetVector3 feet)
        {
            feet = NetVector3.Zero;
            var ctx = GameContext.Current;
            if (ctx != null && (ctx.Scene != GameScene.Main || !ctx.WorldReady))
                return false;
            if (_adapter == null || !_adapter.TryGetLocalPlayerPose(out var pose) || pose.Hidden)
                return false;
            // Answers #55: ECM2's GetPosition is at the feet; in golf mode Presence sends the stance spot beside the ball.
            feet = new NetVector3(pose.Position.x, pose.Position.y, pose.Position.z);
            return feet.IsFinite;
        }

        public Func<float, float, float, float> GroundHeight => _ground;

        private float Ground(float x, float z, float fallback)
        {
            var origin = new Vector3(x, fallback + GroundProbeUp, z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, GroundProbeUp + GroundProbeDepth, ~0, QueryTriggerInteraction.Ignore);
            Transform player = MoveAndHitController.instance != null ? MoveAndHitController.instance.transform : null;
            float best = float.NaN;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                // Not the host's own golfer (its capsule would put a bot on the host's head).
                if (hit.collider == null || (player != null && hit.collider.transform.IsChildOf(player)))
                    continue;
                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    best = hit.point.y;
                }
            }
            return float.IsNaN(best) ? fallback : best;
        }

        public FactTable SessionFacts => GameContext.Current?.Facts;

        public void DescribeWorld(JsonBuilder json)
        {
            var ctx = GameContext.Current;
            json.Value("unityScene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
            json.Value("integration", ctx != null);
            if (ctx != null)
            {
                json.Value("scene", ctx.Scene.ToString());
                json.Value("sceneLoadId", ctx.SceneLoadId);
                json.Value("worldReady", ctx.WorldReady);
                json.Value("loadingWorld", ctx.IsLoadingWorld);
                json.Value("worldReplacePending", ctx.WorldReplacePending);
                json.Value("pauseMenuOpen", ctx.PauseMenuOpen);
                json.Value("replaying", ctx.Replay.IsReplaying);
            }
            // Answers #46/#82/#113: only PlayerPressedEscape.OpenMenu (and the debug Screenshotter) freeze time.
            json.Value("timeScale", Time.timeScale);
            json.Value("hostPose", TryGetHostPose(out var feet) ? feet.ToString() : null);

            var manager = SaveManager.instance;
            json.Value("saveManager", manager != null);
            if (manager != null)
            {
                json.Value("mode", manager.m_gamemodeState != null ? manager.m_gamemodeState.mode.ToString() : null);
                var run = manager.m_run;
                json.Value("runChallengeSet", run.m_challengeSetLoaded);
                json.Value("runChallenge", run.m_currentChallenge);
                json.Value("runBank", run.m_bankBalance);
                json.Value("runCompletedBounties", run.m_completedBounties != null ? run.m_completedBounties.Count : -1);
                json.Value("runIsInLMUGC", run.isInLMUGC);
            }
            var progress = ProgressManager.instance;
            var bank = progress != null ? progress.m_bank : null;
            json.Value("liveBank", bank != null ? bank.m_balance : -1);
            var challenges = ChallengeSystem.instance;
            if (challenges != null)
            {
                json.Value("mainChallengeSet", challenges.m_mainChallengeSet != null ? GameIds.ChallengeSetName(challenges.m_mainChallengeSet) : null);
                json.Value("loadedChallengeSet", challenges.m_challengeSet != null ? GameIds.ChallengeSetName(challenges.m_challengeSet) : null);
            }

            // Answers #97: LukeVideo's trigger has no filter, so remote avatars and ghost balls must carry no collider
            // or rigidbody that could reach it.
            CountRemotes(out int views, out int colliders, out int bodies);
            json.Value("remoteViews", views);
            json.Value("remoteColliders", colliders);
            json.Value("remoteRigidbodies", bodies);
        }

        public void DescribeSaveGuard(JsonBuilder json)
        {
            var ctx = GameContext.Current;
            json.Value("suppressDiskWrites", ctx != null && ctx.Save.SuppressDiskWrites);
            json.Value("restorePending", _service?.Net != null && _service.Net.LocalSaveRestorePending);
            json.Value("writesBlocked", SessionSaveStore.WritesBlocked);
            json.Value("fenceUp", SessionSaveStore.FenceUp);
            json.Value("suppressedBySession", SessionSaveStore.SuppressedBySession);
            json.Value("protectInstalled", SavePatches.ProtectInstalled);
            json.Value("injectInstalled", SavePatches.InjectInstalled);
            if (ctx != null)
            {
                json.Value("protectEnabled", ctx.Patcher.IsEnabled(SavePatches.ProtectFeature));
                json.Value("injectEnabled", ctx.Patcher.IsEnabled(SavePatches.InjectFeature));
            }
            var module = LifecycleSaveModule.Instance;
            json.Value("lifecycle", module != null);
            if (module != null)
            {
                var store = module.Store;
                json.Value("sessionSaveActive", store != null && store.SessionSaveActive);
                json.Value("backupTaken", store != null && store.BackupTaken);
                var missing = module.MissingSafetyFeatures();
                json.Value("missingSafetyFeatures", missing != null && missing.Count > 0 ? string.Join(", ", missing) : string.Empty);
                json.Value("keepGuardsOnUnload", LifecycleSaveModule.KeepWriteGuardsOnUnload());
            }
        }

        public string DescribeWind()
        {
            var panels = PanelManager.instance;
            var panel = panels != null ? panels.m_windPanel : null;
            if (panel == null)
                return "no WindPanel (not in Main)";
            string desired = "unreadable";
            if (DesiredWindField != null && DesiredWindField.GetValue(panel) is Vector2 d)
                desired = Format(d);
            string forced = string.Empty;
            try
            {
                var challenges = ChallengeSystem.instance;
                var challenge = challenges != null && challenges.m_challengeSet != null && CurrentChallengeMethod != null
                    ? CurrentChallengeMethod.Invoke(challenges, null) as Challenge
                    : null;
                // Answers #44/#57/#64: a challenge's m_forceWind makes LateUpdate show m_wind * m_maxWindSpeed.
                if (challenge != null && challenge.m_forceWind)
                    forced = ", current challenge forces wind " + Format(challenge.m_wind);
            }
            catch (Exception e)
            {
                forced = ", challenge unreadable (" + e.GetType().Name + ")";
            }
            return $"current {Format(panel.m_currentWind)} desired {desired}{(panel.m_debugNoWind ? ", m_debugNoWind" : string.Empty)}{forced}";
        }

        public string SaveDirectory => Game.SaveDirOverride.Effective;

        public string ReadPlayerPref(string key) => PlayerPrefs.HasKey(key) ? PlayerPrefs.GetString(key, null) : null;

        // ---- Lookups ------------------------------------------------------------------------------------

        public bool TryResolvePickup(string interactId, out PickupRequest request, out string error)
        {
            request = null;
            error = null;
            if (!InMain(out error))
                return false;
            foreach (var interactable in Compat.FindAll<FPSInteractable>(true))
            {
                if (interactable == null || !Interactions.IsArbitrated(interactable))
                    continue;
                if (!string.Equals(Interactions.IdOf(interactable), interactId, StringComparison.Ordinal))
                    continue;
                if (!interactable.isActiveAndEnabled)
                    error = "it is not active on the host (already taken?)";
                request = new PickupRequest
                {
                    InteractId = interactId,
                    Interaction = (byte)(int)interactable.m_interaction,
                    CashValue = interactable.m_cashValue,
                    SaveableId = SaveableIdOf(interactable.transform)
                };
                return true;
            }
            error = "no arbitrated interactable has that id ('list pickups' shows them)";
            return false;
        }

        public bool TryGetChallengeSet(string name, out bool isBounty, out int challengeCount)
        {
            isBounty = false;
            challengeCount = 0;
            foreach (var set in AllChallengeSets())
            {
                if (GameIds.ChallengeSetName(set) != name)
                    continue;
                isBounty = IsBounty(set);
                challengeCount = (ChallengesField?.GetValue(set) as Array)?.Length ?? 0;
                return true;
            }
            return false;
        }

        public IReadOnlyList<string> List(string what, out string error)
        {
            error = null;
            var items = new List<string>();
            switch (what)
            {
                case "sets":
                case "bounties":
                    foreach (var set in AllChallengeSets())
                    {
                        bool bounty = IsBounty(set);
                        if (what == "bounties" && !bounty)
                            continue;
                        int count = (ChallengesField?.GetValue(set) as Array)?.Length ?? 0;
                        items.Add($"{GameIds.ChallengeSetName(set)} ({(bounty ? "bounty, " : string.Empty)}{count} challenges)");
                    }
                    if (items.Count == 0)
                        error = "GreatConnector.instance.m_allChallenges is empty (not in Main?)";
                    break;
                case "pickups":
                    if (!InMain(out error))
                        return null;
                    foreach (var interactable in Compat.FindAll<FPSInteractable>(true))
                    {
                        if (interactable == null || !Interactions.IsArbitrated(interactable))
                            continue;
                        string saveable = SaveableIdOf(interactable.transform);
                        items.Add($"{Interactions.IdOf(interactable)}  {interactable.m_interaction} m_cashValue {interactable.m_cashValue}" +
                                  $"{(interactable.isActiveAndEnabled ? string.Empty : " (inactive)")}{(saveable != null ? " on " + saveable : string.Empty)}");
                    }
                    items.Sort(StringComparer.Ordinal);
                    break;
                case "doors":
                case "gates":
                {
                    if (!InMain(out error))
                        return null;
                    var progress = ProgressManager.instance;
                    var states = progress != null && ObjectStatesField != null ? ObjectStatesField.GetValue(progress) as ObjectStates : null;
                    if (states == null)
                    {
                        error = "no ObjectStates";
                        return null;
                    }
                    if (what == "gates")
                        AddIds(items, "gate ", GatesField?.GetValue(states));
                    else
                    {
                        AddIds(items, "door ", DoorsField?.GetValue(states));
                        AddIds(items, "sliding ", SlidingDoorsField?.GetValue(states));
                    }
                    break;
                }
                case "saveables":
                    if (!InMain(out error))
                        return null;
                    foreach (var pair in GameIds.Saveables)
                    {
                        if (pair.Value != null)
                            items.Add($"{pair.Key}  {pair.Value.name}{(pair.Value.gameObject.activeSelf ? string.Empty : " (inactive)")}");
                    }
                    items.Sort(StringComparer.Ordinal);
                    break;
                default:
                    error = "unknown list '" + what + "'";
                    return null;
            }
            return items;
        }

        public string RunCheck(string name) => RuntimeChecks.Run(name);

        public IReadOnlyList<string> CheckNames => RuntimeChecks.Names;

        // ---- Helpers ------------------------------------------------------------------------------------

        internal static byte ShotTypeByName(string name)
        {
            try
            {
                if (Enum.IsDefined(typeof(ShotType), name))
                {
                    long value = Convert.ToInt64(Enum.Parse(typeof(ShotType), name), CultureInfo.InvariantCulture);
                    if (value >= 0 && value <= byte.MaxValue)
                        return (byte)value;
                }
            }
            catch (Exception)
            {
                // A changed enum: the shot is sent with type 0.
            }
            return 0;
        }

        internal static void CountRemotes(out int views, out int colliders, out int bodies)
        {
            views = colliders = bodies = 0;
            var root = GameObject.Find(RemoteRootName);
            if (root == null)
                return;
            views = root.transform.childCount;
            colliders = root.GetComponentsInChildren<Collider>(true).Length;
            bodies = root.GetComponentsInChildren<Rigidbody>(true).Length;
        }

        private static bool InMain(out string error)
        {
            var ctx = GameContext.Current;
            if (ctx != null && (ctx.Scene != GameScene.Main || !ctx.WorldReady))
            {
                error = "the host's world is not loaded";
                return false;
            }
            error = null;
            return true;
        }

        private static IEnumerable<ChallengeSet> AllChallengeSets()
        {
            var connector = GreatConnector.instance;
            var all = connector != null ? connector.m_allChallenges : null;
            if (all == null)
                yield break;
            foreach (var set in all)
            {
                if (set != null)
                    yield return set;
            }
        }

        private static bool IsBounty(ChallengeSet set) => IsBountyField != null && IsBountyField.GetValue(set) is bool b && b;

        private static string SaveableIdOf(Transform transform)
        {
            for (var t = transform; t != null; t = t.parent)
            {
                var saveable = t.GetComponent<SaveableObject>();
                if (saveable != null && !string.IsNullOrEmpty(saveable.m_id))
                    return saveable.m_id;
            }
            return null;
        }

        private static void AddIds(List<string> items, string prefix, object array)
        {
            if (!(array is IEnumerable entries))
                return;
            foreach (var entry in entries)
            {
                if (entry == null)
                    continue;
                string id = Traverse.Create(entry).Field("id").GetValue<string>();
                items.Add(prefix + id);
            }
        }

        private static string Format(Vector2 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###})", v.x, v.y);
    }
}
