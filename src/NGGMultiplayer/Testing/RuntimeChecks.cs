using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NGGMultiplayer.Game;
using UnityEngine;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The confirmation answers that need the running game (marked ? in docs/confirm/answers-1.md / answers-2.md),
    /// checked on demand by the harness's <c>check</c> command and logged as the outcome that actually happened. Read
    /// only; each check catches its own failures. Run them in the scene the answer is about (most need "Main").
    /// </summary>
    internal static class RuntimeChecks
    {
        public static readonly string[] Names =
        {
            "interaction", "shottype", "colliders", "pause", "tab", "keys", "standees", "lookinput", "materials", "lmugc", "manager"
        };

        public static string Run(string name)
        {
            if (string.IsNullOrEmpty(name) || name == "all")
            {
                var sb = new StringBuilder();
                foreach (var check in Names)
                {
                    if (sb.Length > 0)
                        sb.Append('\n');
                    sb.Append(Run(check));
                }
                return sb.ToString();
            }
            string result;
            try
            {
                switch (name)
                {
                    case "interaction": result = Interaction(); break;
                    case "shottype": result = ShotTypes(); break;
                    case "colliders": result = Colliders(); break;
                    case "pause": result = Pause(); break;
                    case "tab": result = Tab(); break;
                    case "keys": result = Keys(); break;
                    case "standees": result = Standees(); break;
                    case "lookinput": result = LookInput(); break;
                    case "materials": result = Materials(); break;
                    case "lmugc": result = Lmugc(); break;
                    case "manager": result = Manager(); break;
                    default: return "unknown check '" + name + "'; known: all, " + string.Join(", ", Names);
                }
            }
            catch (Exception e)
            {
                result = "FAILED: " + e.GetType().Name + ": " + e.Message;
            }
            return "check " + name + ": " + result;
        }

        // #92: the bots put InteractionType on the wire as numbers; their table must match the game's enum.
        private static string Interaction()
        {
            var names = Enum.GetNames(typeof(InteractionType));
            var mismatches = new List<string>();
            for (int i = 0; i < Math.Max(names.Length, BotInteractions.Names.Length); i++)
            {
                string game = null;
                if (i < names.Length)
                    game = Enum.GetName(typeof(InteractionType), i);
                string bot = i < BotInteractions.Names.Length ? BotInteractions.Names[i] : null;
                if (game != bot)
                    mismatches.Add($"{i}: game {game ?? "-"} / bot {bot ?? "-"}");
            }
            return mismatches.Count == 0
                ? $"InteractionType has {names.Length} members in the order the bots use (#92 holds)"
                : "MISMATCH (bot pickup claims carry wrong types): " + string.Join("; ", mismatches);
        }

        // Not in the answers: the bots' shot type is resolved by name.
        private static string ShotTypes()
        {
            var sb = new StringBuilder();
            foreach (var value in Enum.GetValues(typeof(ShotType)))
            {
                if (sb.Length > 0)
                    sb.Append(", ");
                sb.Append(value).Append('=').Append(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }
            return "ShotType {" + sb + "}; bots send " + HarnessGame.ShotTypeByName("Perfect");
        }

        // #97: LukeVideo.OnTriggerEnter has no filter; remote avatars and ghost balls must have no collider or body.
        private static string Colliders()
        {
            HarnessGame.CountRemotes(out int views, out int colliders, out int bodies);
            int lukes = CountInScenes(AccessTools.TypeByName("LukeVideo"));
            string verdict = colliders == 0 && bodies == 0 ? "OK" : "DANGER: a remote view could trigger LukeVideo (ending)";
            return $"{views} remote view(s), {colliders} collider(s), {bodies} rigidbody(ies) under them; {lukes} LukeVideo trigger(s) in the scene. {verdict}";
        }

        // #58, #112, #147: is the PauseMenu object inactive while closed, how many exist, what OpenMenu enables.
        private static string Pause()
        {
            var menus = Compat.FindAll<PauseMenu>(true);
            var sb = new StringBuilder();
            sb.Append(menus.Length).Append(" PauseMenu instance(s)");
            foreach (var menu in menus)
            {
                if (menu == null)
                    continue;
                sb.Append("; '").Append(menu.gameObject.name).Append("' activeInHierarchy ").Append(menu.gameObject.activeInHierarchy)
                    .Append(" enabled ").Append(menu.enabled);
            }
            var escapes = Compat.FindAll<PlayerPressedEscape>(true);
            var field = AccessTools.Field(typeof(PlayerPressedEscape), "m_objectToEnable");
            foreach (var escape in escapes)
            {
                var target = field != null ? field.GetValue(escape) as GameObject : null;
                sb.Append("; PlayerPressedEscape.m_objectToEnable = ")
                    .Append(target == null ? (field == null ? "(no such field)" : "null") : "'" + target.name + "' active " + target.activeSelf
                        + (target.GetComponentInChildren<PauseMenu>(true) != null ? ", holds the PauseMenu" : ", NO PauseMenu on it"));
            }
            var settings = Compat.FindAll<SettingsMenu>(true);
            foreach (var menu in settings)
            {
                if (menu != null)
                    sb.Append("; SettingsMenu '").Append(menu.gameObject.name).Append("' active ").Append(menu.gameObject.activeInHierarchy);
            }
            sb.Append("; timeScale ").Append(Time.timeScale.ToString(CultureInfo.InvariantCulture));
            sb.Append(". Run once with the menu closed, once open, once in its settings submenu.");
            return sb.ToString();
        }

        // #50, #67: is the scorecard key ([General] ScorecardKey) bound to a game action? The game binds Tab.
        private static string Tab()
        {
            var key = GameContext.Current != null ? GameContext.Current.ScorecardKey : GameContext.DefaultScorecardKey;
            var tab = Game.Presence.PresenceAccess.KeyBindings(KeyCode.Tab);
            if (tab == null)
                return "no InputManager.instance.input.actions";
            string tabText = tab.Count == 0 ? "Tab is not bound" : "Tab is bound (" + string.Join(", ", tab) + ")";
            if (key == KeyCode.None)
                return tabText + "; scorecard key None (off)";
            var own = Game.Presence.PresenceAccess.KeyBindings(key);
            return tabText + "; scorecard key " + key + (own.Count == 0
                ? " is free in the game's input actions"
                : " COLLIDES with " + string.Join(", ", own));
        }

        // Every keyboard binding of the game's input actions (to pick free keys for the mod).
        private static string Keys()
        {
            var all = Game.Presence.PresenceAccess.KeyBindings(KeyCode.None);
            if (all == null)
                return "no InputManager.instance.input.actions";
            return all.Count + " keyboard binding(s): " + string.Join("; ", all);
        }

        // #41, #123: are the shop standees SaveableObjects (do bought clubs stay gone through a save)?
        private static string Standees()
        {
            var progress = ProgressManager.instance;
            var statesField = AccessTools.Field(typeof(ProgressManager), "m_objectStates");
            var states = progress != null && statesField != null ? statesField.GetValue(progress) as ObjectStates : null;
            if (states == null)
                return "no ObjectStates (not in Main)";
            var sb = new StringBuilder();
            foreach (var name in new[] { "m_driver", "m_driverStandee", "m_wedge", "m_wedgeStandee", "m_hybrid", "m_hybridStandee",
                         "m_ticket", "m_lmugcMachine", "m_assistStandees", "m_assists" })
            {
                var field = AccessTools.Field(typeof(ObjectStates), name);
                var go = field != null ? field.GetValue(states) as GameObject : null;
                if (sb.Length > 0)
                    sb.Append("; ");
                sb.Append(name).Append(": ");
                if (go == null)
                {
                    sb.Append(field == null ? "no field" : "null");
                    continue;
                }
                var own = go.GetComponent<SaveableObject>();
                var parent = own != null ? own : go.GetComponentInParent<SaveableObject>();
                var children = go.GetComponentsInChildren<SaveableObject>(true).Length;
                sb.Append(own != null ? "SaveableObject " + own.m_id
                    : parent != null ? "under SaveableObject " + parent.m_id
                    : "NOT a SaveableObject");
                if (children > (own != null ? 1 : 0))
                    sb.Append(" (").Append(children).Append(" below)");
                sb.Append(go.activeSelf ? ", active" : ", inactive");
            }
            return sb.ToString();
        }

        // #115: the look input sits on m_FPSController; the inputs the pause disables are Behaviours.
        private static string LookInput()
        {
            var controller = MoveAndHitController.instance;
            if (controller == null || controller.m_FPSController == null)
                return "no MoveAndHitController / m_FPSController (not in Main)";
            var fps = controller.m_FPSController;
            var on = fps.GetComponent(typeof(ECM2.Examples.FirstPerson.FirstPersonCharacterLookInput));
            var below = on != null ? null : fps.GetComponentInChildren(typeof(ECM2.Examples.FirstPerson.FirstPersonCharacterLookInput), true);
            var inputField = AccessTools.Field(typeof(MoveAndHitController), "m_fpsInput");
            var input = inputField != null ? inputField.GetValue(controller) as Behaviour : null;
            var panels = PanelManager.instance;
            var swing = panels != null ? panels.m_clubSwingPanel : null;
            return "FirstPersonCharacterLookInput " + (on != null ? "on m_FPSController" : below != null ? "on child '" + below.name + "'" : "NOT FOUND")
                + "; m_fpsInput " + (input != null ? "Behaviour, enabled " + input.enabled : "not readable")
                + "; m_clubSwingPanel " + (swing != null ? "enabled " + swing.enabled : "none");
        }

        // #130, #131: which shader properties the ball skins and the ball's own material have (avatar tint, ghost skins).
        private static string Materials()
        {
            var sb = new StringBuilder();
            var cosmetics = Cosmetics.instance;
            if (cosmetics == null || cosmetics.m_balls == null)
            {
                sb.Append("no Cosmetics.instance.m_balls");
            }
            else
            {
                sb.Append(cosmetics.m_balls.Length).Append(" ball skin(s)");
                for (int i = 0; i < cosmetics.m_balls.Length && i < 3; i++)
                {
                    var mat = cosmetics.m_balls[i] != null ? cosmetics.m_balls[i].mat : null;
                    sb.Append("; skin ").Append(i).Append(": ").Append(Describe(mat));
                }
            }
            var hit = HitManager.instance;
            var renderer = hit != null && hit.m_ball != null ? hit.m_ball.m_meshRenderer : null;
            sb.Append("; ball m_meshRenderer: ").Append(renderer != null ? Describe(renderer.sharedMaterial) : "none");
            return sb.ToString();
        }

        // #143: the LMUGC set is a bounty set (LoadNewChallengeSet then keeps m_mainChallengeSet).
        private static string Lmugc()
        {
            var panels = PanelManager.instance;
            var lmugc = panels != null ? panels.m_LMUGCPanel : null;
            var field = AccessTools.Field(typeof(LMUGC), "LMUGCChallenge");
            var set = lmugc != null && field != null ? field.GetValue(lmugc) as ChallengeSet : null;
            if (set == null)
                return lmugc == null ? "no LMUGC panel (not in Main)" : "LMUGC.LMUGCChallenge " + (field == null ? "field missing" : "is null");
            var isBounty = AccessTools.Field(typeof(ChallengeSet), "isBounty");
            object bounty = isBounty != null ? isBounty.GetValue(set) : null;
            var system = ChallengeSystem.instance;
            string sets = system != null
                ? $"; main set {GameIds.ChallengeSetName(system.m_mainChallengeSet)}, loaded set {GameIds.ChallengeSetName(system.m_challengeSet)}"
                : string.Empty;
            var manager = SaveManager.instance;
            string inLmugc = manager != null ? ", m_run.isInLMUGC " + manager.m_run.isInLMUGC : string.Empty;
            return $"LMUGCChallenge '{GameIds.ChallengeSetName(set)}' isBounty {(bounty == null ? "unreadable" : bounty.ToString())}{sets}{inLmugc}" +
                   " (run inside an LMUGC round too: the main set must not change)";
        }

        // #145: BepInEx's manager object survives scene loads (the plugin's OnDestroy only runs at shutdown).
        private static string Manager()
        {
            var chainloader = AccessTools.TypeByName("BepInEx.Bootstrap.Chainloader");
            var property = chainloader != null ? AccessTools.Property(chainloader, "ManagerObject") : null;
            var go = property != null ? property.GetValue(null, null) as GameObject : null;
            if (go == null)
                return "BepInEx Chainloader.ManagerObject not found";
            return $"BepInEx manager '{go.name}' in scene '{go.scene.name}' (DontDestroyOnLoad means it survives scene loads), " +
                   $"hideFlags {go.hideFlags}";
        }

        private static int CountInScenes(Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return -1;
            int count = 0;
            foreach (var found in Resources.FindObjectsOfTypeAll(type))
            {
                if (found is Component component && component.gameObject.scene.IsValid())
                    count++;
            }
            return count;
        }

        private static string Describe(Material mat)
        {
            if (mat == null)
                return "null";
            var shader = mat.shader;
            var props = new List<string>();
            foreach (var p in new[] { "_BaseColor", "_BaseMap", "_Color", "_MainTex" })
            {
                if (mat.HasProperty(p))
                    props.Add(p);
            }
            return $"'{mat.name}' shader '{(shader != null ? shader.name : "none")}' [{string.Join(" ", props)}]";
        }
    }
}
