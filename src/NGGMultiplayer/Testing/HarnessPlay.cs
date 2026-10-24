using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NGGMultiplayer.Core.Protocol;
using NGGMultiplayer.Game;
using NGGMultiplayer.Game.Progression;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Video;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// <see cref="IHarnessPlay"/> over the live game: the harness plays this copy's local player through the game's own
    /// code, so the multiplayer patches see it as real play (unlike the bots, which only talk to the network). Moves go
    /// through ECM2's Character.TeleportPosition, uses through FPSInteractable.Interact / StandHere.Activate /
    /// BountyPad.StartBounty / DialogueTrigger.OnTriggerEnter, shots through ClubSwing.HandleShot (the swing panel's
    /// own shot, minus the mouse gesture). Private members by name (AccessTools); every call tolerates a missing member,
    /// scene or object and answers with text. Main thread only; only created when the harness is enabled.
    /// </summary>
    internal sealed class HarnessPlay : IHarnessPlay
    {
        private const string RemoteRootName = "NGGMultiplayer Remotes";
        private const string ModPrefix = "NGGMP";
        private const float GroundProbeUp = 3f;
        private const float GroundProbeDepth = 40f;
        private const float FarFromTarget = 6f;
        private const float DefaultRadius = 0.5f;

        private static readonly MethodInfo HandleShotMethod = AccessTools.Method(typeof(ClubSwing), "HandleShot", new[] { typeof(ShotType), typeof(float) });
        private static readonly MethodInfo FpsTransitionMethod = AccessTools.Method(typeof(MoveAndHitController), "FPSTransition", Type.EmptyTypes);
        private static readonly FieldInfo FpsInputField = AccessTools.Field(typeof(MoveAndHitController), "m_fpsInput");
        private static readonly FieldInfo GoToFpsField = AccessTools.Field(typeof(MoveAndHitController), "_goToFPS");
        private static readonly FieldInfo TransitionImageField = AccessTools.Field(typeof(MoveAndHitController), "m_transitionImage");
        private static readonly MethodInfo CanSwitchFirstTimeMethod = AccessTools.Method(typeof(ProgressManager), "CanSwitchFirstTime", new[] { typeof(float) });
        private static readonly MethodInfo DoKillMethod = AccessTools.Method(AccessTools.TypeByName("DG.Tweening.ShortcutExtensions"), "DOKill",
            new[] { typeof(Component), typeof(bool) });
        private static readonly MethodInfo TriggerEnterMethod = AccessTools.Method(typeof(DialogueTrigger), "OnTriggerEnter", new[] { typeof(Collider) });
        private static readonly FieldInfo ObjectStatesField = AccessTools.Field(typeof(ProgressManager), "m_objectStates");
        private static readonly FieldInfo DoorsField = AccessTools.Field(typeof(ObjectStates), "m_doors");
        private static readonly FieldInfo SlidingDoorsField = AccessTools.Field(typeof(ObjectStates), "m_slidingDoors");
        private static readonly FieldInfo GateToOpenField = AccessTools.Field(typeof(FPSInteractable), "m_gateToOpen");
        private static readonly FieldInfo IsBountyField = AccessTools.Field(typeof(ChallengeSet), "isBounty");
        private static readonly FieldInfo ChallengesField = AccessTools.Field(typeof(ChallengeSet), "m_challenges");
        private static readonly FieldInfo ShortStringField = AccessTools.Field(typeof(Challenge), "m_shortString");
        private static readonly FieldInfo ActiveField = AccessTools.Field(typeof(Challenge), "m_active");
        private static readonly FieldInfo CompleteField = AccessTools.Field(typeof(Challenge), "m_complete");
        private static readonly MethodInfo NextButtonMethod = AccessTools.Method(typeof(DialogueManager), "NextButtonPressed", Type.EmptyTypes);

        private readonly MonoBehaviour _runner;
        private readonly RaycastHit[] _hits = new RaycastHit[32];

        public HarnessPlay(MonoBehaviour runner)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        }

        // ---- Objects ------------------------------------------------------------------------------------

        public IReadOnlyList<SceneObject> FindObjects(string category, out string error)
        {
            error = null;
            if (category != PlayArgs.AnyObject && !InWorld(out error))
                return null;
            var list = new List<SceneObject>();
            switch (category)
            {
                case PlayArgs.AnyObject:
                    AddAllObjects(list);
                    break;
                case PlayArgs.Interactable:
                    foreach (var interactable in Compat.FindAll<FPSInteractable>(false))
                    {
                        if (interactable == null || !interactable.isActiveAndEnabled)
                            continue;
                        list.Add(Make(category, interactable.transform, Interactions.IdOf(interactable), interactable.m_interaction.ToString(), interactable));
                    }
                    break;
                case PlayArgs.Tee:
                    foreach (var tee in Compat.FindAll<StandHere>(false))
                    {
                        if (tee != null && tee.isActiveAndEnabled)
                            list.Add(Make(category, tee.transform, tee.name, tee.m_playerInside ? "(player inside)" : string.Empty, tee));
                    }
                    break;
                case PlayArgs.Pad:
                    foreach (var pad in Compat.FindAll<BountyPad>(true))
                    {
                        if (pad == null)
                            continue;
                        string set = pad.m_bountyChallengeSet != null ? GameIds.ChallengeSetName(pad.m_bountyChallengeSet) : string.Empty;
                        var spot = pad.m_playerPosition != null ? pad.m_playerPosition : pad.transform;
                        string detail = (pad.m_completed ? "completed" : "open") + (IsPadLocked(pad) ? ", locked" : string.Empty) +
                                        (pad.gameObject.activeInHierarchy ? string.Empty : ", inactive");
                        list.Add(new SceneObject(category, pad.name, PathOf(pad.transform), set, ToNet(spot.position), detail, pad));
                    }
                    break;
                case PlayArgs.Trigger:
                    foreach (var trigger in Compat.FindAll<DialogueTrigger>(false))
                    {
                        if (trigger != null && trigger.isActiveAndEnabled)
                            list.Add(Make(category, trigger.transform, GameIds.ForObject(trigger), string.Empty, trigger));
                    }
                    break;
                case PlayArgs.Lock:
                    AddLocks(list);
                    break;
                case PlayArgs.Remote:
                {
                    var root = GameObject.Find(RemoteRootName);
                    if (root != null)
                    {
                        foreach (Transform child in root.transform)
                            list.Add(Make(category, child, child.name, child.gameObject.activeInHierarchy ? string.Empty : "(hidden)", child.gameObject));
                    }
                    break;
                }
                default:
                    error = "unknown category '" + category + "'";
                    return null;
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            return list;
        }

        private static void AddAllObjects(List<SceneObject> list)
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root == null || !root.activeInHierarchy)
                        continue;
                    foreach (var t in root.GetComponentsInChildren<Transform>(false))
                    {
                        var saveable = t.GetComponent<SaveableObject>();
                        string id = saveable != null && !string.IsNullOrEmpty(saveable.m_id) ? GameIds.SaveablePrefix + saveable.m_id : string.Empty;
                        list.Add(new SceneObject(PlayArgs.AnyObject, t.name, PathOf(t), id, ToNet(t.position), string.Empty, t.gameObject));
                    }
                }
            }
        }

        private static void AddLocks(List<SceneObject> list)
        {
            var states = ObjectStatesOrNull();
            if (states != null)
            {
                foreach (var gate in states.m_gates ?? Array.Empty<PicketGates>())
                {
                    if (gate != null && gate.gate1 != null)
                        list.Add(new SceneObject(PlayArgs.Lock, gate.gate1.name, PathOf(gate.gate1), gate.id, ToNet(gate.gate1.position),
                            "gate " + LockText.Name(GateState(gate.padlock, gate.padlockOpened)), gate.gate1.gameObject));
                }
                foreach (var door in DoorsField?.GetValue(states) as Doors[] ?? Array.Empty<Doors>())
                {
                    if (door != null && door.door != null)
                        list.Add(new SceneObject(PlayArgs.Lock, door.door.name, PathOf(door.door), door.id, ToNet(door.door.position),
                            "door " + LockText.Name(GateState(door.padlock, door.padlockOpened)), door.door.gameObject));
                }
                foreach (var sliding in SlidingDoorsField?.GetValue(states) as SlidingDoors[] ?? Array.Empty<SlidingDoors>())
                {
                    if (sliding != null && sliding.door != null)
                        list.Add(new SceneObject(PlayArgs.Lock, sliding.door.name, PathOf(sliding.door), sliding.id, ToNet(sliding.door.position),
                            "sliding " + (SafeRfidOpen(states, sliding.id) ? "OPEN" : "closed"), sliding.door.gameObject));
                }
            }
            foreach (var interactable in Compat.FindAll<FPSInteractable>(true))
            {
                if (interactable == null || !IsLockInteraction(interactable.m_interaction))
                    continue;
                string gate = GateToOpenField?.GetValue(interactable) as string;
                string detail = interactable.m_interaction + (string.IsNullOrEmpty(gate) ? string.Empty : " opens " + gate) +
                                (interactable.isActiveAndEnabled ? string.Empty : " (inactive)");
                list.Add(new SceneObject(PlayArgs.Lock, interactable.name, PathOf(interactable.transform), Interactions.IdOf(interactable),
                    ToNet(interactable.transform.position), detail, interactable));
            }
        }

        private static SceneObject Make(string category, Transform t, string id, string detail, object handle) =>
            new SceneObject(category, t.name, PathOf(t), id, ToNet(t.position), detail, handle);

        // ---- Moving -------------------------------------------------------------------------------------

        public bool Goto(SceneObject target, out string message)
        {
            if (!WalkingPlayer(out var controller, out message))
                return false;
            var transform = TransformOf(target?.Handle);
            if (transform == null)
            {
                message = "the object is gone";
                return false;
            }
            Vector3 feet = FeetOf(controller);
            Vector3 stand;
            float? yaw = null;
            string how;
            switch (target.Category)
            {
                case PlayArgs.Pad:
                {
                    var pad = target.Handle as BountyPad;
                    var spot = pad != null && pad.m_playerPosition != null ? pad.m_playerPosition : transform;
                    stand = spot.position;
                    yaw = spot.eulerAngles.y;
                    how = pad != null && pad.m_playerPosition != null ? "onto the pad's player position" : "onto the pad";
                    break;
                }
                case PlayArgs.Tee:
                case PlayArgs.Trigger:
                    // Into its trigger: entering it is what the game reacts to (StandHere / DialogueTrigger.OnTriggerEnter).
                    stand = BoundsOf(transform, true, out _).center;
                    how = target.Category == PlayArgs.Tee ? "onto the tee (its trigger fires as the player enters)" : "into the trigger (it fires as the player enters)";
                    break;
                default:
                {
                    var bounds = BoundsOf(transform, false, out bool hasBounds);
                    float radius = hasBounds ? Mathf.Max(bounds.extents.x, bounds.extents.z) : DefaultRadius;
                    var spot = Placement.StandBeside(ToNet(feet), ToNet(bounds.center), radius, Placement.DefaultMargin, out float facing);
                    stand = ToUnity(spot);
                    yaw = facing;
                    how = "beside it, facing it";
                    break;
                }
            }
            stand.y = GroundBelow(stand, controller);
            if (!Teleport(controller, stand, yaw, out string via))
            {
                message = via;
                return false;
            }
            message = $"moved {how}: {target} -> feet {PlayText.Vector(ToNet(stand))} via {via}" +
                      $" (was {PlayText.Vector(ToNet(feet))}, {PlayText.Number(Vector3.Distance(feet, stand))} m away)";
            return true;
        }

        public bool GotoPosition(NetVector3 feet, float? yaw, out string message)
        {
            if (!WalkingPlayer(out var controller, out message))
                return false;
            var before = FeetOf(controller);
            var stand = ToUnity(feet);
            stand.y = GroundBelow(stand, controller);
            if (!Teleport(controller, stand, yaw, out string via))
            {
                message = via;
                return false;
            }
            message = $"moved to feet {PlayText.Vector(ToNet(stand))}" + (yaw.HasValue ? ", facing yaw " + PlayText.Number(yaw.Value) : string.Empty) +
                      $" via {via} (was {PlayText.Vector(ToNet(before))})";
            return true;
        }

        /// <summary>
        /// ECM2's own teleport (answers #4): Character.TeleportPosition(pos, interpolating: true, updateGround: true)
        /// drops the rigidbody interpolation for the jump and finds the ground. Without the character, the walker's
        /// transform is set with the character component off for one frame.
        /// </summary>
        private bool Teleport(MoveAndHitController controller, Vector3 feet, float? yaw, out string via)
        {
            try
            {
                var character = controller.m_fpsCharacter;
                if (character != null)
                {
                    character.TeleportPosition(feet, true, true);
                    if (yaw.HasValue)
                        character.SetRotation(Quaternion.Euler(0f, yaw.Value, 0f));
                    via = "ECM2 Character.TeleportPosition";
                    return true;
                }
                var walker = controller.m_FPSController;
                if (walker == null)
                {
                    via = "the player has no character and no m_FPSController";
                    return false;
                }
                _runner.StartCoroutine(MoveWithControllerOff(walker, feet, yaw));
                via = "m_FPSController's transform with its components off for a frame (no ECM2 character)";
                return true;
            }
            catch (Exception e)
            {
                via = "the teleport threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        private static IEnumerator MoveWithControllerOff(GameObject walker, Vector3 feet, float? yaw)
        {
            var behaviours = walker.GetComponents<Behaviour>();
            var wasEnabled = new bool[behaviours.Length];
            for (int i = 0; i < behaviours.Length; i++)
            {
                wasEnabled[i] = behaviours[i] != null && behaviours[i].enabled;
                if (wasEnabled[i])
                    behaviours[i].enabled = false;
            }
            walker.transform.position = feet;
            if (yaw.HasValue)
                walker.transform.rotation = Quaternion.Euler(0f, yaw.Value, 0f);
            yield return null;
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (wasEnabled[i] && behaviours[i] != null)
                    behaviours[i].enabled = true;
            }
        }

        private float GroundBelow(Vector3 at, MoveAndHitController controller)
        {
            var origin = new Vector3(at.x, at.y + GroundProbeUp, at.z);
            int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, GroundProbeUp + GroundProbeDepth, ~0, QueryTriggerInteraction.Ignore);
            Transform own = controller != null && controller.m_FPSController != null ? controller.m_FPSController.transform : null;
            float best = float.NaN;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.collider == null || (own != null && hit.collider.transform.IsChildOf(own)) || IsModObject(hit.collider.transform))
                    continue;
                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    best = hit.point.y;
                }
            }
            return float.IsNaN(best) ? at.y : best + 0.05f;
        }

        // ---- Using things -------------------------------------------------------------------------------

        public bool Interact(SceneObject target, out string message)
        {
            if (!Player(out var controller, out message))
                return false;
            object handle = target?.Handle;
            if (handle is GameObject go)
                handle = UsableOn(go);
            if (target?.Category == PlayArgs.Lock && !(handle is FPSInteractable))
                handle = PadlockFor(target.Id) ?? handle;
            string distance = DistanceNote(controller, TransformOf(target?.Handle));
            try
            {
                switch (handle)
                {
                    case FPSInteractable interactable:
                    {
                        if (!interactable.isActiveAndEnabled)
                        {
                            message = "it is inactive (used already?)";
                            return false;
                        }
                        var type = interactable.m_interaction;
                        string id = Interactions.IdOf(interactable);
                        interactable.Interact();
                        message = $"FPSInteractable.Interact() on {type} {id}{distance}; now " +
                                  (interactable != null && interactable.isActiveAndEnabled ? "still active" : "inactive") +
                                  ". In a session the mod's prefix may wait for the host's grant first (watch the toasts / log).";
                        return true;
                    }
                    case StandHere tee:
                        tee.Activate();
                        message = $"StandHere.Activate() on tee {tee.name}{distance} (what entering its trigger does)";
                        return true;
                    case BountyPad pad:
                        return UsePad(pad, controller, out message);
                    case DialogueTrigger trigger:
                        return EnterTrigger(trigger, controller, distance, out message);
                    default:
                        message = "nothing usable there (no FPSInteractable, StandHere, BountyPad or DialogueTrigger on it)";
                        return false;
                }
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                message = "the game threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private static object UsableOn(GameObject go)
        {
            if (go == null)
                return null;
            return (object)go.GetComponent<FPSInteractable>() ?? (object)go.GetComponent<StandHere>() ?? (object)go.GetComponent<BountyPad>()
                ?? (object)go.GetComponent<DialogueTrigger>() ?? go.GetComponentInChildren<FPSInteractable>(false);
        }

        private static FPSInteractable PadlockFor(string gateId)
        {
            if (string.IsNullOrEmpty(gateId))
                return null;
            foreach (var interactable in Compat.FindAll<FPSInteractable>(false))
            {
                if (interactable != null && IsLockInteraction(interactable.m_interaction)
                    && string.Equals(GateToOpenField?.GetValue(interactable) as string, gateId, StringComparison.Ordinal))
                    return interactable;
            }
            return null;
        }

        private bool EnterTrigger(DialogueTrigger trigger, MoveAndHitController controller, string distance, out string message)
        {
            var walker = controller.m_FPSController;
            var collider = walker != null ? walker.GetComponent<Collider>() ?? walker.GetComponentInChildren<Collider>() : null;
            if (TriggerEnterMethod == null)
            {
                message = "DialogueTrigger.OnTriggerEnter(Collider) not found; 'goto trigger ...' walks the player into it instead";
                return false;
            }
            if (collider == null)
            {
                message = "the player has no collider to enter the trigger with; 'goto trigger ...' instead";
                return false;
            }
            TriggerEnterMethod.Invoke(trigger, new object[] { collider });
            message = $"DialogueTrigger.OnTriggerEnter({collider.name}) on {trigger.name}{distance}; " +
                      (trigger.isActiveAndEnabled ? "the trigger is still active (the game ignored it?)" : "the trigger switched itself off (dialogue started)");
            return true;
        }

        private bool UsePad(BountyPad pad, MoveAndHitController controller, out string message)
        {
            string set = pad.m_bountyChallengeSet != null ? GameIds.ChallengeSetName(pad.m_bountyChallengeSet) : "(no set)";
            var notes = new StringBuilder();
            if (pad.m_completed)
                notes.Append("; the pad is completed (a replay)");
            if (IsPadLocked(pad))
                notes.Append("; the pad shows LOCKED (the game may refuse)");
            if (controller.m_activeBountyPad != null)
                notes.Append("; already in bounty ").Append(SetOf(controller.m_activeBountyPad));
            notes.Append(DistanceNote(controller, pad.m_playerPosition != null ? pad.m_playerPosition : pad.transform));
            var interactable = StartInteractableOf(pad);
            if (interactable != null)
            {
                interactable.Interact();
                message = $"FPSInteractable.Interact() (StartBounty) on pad {pad.name} of {set}{notes}; then {AfterStart(controller)}";
                return true;
            }
            pad.StartBounty();
            message = $"BountyPad.StartBounty() on pad {pad.name} of {set} (no StartBounty interactable found on it){notes}; then {AfterStart(controller)}";
            return true;
        }

        private static string AfterStart(MoveAndHitController controller)
        {
            var system = ChallengeSystem.instance;
            return "active pad " + (controller.m_activeBountyPad != null ? SetOf(controller.m_activeBountyPad) : "none") +
                   ", loaded set " + (system != null && system.m_challengeSet != null ? GameIds.ChallengeSetName(system.m_challengeSet) : "none") +
                   ", player " + controller.m_mode;
        }

        private static FPSInteractable StartInteractableOf(BountyPad pad)
        {
            foreach (var interactable in pad.GetComponentsInChildren<FPSInteractable>(false))
            {
                if (interactable != null && interactable.m_interaction == InteractionType.StartBounty)
                    return interactable;
            }
            foreach (var interactable in Compat.FindAll<FPSInteractable>(false))
            {
                if (interactable != null && interactable.m_interaction == InteractionType.StartBounty
                    && interactable.GetComponentInParent<BountyPad>() == pad)
                    return interactable;
            }
            return null;
        }

        // ---- Bounties -----------------------------------------------------------------------------------

        public bool StartBounty(string setName, out string message)
        {
            if (!Player(out var controller, out message))
                return false;
            BountyPad chosen = null;
            int found = 0;
            foreach (var pad in Compat.FindAll<BountyPad>(true))
            {
                if (pad == null || pad.m_bountyChallengeSet == null
                    || !string.Equals(GameIds.ChallengeSetName(pad.m_bountyChallengeSet), setName.Trim(), StringComparison.OrdinalIgnoreCase))
                    continue;
                found++;
                if (chosen == null || (!chosen.gameObject.activeInHierarchy && pad.gameObject.activeInHierarchy))
                    chosen = pad;
            }
            if (chosen == null)
            {
                message = "no BountyPad has that set ('find pad' lists the pads and their sets)";
                return false;
            }
            if (!chosen.gameObject.activeInHierarchy)
            {
                message = $"its pad {chosen.name} is inactive (not unlocked in this run yet?)";
                return false;
            }
            try
            {
                if (!UsePad(chosen, controller, out message))
                    return false;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                message = "the game threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            if (found > 1)
                message += $" ({found} pads have this set)";
            return true;
        }

        public bool ExitBounty(out string message)
        {
            if (!Player(out var controller, out message))
                return false;
            var system = ChallengeSystem.instance;
            if (system == null)
            {
                message = "no ChallengeSystem";
                return false;
            }
            string was = controller.m_activeBountyPad != null ? SetOf(controller.m_activeBountyPad) : null;
            try
            {
                system.ExitBountyChallenge(false);
            }
            catch (Exception e)
            {
                message = "ExitBountyChallenge threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            message = "ChallengeSystem.ExitBountyChallenge(false)" + (was != null ? " (was in " + was + ")" : " (no active pad before)") +
                      "; now " + AfterStart(controller);
            return true;
        }

        public string DescribeBounty()
        {
            if (!Player(out var controller, out string error))
                return error;
            var sb = new StringBuilder();
            var pad = controller.m_activeBountyPad;
            sb.Append("activeBounty ").Append(pad != null ? SetOf(pad) : "none");
            if (pad != null)
                sb.Append(" (pad ").Append(pad.name).Append(pad.m_completed ? ", completed" : string.Empty).Append(')');
            var system = ChallengeSystem.instance;
            if (system != null)
            {
                sb.Append("\nloadedSet ").Append(SetLine(system.m_challengeSet));
                sb.Append("\nmainSet ").Append(SetLine(system.m_mainChallengeSet));
                try
                {
                    var current = system.m_challengeSet != null ? system.GetCurrentChallenge() : null;
                    var challenges = system.m_challengeSet != null ? ChallengesField?.GetValue(system.m_challengeSet) as Challenge[] : null;
                    int index = current != null && challenges != null ? Array.IndexOf(challenges, current) : -1;
                    sb.Append("\ncurrentChallenge ").Append(index);
                    if (current != null)
                        sb.Append(" '").Append(ShortStringField?.GetValue(current) as string).Append("' active ").Append(Flag(ActiveField, current))
                            .Append(" complete ").Append(Flag(CompleteField, current));
                    sb.Append("\nchallengesCompleted ").Append(system.m_challengeSet != null ? system.ChallengesCompleted() : 0)
                        .Append(" of ").Append(challenges != null ? challenges.Length : 0);
                    if (pad != null)
                        sb.Append("\nhasCompletedThisBounty ").Append(system.HasCompletedThisBounty());
                }
                catch (Exception e)
                {
                    sb.Append("\nchallenge unreadable (").Append(e.GetType().Name).Append(": ").Append(e.Message).Append(')');
                }
                sb.Append("\ncurrentTee ").Append(string.IsNullOrEmpty(system.m_currentTee) ? "none" : system.m_currentTee);
                sb.Append("\nreplayingBounty ").Append(system.m_isReplayingBounty);
                sb.Append("\nballsInPlay ").Append(system.m_ballsInPlay != null ? system.m_ballsInPlay.Count : 0);
            }
            var manager = SaveManager.instance;
            if (manager != null)
            {
                var completed = manager.m_run.m_completedBounties;
                sb.Append("\ncompletedBounties ").Append(completed != null ? completed.Count : 0);
                if (pad != null && completed != null)
                    sb.Append("\nactiveBountyInCompleted ").Append(completed.Contains(SetOf(pad)));
                sb.Append("\nbank ").Append(ProgressManager.instance != null && ProgressManager.instance.m_bank != null ? ProgressManager.instance.m_bank.m_balance : manager.m_run.m_bankBalance);
            }
            sb.Append("\nplayer ").Append(controller.m_mode).Append(controller.m_moveLocked ? " moveLocked" : string.Empty);
            return sb.ToString();
        }

        private static string Flag(FieldInfo field, object target) =>
            field != null && field.GetValue(target) is bool b ? (b ? "True" : "False") : "?";

        private static string SetLine(ChallengeSet set)
        {
            if (set == null)
                return "none";
            bool bounty = IsBountyField != null && IsBountyField.GetValue(set) is bool b && b;
            return GameIds.ChallengeSetName(set) + (bounty ? " (bounty)" : string.Empty);
        }

        private static string SetOf(BountyPad pad) =>
            pad != null && pad.m_bountyChallengeSet != null ? GameIds.ChallengeSetName(pad.m_bountyChallengeSet) : "(no set)";

        // ---- Golf ---------------------------------------------------------------------------------------

        public bool SetMode(bool golf, out string message)
        {
            if (!Player(out var controller, out message))
                return false;
            if (golf == (controller.m_mode == ControlMode.Golf))
            {
                message = "already " + controller.m_mode;
                return true;
            }
            try
            {
                // As MoveAndHitController.Update does for the Switch action: it sets m_mode itself, then the switch
                // cooldown, then runs the transition (GolfTransition alone leaves the mode Walk).
                if (golf)
                {
                    if (controller.m_fpsCharacter != null && !controller.m_fpsCharacter.IsOnGround())
                    {
                        message = "the player is not on the ground (the game only switches to golf on the ground)";
                        return false;
                    }
                    controller.m_mode = ControlMode.Golf;
                    BeginSwitch(controller);
                    var input = FpsInputField?.GetValue(controller);
                    if (input != null)
                        AccessTools.Method(input.GetType(), "TurnOffGrid", Type.EmptyTypes)?.Invoke(input, null);
                    controller.GolfTransition(false);
                    message = "Switch to golf as the game's Switch action does (m_mode Golf, then GolfTransition(false); " +
                              "the game's own spot check is skipped)";
                }
                else if (FpsTransitionMethod != null && FpsTransitionMethod.Invoke(controller, null) is IEnumerator routine)
                {
                    controller.m_mode = ControlMode.Walk;
                    BeginSwitch(controller);
                    GoToFpsField?.SetValue(controller, controller.StartCoroutine(routine));
                    message = "Switch to walk as the game's Switch action does (m_mode Walk, then FPSTransition())";
                }
                else
                {
                    controller.FPSTransitionForBounty();
                    message = "MoveAndHitController.FPSTransitionForBounty() (FPSTransition not found)";
                }
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                message = "the transition threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            message += "; mode now " + controller.m_mode + " (transitions take a moment: check 'player')";
            return true;
        }

        /// <summary>The Switch action's common part: the 0.25 s switch cooldown, the walk coroutine stopped, the fade killed.</summary>
        private static void BeginSwitch(MoveAndHitController controller)
        {
            controller.m_cantSwitch = true;
            var progress = ProgressManager.instance;
            if (progress != null && CanSwitchFirstTimeMethod?.Invoke(progress, new object[] { 0.25f }) is IEnumerator cooldown)
                progress.StartCoroutine(cooldown);
            if (GoToFpsField?.GetValue(controller) is Coroutine running)
                controller.StopCoroutine(running);
            var image = TransitionImageField?.GetValue(controller) as Component;
            if (image != null)
                DoKillMethod?.Invoke(null, new object[] { image, false });
        }

        public bool Shoot(ShotRequest shot, out string message)
        {
            if (!Player(out var controller, out message))
                return false;
            if (controller.m_mode != ControlMode.Golf)
            {
                message = "the player is walking: 'mode golf' first (or stand on a tee / start a bounty)";
                return false;
            }
            var panels = PanelManager.instance;
            var swing = panels != null ? panels.m_clubSwingPanel : null;
            var hits = HitManager.instance;
            var ball = hits != null ? hits.m_ball : null;
            var sequence = HitSequeneceManager.instance;
            if (swing == null || ball == null || sequence == null)
            {
                message = "no swing panel, ball or HitSequeneceManager";
                return false;
            }
            if (ball.m_ballHit)
            {
                message = "this stroke was played already (the ball is in flight or at rest): 'shoot next' plays it where it lies, 'shoot again' restarts";
                return false;
            }
            if (!Enum.TryParse(shot.ShotType, true, out ShotType type) || !Enum.IsDefined(typeof(ShotType), type))
            {
                message = "unknown ShotType '" + shot.ShotType + "' (" + string.Join(", ", Enum.GetNames(typeof(ShotType))) + ")";
                return false;
            }
            float? full = ReadFullSwing(swing.m_currentClub);
            float power = ShotPower.ToPanelPower(shot.Power, full, out bool fallback);
            try
            {
                swing.m_power = power;
                var spin = panels.m_spinPanel;
                if (shot.Angle.HasValue && spin != null)
                    spin.m_angle = shot.Angle.Value;
                float angle = spin != null ? spin.m_angle : 0f;
                string club = swing.m_currentClub != null ? swing.m_currentClub.clubName : "?";
                string how;
                if (HandleShotMethod != null)
                {
                    HandleShotMethod.Invoke(swing, new object[] { type, 1f });
                    how = $"ClubSwing.HandleShot({type}, 1)";
                }
                else
                {
                    // The swing's own path not found: its two calls (answers #11 / gamemap 2).
                    sequence.StartCoroutine(sequence.HitSequence(angle, power, false, type));
                    var system = ChallengeSystem.instance;
                    if (system != null && system.m_ballsInPlay != null && !system.m_ballsInPlay.Contains(ball))
                        system.m_ballsInPlay.Add(ball);
                    how = $"HitSequeneceManager.HitSequence({PlayText.Number(angle)}, {PlayText.Number(power)}, false, {type}) (HandleShot not found)";
                }
                message = $"shot through {how}: club {club}, m_power {PlayText.Number(power)} = {PlayText.Number(shot.Power)} of " +
                          (fallback ? "an assumed full swing " + PlayText.Number(ShotPower.FallbackFullSwing) + " (powerVisualMult unreadable)" : "powerVisualMult " + PlayText.Number(full.Value)) +
                          $", angle {PlayText.Number(angle)}, stroke {ball.m_currentShot + 1}; the ball leaves after the swing video (about 0.9 s)";
                return true;
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                message = "the swing threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
        }

        private static float? ReadFullSwing(Club club)
        {
            if (club == null)
                return null;
            try
            {
                var field = Traverse.Create(club).Field("powerVisualMult");
                if (!field.FieldExists())
                    return null;
                object value = field.GetValue();
                return value == null ? (float?)null : Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool RestartShot(bool resume, out string message)
        {
            if (!Player(out _, out message))
                return false;
            var sequence = HitSequeneceManager.instance;
            if (sequence == null)
            {
                message = "no HitSequeneceManager";
                return false;
            }
            try
            {
                sequence.Restart(resume);
            }
            catch (Exception e)
            {
                message = "Restart threw " + e.GetType().Name + ": " + e.Message;
                return false;
            }
            var ball = HitManager.instance != null ? HitManager.instance.m_ball : null;
            message = $"HitSequeneceManager.Restart({(resume ? "true" : "false")}): " +
                      (resume ? "the next stroke from where the ball lies" : "the stroke again") +
                      (ball != null ? $"; stroke {ball.m_currentShot + 1}, ball at {PlayText.Vector(ToNet(ball.transform.position))}" : string.Empty);
            return true;
        }

        // ---- Reports ------------------------------------------------------------------------------------

        public string DescribeLocks(string filter)
        {
            if (!InWorld(out string error))
                return error;
            var sb = new StringBuilder();
            var lines = new List<string>();
            int open = 0, locked = 0;
            var states = ObjectStatesOrNull();
            if (states == null)
                sb.Append("no ObjectStates (ProgressManager.m_objectStates)");
            else
            {
                foreach (var gate in states.m_gates ?? Array.Empty<PicketGates>())
                {
                    if (gate == null)
                        continue;
                    var state = GateState(gate.padlock, gate.padlockOpened);
                    Count(state, ref open, ref locked);
                    lines.Add($"gate {gate.id}: {LockText.Name(state)} (padlock {LockText.Active(ActiveOf(gate.padlock))}, opened padlock {LockText.Active(ActiveOf(gate.padlockOpened))}" +
                              $", gate1 yaw {Yaw(gate.gate1)}, gate2 yaw {Yaw(gate.gate2)})");
                }
                foreach (var door in DoorsField?.GetValue(states) as Doors[] ?? Array.Empty<Doors>())
                {
                    if (door == null)
                        continue;
                    var state = GateState(door.padlock, door.padlockOpened);
                    Count(state, ref open, ref locked);
                    lines.Add($"door {door.id}: {LockText.Name(state)} (padlock {LockText.Active(ActiveOf(door.padlock))}, opened padlock {LockText.Active(ActiveOf(door.padlockOpened))}" +
                              $", door yaw {Yaw(door.door)}, open rotation {PlayText.Vector(ToNet(door.rotation))})");
                }
                foreach (var sliding in SlidingDoorsField?.GetValue(states) as SlidingDoors[] ?? Array.Empty<SlidingDoors>())
                {
                    if (sliding == null)
                        continue;
                    bool isOpen = SafeRfidOpen(states, sliding.id);
                    if (isOpen)
                        open++;
                    else
                        locked++;
                    lines.Add($"sliding {sliding.id}: {(isOpen ? "OPEN" : "LOCKED")} (CheckRFIDDoorAlreadyOpen)");
                }
            }
            foreach (var interactable in Compat.FindAll<FPSInteractable>(true))
            {
                if (interactable == null || !IsLockInteraction(interactable.m_interaction))
                    continue;
                string gate = GateToOpenField?.GetValue(interactable) as string;
                lines.Add($"{interactable.m_interaction} {Interactions.IdOf(interactable)}: {(interactable.isActiveAndEnabled ? "active (usable)" : "inactive (used)")}" +
                          (string.IsNullOrEmpty(gate) ? string.Empty : ", opens " + gate) + " at " + PlayText.Vector(ToNet(interactable.transform.position)));
            }
            foreach (var pad in Compat.FindAll<BountyPad>(true))
            {
                if (pad == null)
                    continue;
                lines.Add($"pad {SetOf(pad)}: {(IsPadLocked(pad) ? "LOCKED" : "unlocked")}, {(pad.m_completed ? "completed" : "open")}" +
                          (pad.gameObject.activeInHierarchy ? string.Empty : ", inactive"));
            }
            var manager = SaveManager.instance;
            if (manager != null)
            {
                var run = manager.m_run;
                lines.Add($"run: crowbar {run.m_crowbarUnlocked}, card {run.m_cardUnlocked}, serverCut {run.m_serverCut}, bunkerOpen {run.m_bunkerOpen}, " +
                          $"bountiesRestored {run.m_bountiesRestored}, bountyAccessLevel {run.bountyAccessLevel}");
            }
            string f = (filter ?? string.Empty).Trim();
            int shown = 0;
            var body = new StringBuilder();
            foreach (var line in lines)
            {
                if (f.Length > 0 && line.IndexOf(f, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                body.Append('\n').Append("  ").Append(line);
                shown++;
            }
            sb.Insert(0, $"locks: {open} open, {locked} locked (gates, doors, sliding doors); {shown} line(s){(f.Length > 0 ? " matching '" + f + "'" : string.Empty)}");
            sb.Append(body);
            return sb.ToString();
        }

        private static void Count(LockState state, ref int open, ref int locked)
        {
            if (state == LockState.Open)
                open++;
            else if (state == LockState.Locked)
                locked++;
        }

        public string DescribeDialogue()
        {
            var sb = new StringBuilder();
            var controller = MoveAndHitController.instance;
            if (controller == null)
                sb.Append("no player (not in Main)");
            else
            {
                sb.Append("moveLocked ").Append(controller.m_moveLocked)
                    .Append("\nmode ").Append(controller.m_mode)
                    .Append("\ncantSwitch ").Append(controller.m_cantSwitch);
                var fps = controller.m_fpsVideo;
                var player = fps != null ? fps.m_videoPlayer : null;
                sb.Append("\nfpsVideo ").Append(player == null ? "none" : player.isPlaying ? "PLAYING " + VideoName(player) : "idle");
            }
            var dialogue = DialogueManager.instance;
            sb.Append("\ndialogueManager ").Append(dialogue != null ? "present" : "none");
            if (dialogue != null)
                sb.Append("\ndialogueFlags ").Append(SimpleFields(dialogue));
            int playing = 0;
            var videos = new StringBuilder();
            foreach (var video in Compat.FindAll<VideoPlayer>(false))
            {
                if (video == null || !video.isPlaying)
                    continue;
                playing++;
                videos.Append("\n  ").Append(PathOf(video.transform)).Append(": ").Append(VideoName(video))
                    .Append(" frame ").Append(video.frame).Append('/').Append(video.frameCount);
            }
            sb.Append("\nvideosPlaying ").Append(playing).Append(videos);
            int triggers = 0;
            foreach (var trigger in Compat.FindAll<DialogueTrigger>(false))
            {
                if (trigger != null && trigger.isActiveAndEnabled)
                    triggers++;
            }
            sb.Append("\nactiveDialogueTriggers ").Append(triggers);
            sb.Append("\ntimeScale ").Append(Time.timeScale.ToString("0.###", CultureInfo.InvariantCulture))
                .Append(", cursor ").Append(Cursor.lockState).Append(Cursor.visible ? " visible" : " hidden");
            return sb.ToString();
        }

        /// <summary>The dialogue manager's own bool / int / float / string fields (their names are not documented).</summary>
        private static string SimpleFields(object target)
        {
            var parts = new List<string>();
            foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                var type = field.FieldType;
                if (type != typeof(bool) && type != typeof(int) && type != typeof(float) && type != typeof(string))
                    continue;
                object value;
                try
                {
                    value = field.GetValue(target);
                }
                catch (Exception)
                {
                    continue;
                }
                string text = value is float f ? f.ToString("0.###", CultureInfo.InvariantCulture) : value?.ToString() ?? "null";
                if (text.Length > 40)
                    text = text.Substring(0, 40) + "...";
                parts.Add(field.Name + "=" + text);
            }
            return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
        }

        private static string VideoName(VideoPlayer video)
        {
            if (video.clip != null)
                return "'" + video.clip.name + "'";
            return string.IsNullOrEmpty(video.url) ? "(no clip)" : "'" + video.url + "'";
        }

        public bool DialogueNext(out string message)
        {
            var dialogue = DialogueManager.instance;
            if (dialogue == null)
            {
                message = "no DialogueManager (not in Main)";
                return false;
            }
            if (NextButtonMethod == null)
            {
                message = "DialogueManager.NextButtonPressed not found";
                return false;
            }
            try
            {
                NextButtonMethod.Invoke(dialogue, null);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                message = "NextButtonPressed threw " + inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            var controller = MoveAndHitController.instance;
            message = "DialogueManager.NextButtonPressed()" + (controller != null ? "; moveLocked now " + controller.m_moveLocked : string.Empty);
            return true;
        }

        public string DescribeRemotes()
        {
            var root = GameObject.Find(RemoteRootName);
            if (root == null)
                return "remotes: 0 (no '" + RemoteRootName + "' object: no remote player in this world)";
            var controller = MoveAndHitController.instance;
            Vector3? feet = controller != null ? FeetOf(controller) : (Vector3?)null;
            var sb = new StringBuilder();
            sb.Append("remotes: ").Append(root.transform.childCount);
            int trails = 0, emitting = 0;
            foreach (Transform child in root.transform)
            {
                sb.Append("\n  ").Append(child.name).Append(child.gameObject.activeInHierarchy ? " shown" : " HIDDEN")
                    .Append(" at ").Append(PlayText.Vector(ToNet(child.position)));
                if (feet.HasValue)
                    sb.Append(", ").Append(PlayText.Number(Vector3.Distance(feet.Value, child.position))).Append(" m from the player");
                int shown = 0, renderers = 0;
                foreach (var renderer in child.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer == null || renderer is TrailRenderer || renderer is LineRenderer)
                        continue;
                    renderers++;
                    if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                        shown++;
                }
                sb.Append(", renderers ").Append(shown).Append('/').Append(renderers).Append(" on");
                foreach (var cutout in child.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (cutout != null && cutout.sprite != null)
                        sb.Append(", cutout '").Append(cutout.sprite.name).Append('\'');
                }
                foreach (var trail in child.GetComponentsInChildren<TrailRenderer>(true))
                {
                    if (trail == null)
                        continue;
                    trails++;
                    if (trail.enabled && trail.emitting)
                        emitting++;
                    sb.Append(", trail '").Append(trail.name).Append("' ").Append(trail.enabled ? "enabled" : "disabled")
                        .Append(trail.emitting ? " emitting" : " not emitting").Append(", ").Append(trail.positionCount).Append(" points, time ")
                        .Append(PlayText.Number(trail.time)).Append(" s");
                }
                foreach (var line in child.GetComponentsInChildren<LineRenderer>(true))
                {
                    if (line == null)
                        continue;
                    trails++;
                    if (line.enabled && line.positionCount > 1)
                        emitting++;
                    sb.Append(", line '").Append(line.name).Append("' ").Append(line.enabled ? "enabled" : "disabled").Append(", ")
                        .Append(line.positionCount).Append(" points");
                }
                int colliders = child.GetComponentsInChildren<Collider>(true).Length;
                int bodies = child.GetComponentsInChildren<Rigidbody>(true).Length;
                if (colliders > 0 || bodies > 0)
                    sb.Append(", DANGER ").Append(colliders).Append(" collider(s) ").Append(bodies).Append(" rigidbody(ies)");
            }
            sb.Append("\ntrails: ").Append(trails).Append(", drawing: ").Append(emitting);
            return sb.ToString();
        }

        public string DescribePlayer()
        {
            if (!Player(out var controller, out string error))
                return error;
            var sb = new StringBuilder();
            var feet = FeetOf(controller);
            sb.Append("feet ").Append(PlayText.Vector(ToNet(feet)));
            var character = controller.m_fpsCharacter;
            if (character != null)
                sb.Append(", yaw ").Append(PlayText.Number(character.GetRotation().eulerAngles.y)).Append(", on ground ").Append(character.IsOnGround());
            sb.Append("\nmode ").Append(controller.m_mode).Append(controller.m_moveLocked ? " moveLocked" : string.Empty)
                .Append(controller.m_cantSwitch ? " cantSwitch" : string.Empty);
            sb.Append("\nclub ").Append(controller.m_selectedClub != null ? controller.m_selectedClub.clubName : "none");
            if (controller.m_golferHolder != null)
                sb.Append("\ngolfer ").Append(PlayText.Vector(ToNet(controller.m_golferHolder.position)))
                    .Append(" yaw ").Append(PlayText.Number(controller.m_golferHolder.eulerAngles.y));
            var ball = HitManager.instance != null ? HitManager.instance.m_ball : null;
            if (ball != null)
            {
                sb.Append("\nball ").Append(PlayText.Vector(ToNet(ball.transform.position)))
                    .Append(ball.gameObject.activeInHierarchy ? string.Empty : " (inactive)")
                    .Append(", hit ").Append(ball.m_ballHit)
                    .Append(", stroke ").Append(ball.m_currentShot + 1);
                if (ball.m_rb != null)
                    sb.Append(", ").Append(ball.m_rb.isKinematic ? "kinematic" : "moving " + PlayText.Number(Compat.GetVelocity(ball.m_rb).magnitude) + " m/s");
                if (ball.m_currentShotData != null)
                    sb.Append(", shotComplete ").Append(ball.m_currentShotData.m_shotComplete)
                        .Append(", finishedDistance ").Append(PlayText.Number(ball.m_currentShotData.m_finishedDistance));
                if (ball.m_trail != null)
                    sb.Append(", own trail ").Append(ball.m_trail.emitting ? "emitting" : "idle").Append(' ').Append(ball.m_trail.positionCount).Append(" points");
            }
            var bank = ProgressManager.instance != null ? ProgressManager.instance.m_bank : null;
            if (bank != null)
                sb.Append("\nbank ").Append(bank.m_balance);
            return sb.ToString();
        }

        // ---- Helpers ------------------------------------------------------------------------------------

        private static bool InWorld(out string error)
        {
            var ctx = GameContext.Current;
            if (ctx != null && (ctx.Scene != GameScene.Main || !ctx.WorldReady))
            {
                error = "the world is not loaded (scene " + ctx.Scene + (ctx.WorldReady ? string.Empty : ", not ready") + ")";
                return false;
            }
            if (MoveAndHitController.instance == null)
            {
                error = "no player (not in Main)";
                return false;
            }
            error = null;
            return true;
        }

        private static bool Player(out MoveAndHitController controller, out string error)
        {
            controller = null;
            if (!InWorld(out error))
                return false;
            controller = MoveAndHitController.instance;
            return true;
        }

        private static bool WalkingPlayer(out MoveAndHitController controller, out string error)
        {
            if (!Player(out controller, out error))
                return false;
            if (controller.m_mode != ControlMode.Walk)
            {
                error = "the player is in golf mode: 'mode walk' first";
                return false;
            }
            if (controller.m_moveLocked)
            {
                error = "the player is move-locked (a dialogue or cutscene runs): 'dialogue next' or wait";
                return false;
            }
            return true;
        }

        private static Vector3 FeetOf(MoveAndHitController controller)
        {
            // Answers #55: ECM2's GetPosition is at the feet.
            if (controller.m_fpsCharacter != null)
                return controller.m_fpsCharacter.GetPosition();
            return controller.m_FPSController != null ? controller.m_FPSController.transform.position : controller.transform.position;
        }

        private static string DistanceNote(MoveAndHitController controller, Transform target)
        {
            if (controller == null || target == null)
                return string.Empty;
            float distance = Placement.FlatDistance(ToNet(FeetOf(controller)), ToNet(target.position));
            return distance > FarFromTarget
                ? $" (the player is {PlayText.Number(distance)} m away; a player at the PC would have to be close: 'goto' first)"
                : string.Empty;
        }

        private static Transform TransformOf(object handle)
        {
            switch (handle)
            {
                case GameObject go when go != null:
                    return go.transform;
                case Component component when component != null:
                    return component.transform;
                default:
                    return null;
            }
        }

        /// <summary>The object's renderers (or colliders) as world bounds; triggers only when <paramref name="triggers"/>.</summary>
        private static Bounds BoundsOf(Transform t, bool triggers, out bool found)
        {
            found = false;
            var bounds = new Bounds(t.position, Vector3.zero);
            foreach (var collider in t.GetComponentsInChildren<Collider>(false))
            {
                if (collider == null || !collider.enabled || collider.isTrigger != triggers)
                    continue;
                Encapsulate(ref bounds, ref found, collider.bounds);
            }
            if (found || triggers)
                return bounds;
            foreach (var renderer in t.GetComponentsInChildren<Renderer>(false))
            {
                if (renderer != null && renderer.enabled)
                    Encapsulate(ref bounds, ref found, renderer.bounds);
            }
            return bounds;
        }

        private static void Encapsulate(ref Bounds bounds, ref bool found, Bounds add)
        {
            if (!found)
            {
                bounds = add;
                found = true;
            }
            else
                bounds.Encapsulate(add);
        }

        private static bool IsLockInteraction(InteractionType type) =>
            type == InteractionType.Padlock || type == InteractionType.RFID || type == InteractionType.CutServer || type == InteractionType.OpenBunker;

        private static bool IsPadLocked(BountyPad pad)
        {
            if (pad == null || pad.m_locked == null)
                return false;
            foreach (var go in pad.m_locked)
            {
                if (go != null && go.activeSelf)
                    return true;
            }
            return false;
        }

        private static LockState GateState(GameObject padlock, GameObject opened) => LockText.Classify(ActiveOf(padlock), ActiveOf(opened));

        private static bool? ActiveOf(GameObject go) => go == null ? (bool?)null : go.activeInHierarchy;

        private static bool SafeRfidOpen(ObjectStates states, string id)
        {
            try
            {
                return states.CheckRFIDDoorAlreadyOpen(id);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ObjectStates ObjectStatesOrNull()
        {
            var progress = ProgressManager.instance;
            return progress != null && ObjectStatesField != null ? ObjectStatesField.GetValue(progress) as ObjectStates : null;
        }

        private static string Yaw(Transform t) => t == null ? "-" : PlayText.Number(t.eulerAngles.y);

        private static bool IsModObject(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (t.name.StartsWith(ModPrefix, StringComparison.Ordinal) || t.name == RemoteRootName)
                    return true;
            }
            return false;
        }

        private static string PathOf(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null; t = t.parent)
                names.Add(t.name);
            names.Reverse();
            return transform.gameObject.scene.name + ":" + string.Join("/", names);
        }

        private static NetVector3 ToNet(Vector3 v) => new NetVector3(v.x, v.y, v.z);

        private static Vector3 ToUnity(NetVector3 v) => new Vector3(v.x, v.y, v.z);
    }
}
