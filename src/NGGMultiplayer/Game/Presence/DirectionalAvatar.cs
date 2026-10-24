using System;
using System.Collections.Generic;
using System.IO;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The cutouts a remote player is drawn with: PNGs named "&lt;pose&gt;_&lt;direction&gt;[_&lt;frame&gt;].png" (see
    /// <see cref="BillboardAngles.TryParseName"/>) in the "avatar" folder next to the plugin DLL. Every image is a
    /// canvas whose full height is <see cref="CanvasHeightMeters"/> with the feet at its bottom centre, so all poses and
    /// directions share one scale. Poses: "stand" (required), "walk" (moving), "golf" (address stance), "crouch".
    /// The female golfer's drawings sit in the "female" folder inside it, named the same; without them a female
    /// player is drawn as the male golfer. Each set is loaded once, kept for the whole game (the textures survive
    /// scene loads).
    /// </summary>
    internal sealed class AvatarSprites
    {
        public const string FolderName = "avatar";
        public const string FemaleFolderName = "female";
        public const float CanvasHeightMeters = 2f;

        /// <summary>
        /// Drawn this much larger than the canvas says: a 1.8 m golfer reads short next to the game's first-person
        /// camera, so the cutout is about 1.5 heads taller (2.16 m standing), eye to eye with the local player.
        /// </summary>
        public const float DisplayScale = 1.2f;
        public const string StandPose = "stand";

        private static AvatarSprites _shared;
        private static bool _loaded;
        private static AvatarSprites _female;
        private static bool _femaleLoaded;

        // pose -> direction -> frames (null when that direction has no drawing).
        private readonly Dictionary<string, Sprite[][]> _poses = new Dictionary<string, Sprite[][]>();

        public static AvatarSprites Shared
        {
            get
            {
                if (!_loaded)
                {
                    _loaded = true;
                    _shared = Load(null);
                }
                return _shared;
            }
        }

        /// <summary>The drawings of <paramref name="look"/>: the male set when that look has none (null: no cutouts).</summary>
        public static AvatarSprites For(PlayerLook look)
        {
            var male = Shared;
            if (look != PlayerLook.Female || male == null)
                return male;
            if (!_femaleLoaded)
            {
                _femaleLoaded = true;
                _female = Load(FemaleFolderName);
            }
            return _female ?? male;
        }

        public bool Has(string pose) => _poses.ContainsKey(pose);

        /// <summary>The frames to show of <paramref name="pose"/> for a viewer in <paramref name="direction"/>.</summary>
        public bool TryGet(string pose, int direction, out Sprite[] frames, out bool flip)
        {
            frames = null;
            flip = false;
            if (!_poses.TryGetValue(pose, out var directions))
                return false;
            if (!BillboardAngles.Resolve(direction, d => directions[d] != null, out int source, out flip))
                return false;
            frames = directions[source];
            return true;
        }

        private static AvatarSprites Load(string subfolder)
        {
            string folder;
            try
            {
                folder = Path.Combine(Path.GetDirectoryName(typeof(AvatarSprites).Assembly.Location) ?? ".", FolderName);
                if (subfolder != null)
                    folder = Path.Combine(folder, subfolder);
            }
            catch (Exception e)
            {
                GameContext.Current?.Warn($"[Presence] Avatar cutouts: plugin folder unknown ({e.Message}); using shapes.");
                return null;
            }
            if (!Directory.Exists(folder))
            {
                GameContext.Current?.Info(subfolder == null
                    ? $"[Presence] No avatar cutouts at {folder}; remote players are drawn with shapes."
                    : $"[Presence] No {subfolder} avatar cutouts at {folder}; those players are drawn as the male golfer.");
                return null;
            }
            var frames = new Dictionary<string, List<KeyValuePair<int, Sprite>>[]>();
            int count = 0;
            foreach (var file in Directory.GetFiles(folder, "*.png"))
            {
                if (!BillboardAngles.TryParseName(Path.GetFileNameWithoutExtension(file), out string pose, out int direction, out int frame))
                {
                    GameContext.Current?.Warn($"[Presence] Avatar cutout {Path.GetFileName(file)} has an unknown name; skipped.");
                    continue;
                }
                var sprite = LoadSprite(file, subfolder);
                if (sprite == null)
                    continue;
                if (!frames.TryGetValue(pose, out var directions))
                    frames[pose] = directions = new List<KeyValuePair<int, Sprite>>[BillboardAngles.Directions];
                (directions[direction] ?? (directions[direction] = new List<KeyValuePair<int, Sprite>>()))
                    .Add(new KeyValuePair<int, Sprite>(frame, sprite));
                count++;
            }
            if (!frames.ContainsKey(StandPose))
            {
                GameContext.Current?.Warn($"[Presence] Avatar cutouts in {folder} have no '{StandPose}' pose; using shapes.");
                return null;
            }
            var result = new AvatarSprites();
            foreach (var pair in frames)
            {
                var directions = new Sprite[BillboardAngles.Directions][];
                for (int d = 0; d < directions.Length; d++)
                {
                    var list = pair.Value[d];
                    if (list == null)
                        continue;
                    list.Sort((a, b) => a.Key.CompareTo(b.Key));
                    directions[d] = list.ConvertAll(p => p.Value).ToArray();
                }
                result._poses[pair.Key] = directions;
            }
            GameContext.Current?.Info($"[Presence] Loaded {count} avatar cutout(s) ({string.Join(", ", result._poses.Keys)}) from {folder}.");
            return result;
        }

        private static Sprite LoadSprite(string file, string subfolder)
        {
            try
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true)
                {
                    // "NGGMP stand_front", "NGGMP female/stand_front" (the harness's remotes command shows it).
                    name = "NGGMP " + (subfolder != null ? subfolder + "/" : string.Empty) + Path.GetFileNameWithoutExtension(file),
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Trilinear,
                    anisoLevel = 4,
                };
                if (!texture.LoadImage(File.ReadAllBytes(file), markNonReadable: true))
                {
                    Object.Destroy(texture);
                    GameContext.Current?.Warn($"[Presence] Avatar cutout {Path.GetFileName(file)} is not a readable PNG.");
                    return null;
                }
                Object.DontDestroyOnLoad(texture);
                var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0f),
                    texture.height / (CanvasHeightMeters * DisplayScale), 0, SpriteMeshType.FullRect);
                sprite.name = texture.name;
                Object.DontDestroyOnLoad(sprite);
                return sprite;
            }
            catch (Exception e)
            {
                GameContext.Current?.Warn($"[Presence] Loading avatar cutout {Path.GetFileName(file)} failed: {e.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// A remote golfer drawn as a directional cutout: an upright sprite turned to face each camera that renders it,
    /// showing the drawing of the side that camera sees (front, back, sides, three-quarters; mirrored where only one
    /// side is drawn). Turned per camera, so the PIP ball camera sees its own angle too. The avatar root carries the
    /// owner's facing (yaw); the sprite child ignores it and faces the camera instead. When the owner's shot is heard
    /// it plays the swing (<see cref="SwingTimeline"/>) if the "swing" drawings are installed.
    /// </summary>
    public sealed class DirectionalAvatar : MonoBehaviour, IRemoteVisual
    {
        private const float WalkSpeed = 0.4f;
        private const float FramesPerSecond = 12f;

        private static readonly List<DirectionalAvatar> Active = new List<DirectionalAvatar>();
        private static readonly Dictionary<PeerId, PlayerLook> Looks = new Dictionary<PeerId, PlayerLook>();
        private static bool _hooked;
        private static bool _shaderLogged;
        private static Material _fallbackMaterial;

        private AvatarSprites _sprites;
        private Transform _sprite;
        private SpriteRenderer _renderer;
        private string _pose = AvatarSprites.StandPose;
        private int _direction = -1;
        private float _animStart;
        private bool _bobbing;
        private bool _crouchStepping;
        private float _roll;
        private PeerId _owner = PeerId.None;
        private float _swingStart = float.NegativeInfinity;

        /// <summary>A billboard avatar, or null when no cutouts are installed (the caller then builds shapes).</summary>
        internal static GameObject TryCreate(PlayerInfo player)
        {
            var owner = player?.Id ?? PeerId.None;
            var sprites = AvatarSprites.For(owner.IsValid && Looks.TryGetValue(owner, out var look) ? look : PlayerLook.Male);
            if (sprites == null)
                return null;
            var root = new GameObject("NGGMP Avatar") { layer = RemoteVisuals.IgnoreRaycastLayer };
            var child = new GameObject("Cutout") { layer = RemoteVisuals.IgnoreRaycastLayer };
            child.transform.SetParent(root.transform, false);
            var renderer = child.AddComponent<SpriteRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            if (renderer.sharedMaterial == null)
            {
                // A script-made SpriteRenderer normally gets the pipeline's default sprite material; if not, the
                // built-in one (always included in builds).
                if (_fallbackMaterial == null)
                {
                    var shader = Shader.Find("Sprites/Default");
                    if (shader != null)
                        _fallbackMaterial = new Material(shader) { name = "NGGMP Cutout" };
                }
                renderer.sharedMaterial = _fallbackMaterial;
            }
            var avatar = root.AddComponent<DirectionalAvatar>();
            avatar._sprites = sprites;
            avatar._sprite = child.transform;
            avatar._renderer = renderer;
            avatar._owner = player?.Id ?? PeerId.None;
            avatar.Show(0f);
            return root;
        }

        public void OnRemotePose(byte flags, Vector3 velocity, bool teleport)
        {
            string pose;
            bool golf = (flags & PoseFlags.PlayerGolf) != 0;
            velocity.y = 0f;
            bool moving = !golf && velocity.sqrMagnitude > WalkSpeed * WalkSpeed;
            bool crouched = !golf && (flags & PoseFlags.PlayerCrouched) != 0;
            // Crouched wins over walking: a crouch-walker stays low (and waddles, see StepMotion).
            if (golf)
                pose = "golf";
            else if (crouched && _sprites.Has("crouch"))
                pose = "crouch";
            else if (moving)
                pose = "walk";
            else
                pose = AvatarSprites.StandPose;
            if (!_sprites.Has(pose))
                pose = AvatarSprites.StandPose;
            bool bobbing = moving && pose != "walk";
            if (bobbing && !_bobbing)
                _animStart = Time.time;  // start on a planted foot
            _bobbing = bobbing;
            _crouchStepping = _bobbing && pose == "crouch";
            if (pose != _pose)
            {
                _pose = pose;
                _animStart = Time.time;
            }
        }

        /// <summary>
        /// <paramref name="owner"/> chose another look: their cutouts show that golfer from now on (one hidden right
        /// now catches up when it is shown again).
        /// </summary>
        internal static void SetLook(PeerId owner, PlayerLook look)
        {
            if (!owner.IsValid)
                return;
            Looks[owner] = look;
            foreach (var avatar in Active)
            {
                if (avatar != null && avatar._owner == owner)
                    avatar.ApplyLook();
            }
        }

        internal static void ForgetLook(PeerId owner) => Looks.Remove(owner);

        internal static void ForgetLooks() => Looks.Clear();

        private void ApplyLook()
        {
            if (_sprites == null)
                return;
            var sprites = AvatarSprites.For(_owner.IsValid && Looks.TryGetValue(_owner, out var look) ? look : PlayerLook.Male);
            if (sprites == null || sprites == _sprites)
                return;
            _sprites = sprites;
            if (!sprites.Has(_pose))
                _pose = AvatarSprites.StandPose;
            Show(Time.time - _animStart);
        }

        /// <summary>
        /// <paramref name="owner"/> hit a shot (heard at impact): their cutout swings, unless it was a putt or no swing
        /// drawings are installed.
        /// </summary>
        internal static void PlaySwing(PeerId owner, string club)
        {
            if (!owner.IsValid || SwingTimeline.IsPutt(club))
                return;
            foreach (var avatar in Active)
            {
                if (avatar != null && avatar._owner == owner && avatar._sprites.Has(SwingTimeline.Pose))
                    avatar._swingStart = Time.time;
            }
        }

        private void OnEnable()
        {
            Active.Add(this);
            Hook();
            ApplyLook();
        }

        private void OnDisable() => Active.Remove(this);

        private void Update()
        {
            float lift = 0f;
            _roll = 0f;
            if (_bobbing)
                StepMotion.Sample(Time.time - _animStart, _crouchStepping, out lift, out _roll);
            var local = _sprite.localPosition;
            if (!Mathf.Approximately(local.y, lift))
                _sprite.localPosition = new Vector3(local.x, lift, local.z);
        }

        private static void Hook()
        {
            if (_hooked)
                return;
            _hooked = true;
            // URP fires beginCameraRendering before it culls each camera; the built-in pipeline has onPreCull.
            RenderPipelineManager.beginCameraRendering += (_, camera) => FaceAll(camera);
            Camera.onPreCull += camera =>
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    FaceAll(camera);
            };
        }

        private static void FaceAll(Camera camera)
        {
            if (camera == null)
                return;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                var avatar = Active[i];
                if (avatar == null)
                {
                    Active.RemoveAt(i);
                    continue;
                }
                try
                {
                    avatar.Face(camera.transform.position);
                }
                catch (Exception e)
                {
                    // Never let a broken avatar break rendering; it stays as it was last drawn.
                    GameContext.Current?.WarnOnce("pr.avatar.face", $"[Presence] Turning an avatar cutout failed: {e.Message}");
                    Active.RemoveAt(i);
                }
            }
        }

        private void Face(Vector3 viewer)
        {
            var position = transform.position;
            var forward = transform.forward;
            float facing = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float yaw = BillboardAngles.RelativeYaw(position.x, position.z, facing, viewer.x, viewer.z);
            _direction = BillboardAngles.Direction(yaw, _direction);
            var away = position - viewer;
            away.y = 0f;
            if (away.sqrMagnitude > 1e-6f)
                _sprite.rotation = Quaternion.LookRotation(away, Vector3.up) * Quaternion.Euler(0f, 0f, _roll);
            Show(Time.time - _animStart);
        }

        private void Show(float animTime)
        {
            int direction = Math.Max(_direction, 0);
            Sprite[] frames;
            bool flip;
            int frame;
            if (SwingTimeline.Sample(Time.time - _swingStart, out string swingPose, out int swingFrame) &&
                _sprites.TryGet(swingPose, direction, out frames, out flip))
            {
                frame = Math.Min(swingFrame, frames.Length - 1);
            }
            else
            {
                if (!_sprites.TryGet(_pose, direction, out frames, out flip) &&
                    !_sprites.TryGet(AvatarSprites.StandPose, direction, out frames, out flip))
                    return;
                frame = frames.Length > 1 ? (int)(Mathf.Max(animTime, 0f) * FramesPerSecond) % frames.Length : 0;
            }
            if (_renderer.sprite != frames[frame])
                _renderer.sprite = frames[frame];
            if (_renderer.flipX != flip)
                _renderer.flipX = flip;
            if (!_shaderLogged)
            {
                _shaderLogged = true;
                var material = _renderer.sharedMaterial;
                GameContext.Current?.Info($"[Presence] Avatar cutouts draw with shader '{(material != null && material.shader != null ? material.shader.name : "none")}'.");
            }
        }
    }
}
