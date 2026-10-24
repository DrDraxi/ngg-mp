using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Finds game members by name and applies Harmony patches per <em>feature</em> (a short key such as
    /// "wind" or "save.redirect"). Any lookup or patch that fails disables its feature with one log line instead of
    /// throwing; patch bodies check <see cref="IsEnabled"/> and report their own exceptions with
    /// <see cref="ReportFailure"/>, which disables a feature that keeps failing. Main thread only.
    /// <para>Modules never use [HarmonyPatch] attributes; everything goes through this class.</para>
    /// </summary>
    public sealed class GamePatcher
    {
        private const string Log = "[Patches] ";
        private const int FailuresBeforeDisable = 3;

        private readonly Func<Harmony> _harmony;
        private readonly Dictionary<string, string> _disabled = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly List<MethodBase> _patched = new List<MethodBase>();

        public GamePatcher(Func<Harmony> harmony)
        {
            _harmony = harmony ?? throw new ArgumentNullException(nameof(harmony));
        }

        /// <summary>Feature -> why it was disabled.</summary>
        public IReadOnlyDictionary<string, string> DisabledFeatures => _disabled;

        /// <summary>Game methods patched so far.</summary>
        public IReadOnlyList<MethodBase> Patched => _patched;

        public bool IsEnabled(string feature) => feature == null || !_disabled.ContainsKey(feature);

        /// <summary>Turns a feature off for the rest of the run; logged once.</summary>
        public void Disable(string feature, string reason)
        {
            if (feature == null || _disabled.ContainsKey(feature))
                return;
            _disabled.Add(feature, reason ?? string.Empty);
            NetLog.Warn($"{Log}Feature '{feature}' disabled: {reason}");
        }

        /// <summary>
        /// A patch body caught an exception. Logged the first time per feature; the feature is disabled after
        /// <see cref="FailuresBeforeDisable"/> failures so a broken hook cannot spam or destabilize the game.
        /// </summary>
        public void ReportFailure(string feature, Exception error)
        {
            string key = feature ?? string.Empty;
            _failures.TryGetValue(key, out int count);
            _failures[key] = ++count;
            if (count == 1)
                NetLog.Error($"{Log}Feature '{key}' failed: {error}");
            if (count >= FailuresBeforeDisable)
                Disable(key, $"failed {count} times; last error: {error?.GetType().Name}: {error?.Message}");
        }

        /// <summary>A method of the game by name (any access). Null, with the feature disabled, when missing.</summary>
        public MethodInfo Method(string feature, Type type, string name, Type[] parameters = null)
        {
            if (!IsEnabled(feature))
                return null;
            MethodInfo method = null;
            try
            {
                method = type == null ? null : AccessTools.Method(type, name, parameters);
            }
            catch (Exception e)
            {
                Disable(feature, $"looking up {Describe(type, name, parameters)} failed: {e.Message}");
                return null;
            }
            if (method == null)
                Disable(feature, Describe(type, name, parameters) + " not found");
            return method;
        }

        /// <summary>
        /// The first method named <paramref name="name"/> with <paramref name="parameterCount"/> parameters, found
        /// without naming the parameter types (for game types whose declaring scope the map does not pin down).
        /// Null, with the feature disabled, when missing.
        /// </summary>
        public MethodInfo MethodByArity(string feature, Type type, string name, int parameterCount)
        {
            if (!IsEnabled(feature))
                return null;
            MethodInfo method = null;
            try
            {
                method = type == null ? null : AccessTools.FirstMethod(type, m => m.Name == name && m.GetParameters().Length == parameterCount);
            }
            catch (Exception e)
            {
                Disable(feature, $"looking up {type?.Name}.{name} ({parameterCount} parameters) failed: {e.Message}");
                return null;
            }
            if (method == null)
                Disable(feature, $"{type?.Name}.{name} with {parameterCount} parameter(s) not found");
            return method;
        }

        public MethodInfo PropertyGetter(string feature, Type type, string name)
        {
            if (!IsEnabled(feature))
                return null;
            MethodInfo getter = null;
            try
            {
                getter = type == null ? null : AccessTools.PropertyGetter(type, name);
            }
            catch (Exception e)
            {
                Disable(feature, $"looking up {type?.Name}.{name} getter failed: {e.Message}");
                return null;
            }
            if (getter == null)
                Disable(feature, $"{type?.Name}.{name} getter not found");
            return getter;
        }

        public FieldInfo Field(string feature, Type type, string name)
        {
            if (!IsEnabled(feature))
                return null;
            FieldInfo field = null;
            try
            {
                field = type == null ? null : AccessTools.Field(type, name);
            }
            catch (Exception e)
            {
                Disable(feature, $"looking up {type?.Name}.{name} failed: {e.Message}");
                return null;
            }
            if (field == null)
                Disable(feature, $"{type?.Name}.{name} field not found");
            return field;
        }

        /// <summary>A fast typed accessor for an instance field of a class; null (feature disabled) when missing or mistyped.</summary>
        public AccessTools.FieldRef<TObject, TField> FieldRef<TObject, TField>(string feature, string name) where TObject : class
        {
            if (Field(feature, typeof(TObject), name) == null)
                return null;
            try
            {
                return AccessTools.FieldRefAccess<TObject, TField>(name);
            }
            catch (Exception e)
            {
                Disable(feature, $"{typeof(TObject).Name}.{name} is not a {typeof(TField).Name}: {e.Message}");
                return null;
            }
        }

        /// <summary>Patches <paramref name="original"/>; false (feature disabled) if it is null or Harmony fails.</summary>
        public bool Apply(string feature, MethodBase original, HarmonyMethod prefix = null, HarmonyMethod postfix = null,
            HarmonyMethod finalizer = null)
        {
            if (!IsEnabled(feature))
                return false;
            if (original == null)
            {
                Disable(feature, "target method missing");
                return false;
            }
            try
            {
                _harmony().Patch(original, prefix, postfix, null, finalizer, null);
                _patched.Add(original);
                return true;
            }
            catch (Exception e)
            {
                Disable(feature, $"patching {original.DeclaringType?.Name}.{original.Name} failed: {e.Message}");
                NetLog.Debug($"{Log}{e}");
                return false;
            }
        }

        /// <summary>A static patch method of <paramref name="patchType"/> (any access); null if missing.</summary>
        public static HarmonyMethod Hook(Type patchType, string methodName)
        {
            var method = AccessTools.Method(patchType, methodName);
            return method == null ? null : new HarmonyMethod(method);
        }

        public bool Prefix(string feature, MethodBase original, Type patchType, string patchMethod) =>
            ApplyHook(feature, original, patchType, patchMethod, HookKind.Prefix);

        public bool Postfix(string feature, MethodBase original, Type patchType, string patchMethod) =>
            ApplyHook(feature, original, patchType, patchMethod, HookKind.Postfix);

        public bool Finalizer(string feature, MethodBase original, Type patchType, string patchMethod) =>
            ApplyHook(feature, original, patchType, patchMethod, HookKind.Finalizer);

        /// <summary>Looks up <paramref name="method"/> on <paramref name="type"/> and prefixes it.</summary>
        public bool Prefix(string feature, Type type, string method, Type[] parameters, Type patchType, string patchMethod) =>
            Prefix(feature, Method(feature, type, method, parameters), patchType, patchMethod);

        /// <summary>Looks up <paramref name="method"/> on <paramref name="type"/> and postfixes it.</summary>
        public bool Postfix(string feature, Type type, string method, Type[] parameters, Type patchType, string patchMethod) =>
            Postfix(feature, Method(feature, type, method, parameters), patchType, patchMethod);

        private bool ApplyHook(string feature, MethodBase original, Type patchType, string patchMethod, HookKind kind)
        {
            if (!IsEnabled(feature))
                return false;
            var hook = Hook(patchType, patchMethod);
            if (hook == null)
            {
                // A mod bug, not a game change: say so loudly.
                NetLog.Error($"{Log}Patch method {patchType?.Name}.{patchMethod} not found (feature '{feature}').");
                Disable(feature, $"patch method {patchType?.Name}.{patchMethod} missing");
                return false;
            }
            switch (kind)
            {
                case HookKind.Prefix: return Apply(feature, original, prefix: hook);
                case HookKind.Postfix: return Apply(feature, original, postfix: hook);
                default: return Apply(feature, original, finalizer: hook);
            }
        }

        private static string Describe(Type type, string name, Type[] parameters)
        {
            var builder = new StringBuilder();
            builder.Append(type?.Name ?? "null").Append('.').Append(name);
            if (parameters != null)
            {
                builder.Append('(');
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i > 0)
                        builder.Append(", ");
                    builder.Append(parameters[i]?.Name ?? "?");
                }
                builder.Append(')');
            }
            return builder.ToString();
        }

        private enum HookKind
        {
            Prefix,
            Postfix,
            Finalizer
        }
    }
}
