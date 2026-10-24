using System;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Game;
using UnityEngine;

namespace NGGMultiplayer.Runtime
{
    /// <summary>Another player's golfer, with a floating name tag.</summary>
    public sealed class RemotePlayerView : RemoteEntityView
    {
        // Above the directional cutout's cap (2.16 m standing, AvatarSprites.DisplayScale).
        private const float LabelHeight = 2.55f;
        // The same gap above the crouch drawings' cap (1.27 m).
        private const float CrouchLabelHeight = 1.65f;

        private static Font _labelFont;
        private static bool _labelFontLoaded;

        private Transform _label;
        private bool _labelCrouched;

        public RemotePlayerView(PlayerInfo player, GameObject visual, Transform parent)
            : base("NGGMP Player " + (player?.DisplayName ?? "?"), visual ?? CreateDefaultVisual(player), parent)
        {
            CreateLabel(player?.DisplayName ?? "Player");
        }

        private static GameObject CreateDefaultVisual(PlayerInfo player) =>
            CreatePlaceholder(PrimitiveType.Capsule, new Vector3(0.6f, 0.9f, 0.6f), new Vector3(0f, 0.9f, 0f),
                PlayerColor(player?.PlayerIndex ?? 0));

        private void CreateLabel(string text)
        {
            // A TextMesh made from script has no font and its renderer no material, so it would draw nothing.
            var font = LabelFont();
            if (font == null)
                return;
            var label = new GameObject("NameTag");
            label.transform.SetParent(Transform, false);
            label.transform.localPosition = new Vector3(0f, LabelHeight, 0f);
            _label = label.transform;
            var mesh = label.AddComponent<TextMesh>();
            mesh.font = font;
            var renderer = label.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.sharedMaterial = font.material;
            mesh.text = text;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.characterSize = 0.05f;
            mesh.fontSize = 64;
            mesh.color = Color.white;
            label.AddComponent<Billboard>();
        }

        protected override void OnPoseFlags(byte flags)
        {
            bool crouched = (flags & PoseFlags.PlayerCrouched) != 0 && (flags & PoseFlags.PlayerGolf) == 0;
            if (_label == null || crouched == _labelCrouched)
                return;
            _labelCrouched = crouched;
            _label.localPosition = new Vector3(0f, crouched ? CrouchLabelHeight : LabelHeight, 0f);
        }

        private static Font LabelFont()
        {
            if (_labelFontLoaded)
                return _labelFont;
            _labelFontLoaded = true;
            // Unity 2022.2 renamed the built-in font; try the name for this engine line first.
            foreach (var name in new[] { "Arial.ttf", "LegacyRuntime.ttf" })
            {
                try
                {
                    _labelFont = Resources.GetBuiltinResource<Font>(name);
                }
                catch (Exception)
                {
                    _labelFont = null;
                }
                if (_labelFont != null)
                    break;
            }
            if (_labelFont == null)
                NetLog.Warn("[Multiplayer] No built-in font found; player name tags are disabled.");
            return _labelFont;
        }
    }
}
