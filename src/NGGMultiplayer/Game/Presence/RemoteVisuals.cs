using System;
using System.Collections.Generic;
using NGGMultiplayer.Core.Net;
using NGGMultiplayer.Core.Session;
using NGGMultiplayer.Runtime;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// Builds other players' avatars and ghost balls (spec 7.2, 7.3) from primitives: no colliders (removed with
    /// DestroyImmediate before a physics step can see them), layer "Ignore Raycast", no Rigidbody, no Ball component
    /// and none of the names the game's triggers look for (LukeVideo's trigger takes any collider, answers #97: see
    /// <see cref="EnsureNoPhysics"/>). Materials come from the game's own ball (a lit material
    /// that matches the render pipeline) and its skins; both are resolved lazily because visuals can be created
    /// before the world is loaded. A ghost ball draws a trail in its owner's colour (<see cref="GhostTrail"/>), styled
    /// like the game ball's own <c>Ball.m_trail</c> when that can be read.
    /// </summary>
    internal sealed class RemoteVisuals
    {
        public const int IgnoreRaycastLayer = 2;

        private readonly GameContext _context;
        private readonly PresenceAccess _access;
        private readonly List<GhostBallVisual> _ghosts = new List<GhostBallVisual>();
        private readonly Dictionary<PeerId, int> _skins = new Dictionary<PeerId, int>();
        private Material _baseMaterial;
        private float _ballDiameter;
        private TrailLook _gameTrailLook;
        private TrailLook _fallbackTrailLook;

        public RemoteVisuals(GameContext context, PresenceAccess access)
        {
            _context = context;
            _access = access;
        }

        public GameObject CreatePlayer(PlayerInfo player)
        {
            // The directional cutout when its images are installed; shapes otherwise.
            var cutout = DirectionalAvatar.TryCreate(player);
            if (cutout != null)
                return cutout;
            var root = new GameObject("NGGMP Avatar") { layer = IgnoreRaycastLayer };
            var pose = new GameObject("Pose") { layer = IgnoreRaycastLayer };
            pose.transform.SetParent(root.transform, false);
            var body = Part(PrimitiveType.Capsule, "Body", pose.transform);
            var head = Part(PrimitiveType.Sphere, "Head", pose.transform);
            var club = Part(PrimitiveType.Cylinder, "Club", pose.transform);
            var avatar = root.AddComponent<AvatarVisual>();
            avatar.Initialize(this, pose.transform, body, head, club, PlayerColors.For(player?.PlayerIndex ?? 0));
            EnsureNoPhysics(root);
            return root;
        }

        public GameObject CreateBall(PlayerInfo player)
        {
            var root = new GameObject("NGGMP Ghost") { layer = IgnoreRaycastLayer };
            var sphere = Part(PrimitiveType.Sphere, "GhostBall", root.transform);
            sphere.transform.localScale = Vector3.one * (_ballDiameter > 0f ? _ballDiameter : GhostBallRules.FallbackDiameter);
            var ghost = root.AddComponent<GhostBallVisual>();
            var owner = player?.Id ?? PeerId.None;
            ghost.Initialize(this, owner, sphere.GetComponent<Renderer>(), PlayerColors.For(player?.PlayerIndex ?? 0));
            if (owner.IsValid && _skins.TryGetValue(owner, out int skin))
                ghost.SetSkin(skin);
            _ghosts.Add(ghost);
            EnsureNoPhysics(root);
            return root;
        }

        /// <summary>The owner's selected ball changed (ngg.pr.info): swap the skin of their ghost.</summary>
        public void SetSkin(PeerId owner, int skin)
        {
            if (!owner.IsValid)
                return;
            _skins[owner] = skin;
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                var ghost = _ghosts[i];
                if (ghost == null)
                {
                    _ghosts.RemoveAt(i);
                    continue;
                }
                if (ghost.Owner == owner)
                    ghost.SetSkin(skin);
            }
        }

        public void ForgetSkin(PeerId owner) => _skins.Remove(owner);

        /// <summary>The owner's look changed (ngg.pr.info): their cutout switches to that golfer's drawings.</summary>
        public void SetLook(PeerId owner, PlayerLook look) => DirectionalAvatar.SetLook(owner, look);

        public void ForgetLook(PeerId owner) => DirectionalAvatar.ForgetLook(owner);

        /// <summary>
        /// A new world loaded: let every visual pick up materials again (skins may now be resolvable, and a trail that
        /// fell back to the mod's own look may now find the game ball's).
        /// </summary>
        public void RefreshMaterials()
        {
            _ballDiameter = 0f;
            for (int i = _ghosts.Count - 1; i >= 0; i--)
            {
                if (_ghosts[i] == null)
                    _ghosts.RemoveAt(i);
                else
                    _ghosts[i].Invalidate();
            }
        }

        public void Clear()
        {
            _skins.Clear();
            DirectionalAvatar.ForgetLooks();
            _ghosts.RemoveAll(g => g == null);
        }

        /// <summary>A copy-able lit material: the game ball's. Null until the world (and its ball) exists.</summary>
        internal Material BaseMaterial()
        {
            if (_baseMaterial != null)
                return _baseMaterial;
            var hit = HitManager.instance;
            var ball = hit != null ? hit.m_ball : null;
            var renderer = _access.RendererOf(ball);
            if (renderer == null || renderer.sharedMaterial == null)
                return null;
            // A private copy survives the scene that owned the original.
            _baseMaterial = new Material(renderer.sharedMaterial) { name = "NGGMP Base" };
            return _baseMaterial;
        }

        /// <summary>
        /// The world's own ball's drawn diameter (metres: its mesh's bounds times its scale, which a rolling ball's
        /// rotation does not change; <see cref="GhostBallRules.DiameterFromMesh"/>), so other players' balls look as big
        /// as it at the same distance. 0 until it can be measured.
        /// </summary>
        internal float GameBallDiameter()
        {
            if (_ballDiameter > 0f)
                return _ballDiameter;
            var hit = HitManager.instance;
            var ball = hit != null ? hit.m_ball : null;
            var renderer = _access.RendererOf(ball);
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return 0f;
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            float diameter;
            if (mesh != null)
            {
                var meshSize = mesh.bounds.size;
                var scale = renderer.transform.lossyScale;
                diameter = GhostBallRules.DiameterFromMesh(meshSize.x, meshSize.y, meshSize.z, scale.x, scale.y, scale.z);
            }
            else
            {
                // No mesh to read: the world bounds, which a rotated ball can overstate (up to sqrt(3)).
                var size = renderer.bounds.size;
                diameter = GhostBallRules.Diameter(size.x, size.y, size.z);
            }
            if (diameter <= 0f)
                return 0f;
            _ballDiameter = diameter;
            _context?.Info($"[Presence] Other players' balls are drawn {Invariant(diameter)} m across, like the world's ball.");
            return diameter;
        }

        // ---- Ghost ball trail ------------------------------------------------------------------------

        /// <summary>[Presence] RemoteBallTrail is on and the trail feature has not been disabled by failures.</summary>
        internal bool TrailEnabled =>
            _context != null && _context.RemoteBallTrail && _context.Patcher.IsEnabled(PresenceFeature.Trail);

        /// <summary>The trail's time: [Presence] TrailSeconds, else the game ball trail's <paramref name="gameSeconds"/>.</summary>
        internal float TrailSeconds(float gameSeconds) =>
            GhostTrailRules.Seconds(_context != null ? _context.TrailSeconds : 0f, gameSeconds);

        internal void ReportTrailFailure(Exception e) => _context?.Patcher.ReportFailure(PresenceFeature.Trail, e);

        /// <summary>
        /// How a ghost trail looks: the game ball's own trail (public <c>Ball.m_trail</c>, else <c>m_chasingTrail</c>,
        /// answers #6) once the world has a ball, else the mod's fallback (an unlit transparent material the render
        /// pipeline draws). Null only while no usable material exists at all: no trail then, never a null material.
        /// </summary>
        internal TrailLook GetTrailLook()
        {
            if (_gameTrailLook != null)
                return _gameTrailLook;
            var hit = HitManager.instance;
            var ball = hit != null ? hit.m_ball : null;
            if (ball != null)
            {
                var trail = ball.m_trail;
                string field = "m_trail";
                if (trail == null)
                {
                    trail = ball.m_chasingTrail;
                    field = "m_chasingTrail";
                }
                if (trail != null)
                {
                    bool own = trail.sharedMaterial != null;
                    var material = own ? new Material(trail.sharedMaterial) { name = "NGGMP Trail Template" } : FallbackTrailMaterial();
                    if (material != null)
                    {
                        _gameTrailLook = TrailLook.From(trail, material);
                        _context?.Info($"[Presence] Ghost ball trail: the game's Ball.{field} look (shader '{ShaderName(material)}'" +
                            (own ? string.Empty : ", the mod's: the game's trail has no material") +
                            $", width {Invariant(trail.widthMultiplier)} m, time {Invariant(trail.time)} s).");
                        return _gameTrailLook;
                    }
                }
                else
                {
                    _context?.WarnOnce("pr.trail.none",
                        "[Presence] The game ball has no trail (m_trail, m_chasingTrail); ghost trails use the mod's look.");
                }
            }
            if (_fallbackTrailLook == null)
            {
                var material = FallbackTrailMaterial();
                if (material != null)
                {
                    _fallbackTrailLook = TrailLook.Fallback(material);
                    _context?.Info($"[Presence] Ghost ball trail: the mod's look (shader '{ShaderName(material)}') until the game ball's trail can be read.");
                }
            }
            if (_fallbackTrailLook == null)
                _context?.WarnOnce("pr.trail.shader", "[Presence] No material for ghost ball trails yet; they appear once the world's ball can be read.");
            return _fallbackTrailLook;
        }

        /// <summary>
        /// Shaders tried for the mod's own trail material, in order: URP's (the game is URP, answers #130), then built-in
        /// ones URP still draws (no LightMode tag) that are in every build's always-included list.
        /// </summary>
        internal static readonly string[] TrailShaders =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Sprites/Default",
            "Universal Render Pipeline/Unlit",
            "Legacy Shaders/Particles/Alpha Blended",
            "Unlit/Color",
        };

        /// <summary>
        /// The first supported shader of <see cref="TrailShaders"/>, set up for alpha blending; else a copy of the ball's
        /// lit material (opaque, but drawn); else null.
        /// </summary>
        private Material FallbackTrailMaterial()
        {
            foreach (var name in TrailShaders)
            {
                var shader = Shader.Find(name);
                if (shader == null || !shader.isSupported)
                    continue;
                var material = new Material(shader) { name = "NGGMP Trail Template" };
                MakeTransparent(material);
                return material;
            }
            var lit = BaseMaterial();
            return lit != null ? new Material(lit) { name = "NGGMP Trail Template" } : null;
        }

        /// <summary>URP surface options for alpha blending (nothing on shaders without them).</summary>
        private static void MakeTransparent(Material material)
        {
            if (!material.HasProperty("_Surface"))
                return;
            material.SetFloat("_Surface", 1f);
            SetIfPresent(material, "_Blend", 0f);
            SetIfPresent(material, "_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            SetIfPresent(material, "_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            SetIfPresent(material, "_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            SetIfPresent(material, "_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            SetIfPresent(material, "_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.SetOverrideTag("RenderType", "Transparent");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        private static void SetIfPresent(Material material, string property, float value)
        {
            if (material.HasProperty(property))
                material.SetFloat(property, value);
        }

        /// <summary>
        /// A ghost's own copy of <paramref name="template"/> in <paramref name="color"/>, through the first tint property
        /// it has (_BaseColor, _TintColor, _Color; the material's alpha is kept). <paramref name="tinted"/> false: the
        /// shader has none, so the colour has to come from the trail's vertex colours.
        /// </summary>
        internal static Material TrailMaterial(Material template, Color color, out bool tinted)
        {
            var material = new Material(template) { name = "NGGMP Trail" };
            tinted = Tint(material, "_BaseColor", color) || Tint(material, "_TintColor", color) || Tint(material, "_Color", color);
            return material;
        }

        private static bool Tint(Material material, string property, Color color)
        {
            if (!material.HasProperty(property))
                return false;
            var current = material.GetColor(property);
            material.SetColor(property, new Color(color.r, color.g, color.b, current.a));
            return true;
        }

        private static string ShaderName(Material material) =>
            material != null && material.shader != null ? material.shader.name : "?";

        private static string Invariant(float value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        internal Material SkinMaterial(int skin)
        {
            try
            {
                return PresenceAccess.SkinMaterial(skin);
            }
            catch (Exception e)
            {
                GameContext.Current?.WarnOnce("pr.skin.read", $"[Presence] Reading ball skin {skin} failed: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// A flat copy of <paramref name="source"/> in <paramref name="color"/>. The game is URP and tints like its own
        /// ObjectStates: _BaseColor first, else _Color (answers #130); which one was used is logged once.
        /// </summary>
        internal static Material Tinted(Material source, Color color)
        {
            var material = new Material(source) { name = "NGGMP Avatar" };
            // Flat colour: drop the ball's texture (dimples) so the player colour reads clearly.
            if (material.HasProperty("_BaseMap"))
                material.SetTexture("_BaseMap", null);
            if (material.HasProperty("_MainTex"))
                material.SetTexture("_MainTex", null);
            string tint;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
                tint = "_BaseColor";
            }
            else if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
                tint = "_Color";
            }
            else
            {
                tint = null;
            }
            if (!_tintLogged)
            {
                _tintLogged = true;
                string shader = material.shader != null ? material.shader.name : "?";
                GameContext.Current?.Info(tint != null
                    ? $"[Presence] Avatar material: shader '{shader}', tinted through {tint}."
                    : $"[Presence] Avatar material: shader '{shader}' has neither _BaseColor nor _Color; avatars keep the ball's colour.");
            }
            return material;
        }

        private static bool _tintLogged;
        private static bool _physicsLogged;

        /// <summary>
        /// No collider and no Rigidbody anywhere under a visual: LukeVideo.OnTriggerEnter takes any collider (it would
        /// end the run for the whole session, answers #97), and so may other triggers. A trigger event needs a collider
        /// on both sides, so a visual without any can never fire one. Logged once (runtime check of #97).
        /// </summary>
        internal static void EnsureNoPhysics(GameObject root)
        {
            if (root == null)
                return;
            int removed = 0;
            foreach (var collider in root.GetComponentsInChildren<Collider>(true))
            {
                Object.DestroyImmediate(collider);
                removed++;
            }
            foreach (var body in root.GetComponentsInChildren<Rigidbody>(true))
            {
                Object.DestroyImmediate(body);
                removed++;
            }
            if (removed > 0)
            {
                GameContext.Current?.Warn($"[Presence] Removed {removed} collider/Rigidbody component(s) left on {root.name}.");
            }
            else if (!_physicsLogged)
            {
                _physicsLogged = true;
                GameContext.Current?.Info($"[Presence] Verified: {root.name} has no colliders and no Rigidbody.");
            }
        }

        private static GameObject Part(PrimitiveType type, string name, Transform parent)
        {
            var go = GameObject.CreatePrimitive(type);
            // Immediately, not Destroy: a collider alive until the end of the frame could already fire triggers.
            foreach (var collider in go.GetComponents<Collider>())
                Object.DestroyImmediate(collider);
            go.name = name;
            go.layer = IgnoreRaycastLayer;
            go.transform.SetParent(parent, false);
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                renderer.receiveShadows = true;
            }
            return go;
        }
    }

    /// <summary>Player colours (<see cref="PlayerPalette"/>): avatar tint, name tag, placeholders and ghost trails.</summary>
    internal static class PlayerColors
    {
        public static Color For(int playerIndex)
        {
            PlayerPalette.Get(playerIndex, out float r, out float g, out float b);
            return new Color(r, g, b);
        }
    }

    /// <summary>
    /// A remote golfer: upright with the club hidden while walking, leaning into a stance with the club out in golf
    /// mode, squashed to 0.6 of its height when crouched.
    /// </summary>
    public sealed class AvatarVisual : MonoBehaviour, IRemoteVisual
    {
        private const float BodyHeight = 1.45f;
        private const float BodyWidth = 0.45f;
        private const float HeadSize = 0.3f;
        private const float CrouchScale = 0.6f;
        private const float StanceLean = 18f;
        private const float MaterialRetrySeconds = 1f;

        private RemoteVisuals _owner;
        private Transform _pose;
        private Transform _body;
        private Transform _head;
        private GameObject _club;
        private Renderer[] _renderers;
        private Color _color;
        private Material _material;
        private float _nextMaterialTry;
        private int _shape = -1;

        internal void Initialize(RemoteVisuals owner, Transform pose, GameObject body, GameObject head, GameObject club, Color color)
        {
            _owner = owner;
            _pose = pose;
            _body = body.transform;
            _head = head.transform;
            _club = club;
            _color = color;
            _renderers = new[] { body.GetComponent<Renderer>(), head.GetComponent<Renderer>(), club.GetComponent<Renderer>() };
            ApplyShape(golf: false, crouched: false);
            TryMaterial();
        }

        public void OnRemotePose(byte flags, Vector3 velocity, bool teleport)
        {
            bool golf = (flags & PoseFlags.PlayerGolf) != 0;
            bool crouched = !golf && (flags & PoseFlags.PlayerCrouched) != 0;
            ApplyShape(golf, crouched);
            if (_material == null && Time.unscaledTime >= _nextMaterialTry)
                TryMaterial();
        }

        private void ApplyShape(bool golf, bool crouched)
        {
            int shape = golf ? 2 : crouched ? 1 : 0;
            if (shape == _shape || _pose == null)
                return;
            _shape = shape;
            float height = crouched ? BodyHeight * CrouchScale : BodyHeight;
            // Capsule and cylinder primitives are 2 m tall and 1 m wide; the sphere 1 m.
            _body.localScale = new Vector3(BodyWidth, height * 0.5f, BodyWidth);
            _body.localPosition = new Vector3(0f, height * 0.5f, 0f);
            _head.localScale = Vector3.one * HeadSize;
            _head.localPosition = new Vector3(0f, height + HeadSize * 0.5f, 0f);
            _pose.localRotation = golf ? Quaternion.Euler(StanceLean, 0f, 0f) : Quaternion.identity;
            _club.SetActive(golf);
            if (golf)
            {
                // From the hands down to the ground in front of the feet.
                _club.transform.localScale = new Vector3(0.025f, 0.55f, 0.025f);
                _club.transform.localPosition = new Vector3(0f, 0.5f, 0.35f);
                _club.transform.localRotation = Quaternion.Euler(-25f, 0f, 0f);
            }
        }

        private void TryMaterial()
        {
            _nextMaterialTry = Time.unscaledTime + MaterialRetrySeconds;
            var source = _owner?.BaseMaterial();
            if (source == null)
                return;
            _material = RemoteVisuals.Tinted(source, _color);
            foreach (var renderer in _renderers)
            {
                if (renderer != null)
                    renderer.sharedMaterial = _material;
            }
        }

        private void OnDestroy()
        {
            if (_material != null)
                Destroy(_material);
        }
    }

    /// <summary>A remote ball: the owner's skin, hidden while the ball is holed, with a trail in the owner's colour.</summary>
    public sealed class GhostBallVisual : MonoBehaviour, IRemoteVisual
    {
        private const float MaterialRetrySeconds = 1f;

        private RemoteVisuals _owner;
        private Renderer _renderer;
        private GhostTrail _trail;
        private int _skin = -1;
        private bool _resolved;
        private bool _sized;
        private float _nextTry;

        internal PeerId Owner { get; private set; }

        internal void Initialize(RemoteVisuals owner, PeerId peer, Renderer renderer, Color color)
        {
            _owner = owner;
            Owner = peer;
            _renderer = renderer;
            try
            {
                _trail = new GhostTrail(owner, transform, color);
            }
            catch (Exception e)
            {
                // The ghost works without its trail.
                owner?.ReportTrailFailure(e);
                _trail = null;
            }
            Resolve();
        }

        internal void SetSkin(int skin)
        {
            if (skin == _skin && _resolved)
                return;
            _skin = skin;
            _resolved = false;
            Resolve();
        }

        internal void Invalidate()
        {
            _resolved = false;
            _sized = false;
            _nextTry = 0f;
            _trail?.Invalidate();
        }

        public void OnRemotePose(byte flags, Vector3 velocity, bool teleport)
        {
            if (_trail != null)
            {
                try
                {
                    _trail.OnPose(flags, teleport, transform.position);
                }
                catch (Exception e)
                {
                    // Never let the trail cost the ghost its poses (the remote view drops a visual that throws).
                    _owner?.ReportTrailFailure(e);
                    _trail.TryHide();
                }
            }
            if (_renderer == null)
                return;
            bool holed = (flags & PoseFlags.BallHoled) != 0;
            if (_renderer.enabled == holed)
                _renderer.enabled = !holed;
            if ((!_resolved || !_sized) && Time.unscaledTime >= _nextTry)
                Resolve();
        }

        private void Resolve()
        {
            _nextTry = Time.unscaledTime + MaterialRetrySeconds;
            if (_renderer == null || _owner == null)
                return;
            if (!_sized)
            {
                // The sphere primitive is 1 m across.
                float diameter = _owner.GameBallDiameter();
                if (diameter > 0f)
                {
                    _renderer.transform.localScale = Vector3.one * diameter;
                    _sized = true;
                }
            }
            // Out of range or not yet known: the default skin, then the game ball's own material. (No ?? here:
            // a destroyed Material only compares equal to null through Unity's operator.)
            var skin = _skin >= 0 ? _owner.SkinMaterial(_skin) : null;
            var material = skin;
            if (material == null)
                material = _owner.SkinMaterial(0);
            if (material == null)
                material = _owner.BaseMaterial();
            if (material == null)
                return;
            _renderer.sharedMaterial = material;
            _resolved = _skin < 0 || skin != null;
        }

        // Hidden (holed or killed, owner in a menu or loading) or being destroyed: the next pose starts a fresh trail,
        // never a streak from where the ghost was last seen.
        private void OnDisable() => _trail?.Reset();

        private void OnDestroy() => _trail?.Destroy();
    }

    /// <summary>The style a <see cref="GhostTrail"/> copies: the game ball trail's, or the mod's fallback.</summary>
    internal sealed class TrailLook
    {
        private const float FallbackWidth = 0.08f;
        private const float FallbackMinVertexDistance = 0.05f;

        /// <summary>Template material (a private copy; never null). Each ghost tints its own copy of it.</summary>
        public Material Material;

        /// <summary>Read from the game's ball. A fallback look is replaced once the game's can be read.</summary>
        public bool FromGame;

        public AnimationCurve WidthCurve;
        public float WidthMultiplier;

        /// <summary>The game trail's time in seconds; 0 when unknown.</summary>
        public float Seconds;

        public GradientAlphaKey[] Alpha;
        public int CapVertices;
        public int CornerVertices;
        public float MinVertexDistance;
        public LineAlignment Alignment;
        public LineTextureMode TextureMode;

        public static TrailLook From(TrailRenderer trail, Material material)
        {
            var gradient = trail.colorGradient;
            var alpha = gradient != null ? gradient.alphaKeys : null;
            var curve = trail.widthCurve;
            return new TrailLook
            {
                Material = material,
                FromGame = true,
                WidthCurve = curve != null && curve.length > 0 ? new AnimationCurve(curve.keys) : FallbackCurve(),
                WidthMultiplier = trail.widthMultiplier > 0f ? trail.widthMultiplier : FallbackWidth,
                Seconds = trail.time,
                Alpha = alpha != null && alpha.Length > 0 ? alpha : DefaultAlpha(),
                CapVertices = trail.numCapVertices,
                CornerVertices = trail.numCornerVertices,
                MinVertexDistance = trail.minVertexDistance > 0f ? trail.minVertexDistance : FallbackMinVertexDistance,
                Alignment = trail.alignment,
                TextureMode = trail.textureMode,
            };
        }

        public static TrailLook Fallback(Material material) => new TrailLook
        {
            Material = material,
            FromGame = false,
            WidthCurve = FallbackCurve(),
            WidthMultiplier = FallbackWidth,
            Seconds = 0f,
            Alpha = DefaultAlpha(),
            CapVertices = 2,
            CornerVertices = 0,
            MinVertexDistance = FallbackMinVertexDistance,
            Alignment = LineAlignment.View,
            TextureMode = LineTextureMode.Stretch,
        };

        private static AnimationCurve FallbackCurve() => new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.15f));

        private static GradientAlphaKey[] DefaultAlpha() => new[] { new GradientAlphaKey(0.85f, 0f), new GradientAlphaKey(0f, 1f) };
    }

    /// <summary>
    /// The trail behind one ghost ball: a TrailRenderer on a child of the ghost (no collider, no Rigidbody, layer
    /// "Ignore Raycast"), so it follows the ghost's interpolated pose, in the owner's colour. <see cref="GhostTrailRules"/>
    /// decides when it clears and when it emits; at rest it fades out over its time. Off (cleared and not drawn) while
    /// [Presence] RemoteBallTrail is off, and not drawn until it has a material (never a null one).
    /// </summary>
    internal sealed class GhostTrail
    {
        private const float LookRetrySeconds = 1f;

        private readonly RemoteVisuals _owner;
        private readonly TrailRenderer _renderer;
        private readonly Color _color;
        private readonly GhostTrailRules _rules = new GhostTrailRules();
        private TrailLook _look;
        private Material _material;
        private float _nextTry;
        private float _gameSeconds;
        private float _seconds = -1f;
        private bool _off;

        public GhostTrail(RemoteVisuals owner, Transform parent, Color color)
        {
            _owner = owner;
            _color = color;
            var go = new GameObject("GhostTrail") { layer = RemoteVisuals.IgnoreRaycastLayer };
            go.transform.SetParent(parent, false);
            _renderer = go.AddComponent<TrailRenderer>();
            _renderer.enabled = false;
            _renderer.emitting = false;
            _renderer.autodestruct = false;
            _renderer.time = GhostTrailRules.DefaultSeconds;
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            _renderer.generateLightingData = false;
        }

        /// <summary>One applied pose; <paramref name="position"/> is the ghost's position (already moved there).</summary>
        public void OnPose(byte flags, bool teleport, Vector3 position)
        {
            if (_renderer == null)
                return;
            if (_owner == null || !_owner.TrailEnabled)
            {
                if (!_off)
                {
                    _off = true;
                    Hide();
                }
                return;
            }
            if (_off)
            {
                _off = false;
                _rules.Reset();
            }
            // Until the game ball's look is in use, look again once a second (the world's ball may appear any time).
            if ((_look == null || !_look.FromGame) && Time.unscaledTime >= _nextTry)
                TryLook();
            bool moving = GhostTrailRules.IsMoving(flags);
            bool holed = (flags & PoseFlags.BallHoled) != 0;
            var step = _rules.Step(position.x, position.y, position.z, Time.unscaledTimeAsDouble, moving, holed, teleport);
            if (step.Clear)
                _renderer.Clear();
            if (_material == null)
                return;
            float seconds = _owner.TrailSeconds(_gameSeconds);
            if (seconds != _seconds)
            {
                _seconds = seconds;
                _renderer.time = seconds;
            }
            if (_renderer.emitting != step.Emit)
                _renderer.emitting = step.Emit;
            if (!_renderer.enabled)
                _renderer.enabled = true;
        }

        /// <summary>The ghost was hidden: drop the trail; the next pose starts a new one.</summary>
        public void Reset()
        {
            _rules.Reset();
            if (_renderer != null)
                _renderer.Clear();
        }

        /// <summary>A new world: look for the game ball's trail at the next pose (when still on the fallback).</summary>
        public void Invalidate() => _nextTry = 0f;

        /// <summary>After a failure: stop drawing. Swallows exceptions (it runs inside the ghost's pose handler).</summary>
        public void TryHide()
        {
            try
            {
                _rules.Reset();
                Hide();
            }
            catch (Exception)
            {
                // Already reported; the patcher disables the trail after repeated failures.
            }
        }

        public void Destroy()
        {
            if (_material != null)
                Object.Destroy(_material);
            _material = null;
        }

        private void Hide()
        {
            if (_renderer == null)
                return;
            _renderer.emitting = false;
            _renderer.Clear();
            _renderer.enabled = false;
        }

        private void TryLook()
        {
            _nextTry = Time.unscaledTime + LookRetrySeconds;
            var look = _owner.GetTrailLook();
            if (look == null || look.Material == null || ReferenceEquals(look, _look))
                return;
            var color = new Color(GhostTrailRules.Lift(_color.r), GhostTrailRules.Lift(_color.g), GhostTrailRules.Lift(_color.b), _color.a);
            var material = RemoteVisuals.TrailMaterial(look.Material, color, out bool tinted);
            if (_material != null)
                Object.Destroy(_material);
            _material = material;
            _look = look;
            _renderer.sharedMaterial = material;
            _renderer.widthCurve = look.WidthCurve;
            _renderer.widthMultiplier = GhostTrailRules.Width(look.WidthMultiplier);
            _renderer.numCapVertices = look.CapVertices;
            _renderer.numCornerVertices = look.CornerVertices;
            _renderer.minVertexDistance = look.MinVertexDistance;
            _renderer.alignment = look.Alignment;
            _renderer.textureMode = look.TextureMode;
            // Tinted through the material: white vertex colours keep that tint. Otherwise the colour rides on them.
            var vertex = tinted ? Color.white : color;
            var gradient = new Gradient();
            gradient.SetKeys(new[] { new GradientColorKey(vertex, 0f), new GradientColorKey(vertex, 1f) }, look.Alpha);
            _renderer.colorGradient = gradient;
            _gameSeconds = look.Seconds;
            _seconds = -1f;
        }
    }
}
