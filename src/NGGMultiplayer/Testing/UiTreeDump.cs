using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using NGGMultiplayer.Game;
using UnityEngine;
using UnityEngine.Events;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// The <c>uitree</c> harness command: the UI hierarchy below every root canvas (also inactive ones) whose path
    /// contains a filter, with each object's components and the values that decide how the game's menus look, sound
    /// and navigate (sprites, fonts, colours, layout, navigation, onClick targets, the fields of the game's own UI
    /// scripts). Read through reflection, so it needs nothing of uGUI or TextMeshPro at compile time. For building the
    /// mod's menus out of the game's own pieces (docs/gamemap/06-menus.md).
    /// </summary>
    internal static class UiTreeDump
    {
        private const int MaxText = 60;
        private const int MaxFields = 40;
        private const int MaxLines = 20000;

        /// <summary>Property names read on any component that has them, in this order.</summary>
        private static readonly string[] Properties =
        {
            // RectTransform
            "anchorMin", "anchorMax", "anchoredPosition", "sizeDelta", "pivot",
            // Canvas, CanvasScaler, CanvasGroup
            "renderMode", "sortingOrder", "overrideSorting", "uiScaleMode", "referenceResolution", "matchWidthOrHeight",
            "alpha", "interactable", "blocksRaycasts",
            // Graphic, Image, TMP
            "color", "raycastTarget", "sprite", "type", "preserveAspect", "material",
            "text", "font", "fontSharedMaterial", "fontSize", "fontStyle", "alignment", "enableAutoSizing", "characterSpacing",
            // Selectable, Button, Toggle, Slider, Scrollbar, ScrollRect, TMP_Dropdown, TMP_InputField
            "transition", "targetGraphic", "colors", "spriteState", "animationTriggers", "navigation", "isOn", "group",
            "value", "minValue", "maxValue", "wholeNumbers", "direction", "content", "viewport", "vertical", "horizontal",
            "movementType", "scrollSensitivity", "options",
            // Layout
            "padding", "spacing", "childAlignment", "childControlWidth", "childControlHeight", "childForceExpandWidth",
            "childForceExpandHeight", "constraint", "constraintCount", "cellSize", "minWidth", "minHeight",
            "preferredWidth", "preferredHeight", "flexibleWidth", "flexibleHeight", "ignoreLayout", "horizontalFit",
            "verticalFit",
            // Animator, AudioSource
            "runtimeAnimatorController", "updateMode", "clip", "outputAudioMixerGroup", "volume", "playOnAwake",
            // EventSystem, input module
            "firstSelectedGameObject", "currentSelectedGameObject", "sendNavigationEvents", "actionsAsset", "move", "submit",
            "cancel", "point", "leftClick"
        };

        private static readonly HashSet<string> SkippedMembers = new HashSet<string>(StringComparer.Ordinal)
        {
            "useGUILayout", "runInEditMode", "enabled", "isActiveAndEnabled", "transform", "gameObject", "tag", "name", "hideFlags"
        };

        /// <summary>
        /// Writes the trees to <paramref name="path"/>; returns a one-line summary. <paramref name="filter"/> (case
        /// insensitive, empty for all) picks the canvases whose path or any descendant's path contains it.
        /// </summary>
        public static string Write(string filter, int maxDepth, string path)
        {
            var sb = new StringBuilder(1 << 16);
            int roots = 0;
            int lines = 0;
            var canvases = new List<Component>();
            var canvasType = Type.GetType("UnityEngine.Canvas, UnityEngine.UIModule");
            if (canvasType != null)
            {
                foreach (var obj in Compat.FindAll<Component>(true))
                {
                    if (obj != null && canvasType.IsInstanceOfType(obj) && IsRootCanvas(obj, canvasType))
                        canvases.Add(obj);
                }
            }
            canvases.Sort((a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
            AppendEventSystems(sb);
            foreach (var canvas in canvases)
            {
                if (!string.IsNullOrEmpty(filter) && !Contains(canvas.transform, filter))
                    continue;
                roots++;
                sb.Append("\n=== ").Append(PathOf(canvas.transform)).Append(" ===\n");
                Append(sb, canvas.transform, 0, maxDepth, ref lines);
                if (lines > MaxLines)
                {
                    sb.Append("... cut at ").Append(MaxLines).Append(" objects\n");
                    break;
                }
            }
            System.IO.File.WriteAllText(path, sb.ToString());
            return $"uitree: {roots} root canvas(es), {lines} object(s) written to {path}";
        }

        private static bool IsRootCanvas(Component canvas, Type canvasType)
        {
            for (var t = canvas.transform.parent; t != null; t = t.parent)
            {
                if (t.GetComponent(canvasType) != null)
                    return false;
            }
            return true;
        }

        private static bool Contains(Transform root, string filter)
        {
            if (PathOf(root).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            for (int i = 0; i < root.childCount; i++)
            {
                if (Contains(root.GetChild(i), filter))
                    return true;
            }
            return false;
        }

        private static void AppendEventSystems(StringBuilder sb)
        {
            var type = Type.GetType("UnityEngine.EventSystems.EventSystem, UnityEngine.UI");
            if (type == null)
                return;
            foreach (var obj in Compat.FindAll<Component>(true))
            {
                if (obj == null || !type.IsInstanceOfType(obj))
                    continue;
                sb.Append("EventSystem ").Append(PathOf(obj.transform)).Append(obj.gameObject.activeInHierarchy ? " (active)" : " (inactive)").Append('\n');
                foreach (var c in obj.GetComponents<Component>())
                    AppendComponent(sb, c, "  ");
            }
        }

        private static void Append(StringBuilder sb, Transform t, int depth, int maxDepth, ref int lines)
        {
            lines++;
            string indent = new string(' ', depth * 2);
            sb.Append(indent).Append(t.name).Append(t.gameObject.activeSelf ? string.Empty : " [inactive]")
                .Append(" layer ").Append(t.gameObject.layer).Append('\n');
            foreach (var c in t.GetComponents<Component>())
                AppendComponent(sb, c, indent + "  - ");
            if (depth >= maxDepth)
            {
                if (t.childCount > 0)
                    sb.Append(indent).Append("  (").Append(t.childCount).Append(" children not shown)\n");
                return;
            }
            for (int i = 0; i < t.childCount && lines <= MaxLines; i++)
                Append(sb, t.GetChild(i), depth + 1, maxDepth, ref lines);
        }

        private static void AppendComponent(StringBuilder sb, Component c, string prefix)
        {
            if (c == null)
            {
                sb.Append(prefix).Append("(missing script)\n");
                return;
            }
            var type = c.GetType();
            sb.Append(prefix).Append(type.FullName);
            if (c is Behaviour b && !b.enabled)
                sb.Append(" (disabled)");
            var parts = new List<string>();
            foreach (var name in Properties)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || !prop.CanRead || prop.GetIndexParameters().Length > 0 || SkippedMembers.Contains(name))
                    continue;
                try
                {
                    parts.Add(name + "=" + Format(prop.GetValue(c, null), 0));
                }
                catch (Exception)
                {
                    // Some getters throw on objects that are not set up (a TMP text before its first layout).
                }
            }
            if (!IsEngineType(type))
                AppendFields(type, c, parts);
            else
                AppendEvents(type, c, parts);
            if (parts.Count > 0)
                sb.Append(": ").Append(string.Join(", ", parts));
            sb.Append('\n');
        }

        private static void AppendEvents(Type type, Component c, List<string> parts)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType) || prop.GetIndexParameters().Length > 0)
                    continue;
                try
                {
                    parts.Add(prop.Name + "=" + Format(prop.GetValue(c, null), 0));
                }
                catch (Exception)
                {
                }
            }
            // EventTrigger keeps its entries in a list field.
            var triggers = type.GetProperty("triggers", BindingFlags.Public | BindingFlags.Instance);
            if (triggers != null)
            {
                try
                {
                    parts.Add("triggers=" + Format(triggers.GetValue(c, null), 0));
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>The game's own scripts: their public and serialized fields.</summary>
        private static void AppendFields(Type type, Component c, List<string> parts)
        {
            int count = 0;
            for (var t = type; t != null && !IsEngineType(t); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized || field.Name.Contains("<"))
                        continue;
                    bool serialized = field.IsPublic || field.GetCustomAttributes(typeof(SerializeField), true).Length > 0;
                    if (!serialized)
                        continue;
                    if (++count > MaxFields)
                    {
                        parts.Add("...");
                        return;
                    }
                    try
                    {
                        parts.Add(field.Name + "=" + Format(field.GetValue(c), 0));
                    }
                    catch (Exception e)
                    {
                        parts.Add(field.Name + "=<" + e.GetType().Name + ">");
                    }
                }
            }
        }

        private static bool IsEngineType(Type type)
        {
            string ns = type.Namespace ?? string.Empty;
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal)
                || ns.StartsWith("System", StringComparison.Ordinal) || type == typeof(MonoBehaviour) || type == typeof(Behaviour)
                || type == typeof(Component) || type == typeof(UnityEngine.Object);
        }

        private static string Format(object value, int depth)
        {
            switch (value)
            {
                case null:
                    return "null";
                case string s:
                    s = s.Replace("\n", "\\n");
                    return "\"" + (s.Length > MaxText ? s.Substring(0, MaxText) + "..." : s) + "\"";
                case UnityEngine.Object o:
                    // Unity's null check: a destroyed or unassigned reference.
                    if (o == null)
                        return "null";
                    return o is Component comp ? comp.GetType().Name + "@" + PathOf(comp.transform) : o.GetType().Name + "'" + o.name + "'";
                case float f:
                    return f.ToString("0.###", CultureInfo.InvariantCulture);
                case double d:
                    return d.ToString("0.###", CultureInfo.InvariantCulture);
                case Vector2 v2:
                    return "(" + Format(v2.x, 0) + "," + Format(v2.y, 0) + ")";
                case Vector3 v3:
                    return "(" + Format(v3.x, 0) + "," + Format(v3.y, 0) + "," + Format(v3.z, 0) + ")";
                case Color col:
                    return "rgba(" + Format(col.r, 0) + "," + Format(col.g, 0) + "," + Format(col.b, 0) + "," + Format(col.a, 0) + ")";
                case UnityEventBase ev:
                    return FormatEvent(ev);
                case Enum e:
                    return e.ToString();
            }
            var type = value.GetType();
            if (type.IsPrimitive)
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            if (depth >= 2)
                return type.Name;
            if (value is IEnumerable list)
            {
                var items = new List<string>();
                foreach (var item in list)
                {
                    if (items.Count >= 12)
                    {
                        items.Add("...");
                        break;
                    }
                    items.Add(Format(item, depth + 1));
                }
                return "[" + string.Join("; ", items) + "]";
            }
            if (type.IsValueType || type.IsSerializable || type.Namespace == null || type.Namespace.StartsWith("UnityEngine", StringComparison.Ordinal))
            {
                var parts = new List<string>();
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || parts.Count >= 16)
                        continue;
                    try
                    {
                        parts.Add(prop.Name + ":" + Format(prop.GetValue(value, null), depth + 1));
                    }
                    catch (Exception)
                    {
                    }
                }
                if (parts.Count == 0)
                {
                    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (parts.Count >= 16)
                            break;
                        parts.Add(field.Name + ":" + Format(field.GetValue(value), depth + 1));
                    }
                }
                return type.Name + "{" + string.Join(" ", parts) + "}";
            }
            return type.Name;
        }

        private static string FormatEvent(UnityEventBase ev)
        {
            int count = ev.GetPersistentEventCount();
            if (count == 0)
                return "{}";
            var calls = new List<string>();
            for (int i = 0; i < count; i++)
            {
                var target = ev.GetPersistentTarget(i);
                string targetText = target == null ? "null" : target is Component comp
                    ? comp.GetType().Name + "@" + PathOf(comp.transform)
                    : target.GetType().Name + "'" + target.name + "'";
                calls.Add(targetText + "." + ev.GetPersistentMethodName(i));
            }
            return "{" + string.Join("; ", calls) + "}";
        }

        private static string PathOf(Transform transform)
        {
            var names = new List<string>();
            for (var t = transform; t != null; t = t.parent)
                names.Add(t.name);
            names.Reverse();
            return transform.gameObject.scene.name + ":" + string.Join("/", names);
        }
    }
}
