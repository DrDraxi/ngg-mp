using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The game members Presence reaches by name: the private ones and those whose access is not confirmed
    /// (docs/confirm answers). Public members (Ball's fields, ECM2.Character state, Challenge.m_forceWind,
    /// Cosmetics.m_balls[i].mat) are used directly. Resolved once at install. A missing required member disables its
    /// feature through the patcher; an optional one is null and its use falls back (logged once).
    /// </summary>
    internal sealed class PresenceAccess
    {
        private const string Log = "[Presence] ";

        private GameContext _context;

        // Wind (private).
        public AccessTools.FieldRef<WindPanel, Vector2> DesiredWind;

        // LMUGC (private).
        public AccessTools.FieldRef<LMUGC, int[]> Pars;

        // Club.powerVisualMult: the top of ClubSwing.m_power for the club (answers #53); access unconfirmed.
        private FieldInfo _powerVisualMult;
        private bool _powerVisualMultResolved;

        public void Resolve(GameContext context)
        {
            _context = context;
            var patcher = context.Patcher;
            DesiredWind = patcher.FieldRef<WindPanel, Vector2>(PresenceFeature.Wind, "m_desiredWind");
            Pars = Optional(() => AccessTools.FieldRefAccess<LMUGC, int[]>("m_pars"), "LMUGC.m_pars", "pars come from hole events only");
        }

        /// <summary>The renderer of the game's ball (its public m_meshRenderer, else the first renderer under it).</summary>
        public Renderer RendererOf(Ball ball)
        {
            if (ball == null)
                return null;
            var renderer = ball.m_meshRenderer;
            if (renderer != null)
                return renderer;
            return ball.GetComponentInChildren<Renderer>();
        }

        /// <summary>A challenge with a fixed wind (public Challenge.m_forceWind, answers #64).</summary>
        public static bool IsWindForced(Challenge challenge) => challenge != null && challenge.m_forceWind;

        /// <summary>Skin material <paramref name="index"/> of <c>Cosmetics.instance.m_balls</c>, or null.</summary>
        public static Material SkinMaterial(int index)
        {
            if (index < 0)
                return null;
            var cosmetics = Cosmetics.instance;
            if (cosmetics == null)
                return null;
            var balls = cosmetics.m_balls;
            if (balls == null || index >= balls.Length)
                return null;
            var entry = balls[index];
            return entry != null ? entry.mat : null;
        }

        /// <summary>
        /// <c>Club.powerVisualMult</c> of <paramref name="club"/>, or 0 when it cannot be read (the field is found by
        /// name once; a numeric field of any type is accepted).
        /// </summary>
        public float PowerVisualMult(Club club)
        {
            if (club == null)
                return 0f;
            if (!_powerVisualMultResolved)
            {
                _powerVisualMultResolved = true;
                _powerVisualMult = AccessTools.Field(typeof(Club), "powerVisualMult");
                if (_powerVisualMult == null)
                    _context?.Warn($"{Log}Club.powerVisualMult not found; shot toasts show no power percentage.");
            }
            if (_powerVisualMult == null)
                return 0f;
            try
            {
                return Convert.ToSingle(_powerVisualMult.GetValue(club));
            }
            catch (Exception e)
            {
                _context?.WarnOnce("pr.powermult", $"{Log}Reading Club.powerVisualMult failed ({e.Message}); no power percentage.");
                _powerVisualMult = null;
                return 0f;
            }
        }

        /// <summary>
        /// The game's keyboard bindings for <paramref name="key"/> (all keyboard bindings when it is None), as
        /// "action: path" (answers #67: the game's code never reads Tab, but its InputSystem asset binds it). Null when
        /// the bindings cannot be read. Read through reflection so only well-known public InputSystem members are needed.
        /// </summary>
        public static List<string> KeyBindings(KeyCode key)
        {
            string suffix = key == KeyCode.None ? null : "/" + InputSystemKeyName(key);
            var manager = InputManager.instance;
            var input = manager != null ? manager.input : null;
            if (input == null)
                return null;
            object asset = input.actions;
            if (asset == null || !(asset is IEnumerable actions))
                return null;
            var found = new List<string>();
            foreach (var action in actions)
            {
                if (action == null)
                    continue;
                var type = action.GetType();
                string name = type.GetProperty("name")?.GetValue(action) as string ?? "?";
                if (!(type.GetProperty("bindings")?.GetValue(action) is IEnumerable bindings))
                    continue;
                foreach (var binding in bindings)
                {
                    if (binding == null)
                        continue;
                    var bindingType = binding.GetType();
                    string path = bindingType.GetProperty("effectivePath")?.GetValue(binding) as string
                        ?? bindingType.GetProperty("path")?.GetValue(binding) as string;
                    if (path == null || !path.StartsWith("<Keyboard>", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (suffix == null || path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        found.Add(name + ": " + path);
                }
            }
            return found;
        }

        /// <summary>The Input System's control name for a legacy key ("tab", "h", "1", "backquote", "f8").</summary>
        public static string InputSystemKeyName(KeyCode key)
        {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9)
                return ((int)(key - KeyCode.Alpha0)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9)
                return "numpad" + (int)(key - KeyCode.Keypad0);
            return key.ToString().ToLowerInvariant();
        }

        private T Optional<T>(Func<T> resolve, string member, string consequence) where T : class
        {
            try
            {
                return resolve();
            }
            catch (Exception e)
            {
                _context.Warn($"{Log}{member} not usable ({e.GetType().Name}); {consequence}.");
                return null;
            }
        }
    }
}
