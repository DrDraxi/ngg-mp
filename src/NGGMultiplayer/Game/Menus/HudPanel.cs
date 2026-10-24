using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NGGMultiplayer.Game.Menus
{
    /// <summary>
    /// A text panel in the game's HUD style (docs/gamemap/07-hud.md): a copy of the walking HUD's "You have $N" panel
    /// (faint dark rounded box, white Quantico text) on an overlay canvas of its own, scaled like the HUD's, pinned to
    /// one corner or edge. Used by the player list and the notices.
    /// </summary>
    internal sealed class HudPanel
    {
        private static readonly HashSet<string> PanelParts = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "RectTransform", "CanvasRenderer", "Image", "VerticalLayoutGroup", "ContentSizeFitter"
        };

        private static readonly HashSet<string> LabelParts = new HashSet<string>(System.StringComparer.Ordinal)
        {
            "RectTransform", "CanvasRenderer", "TextMeshProUGUI", "ContentSizeFitter"
        };

        public GameObject Root { get; private set; }
        public RectTransform Panel { get; private set; }
        public CanvasGroup Group { get; private set; }
        public TextMeshProUGUI Label { get; private set; }

        /// <summary>The HUD's money panel and the HUD canvas, from the scene's ChallengeReminder; false while not there.</summary>
        public static bool FindTemplate(out GameObject template, out Canvas hud)
        {
            template = null;
            hud = null;
            var reminder = Compat.FindAny<ChallengeReminder>(true);
            if (reminder == null)
                return false;
            template = MenuKit.GetField(reminder, "m_balanceReminder") as GameObject
                       ?? MenuKit.FindDeep(reminder.transform.parent, "YOU HAVE REMINDER")?.gameObject;
            hud = HudCanvas(reminder.transform);
            return template != null && hud != null && template.GetComponentInChildren<TextMeshProUGUI>(true) != null;
        }

        private static Canvas HudCanvas(Transform from)
        {
            for (var t = from; t != null; t = t.parent)
            {
                var canvas = t.GetComponent<Canvas>();
                if (canvas != null)
                    return canvas;
            }
            return null;
        }

        /// <summary>
        /// Builds the panel, hidden (alpha 0), with its <paramref name="anchor"/> point (also its pivot) at
        /// <paramref name="offset"/> from that point of the screen.
        /// </summary>
        public static HudPanel Build(Canvas hud, GameObject template, string name, int sortingOrder, Vector2 anchor,
            Vector2 offset, TextAlignmentOptions alignment)
        {
            var built = new HudPanel();
            // Built inactive: the copied label's text animator must not wake before it is stripped.
            var root = new GameObject(name);
            built.Root = root;
            root.SetActive(false);
            root.layer = template.layer;
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            var hudScaler = hud.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = hudScaler != null ? hudScaler.referenceResolution : new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = hudScaler != null ? hudScaler.matchWidthOrHeight : 0.5f;

            var clone = Object.Instantiate(template, root.transform, false);
            clone.name = "PANEL";
            var label = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            for (int i = clone.transform.childCount - 1; i >= 0; i--)
            {
                var child = clone.transform.GetChild(i);
                if (child != label.transform)
                    Object.DestroyImmediate(child.gameObject);
            }
            MenuKit.Strip(clone, PanelParts);
            MenuKit.Strip(label.gameObject, LabelParts);
            clone.SetActive(true);
            label.gameObject.SetActive(true);

            var panel = clone.GetComponent<RectTransform>();
            panel.anchorMin = panel.anchorMax = panel.pivot = anchor;
            panel.anchoredPosition = offset;
            var group = clone.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            // The game's text animator drew this label's mesh; without it TMP draws it again.
            label.renderMode = TextRenderFlags.Render;
            label.richText = true;
            label.enableAutoSizing = false;
            label.alignment = alignment;
            label.color = Color.white;
            label.raycastTarget = false;
            label.text = string.Empty;
            root.SetActive(true);

            built.Panel = panel;
            built.Group = group;
            built.Label = label;
            return built;
        }

        /// <summary>Shows <paramref name="text"/> (lines may be rich text) and re-lays the panel out around it.</summary>
        public void SetText(string text)
        {
            Label.text = text;
            Label.ForceMeshUpdate();
            LayoutRebuilder.ForceRebuildLayoutImmediate(Panel);
        }
    }
}
