using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HarmonyLib;
using NGGMultiplayer.Core.Diagnostics;
using NGGMultiplayer.Runtime;
using UnityEngine;
using UnityEngine.Video;
using Object = UnityEngine.Object;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// The local player's own videos in the chosen look: with <see cref="PlayerLook.Female"/>, the golf-mode golfer
    /// (GolfVideo) and the first-person hands (FirstPersonVideo, and the hands' Crouch player) show redrawn key frames
    /// instead of the game's footage. The game's VideoPlayer keeps playing its own clip (so every frame-timed event,
    /// such as the ball impact at frame 26, stays where it was); only the texture on the video's quad is swapped, to
    /// the drawing whose key frame is the last one at or before the playing frame (stop-motion, <see cref="LookFrames"/>).
    /// <para>Drawings: "video/female/&lt;clip name&gt;/&lt;frame&gt;.png" next to the plugin DLL, full video frames
    /// with alpha (any size of the same aspect). A clip without drawings shows the game's footage.</para>
    /// </summary>
    internal sealed class LookVideo
    {
        public const string FolderName = "video";
        private const string Log = "[Presence] ";
        private const string BaseMap = "_BaseMap";
        private const int PreloadPerTick = 2;

        private readonly GameContext _context;
        private readonly Dictionary<string, ClipDrawings> _clips = new Dictionary<string, ClipDrawings>(StringComparer.Ordinal);
        private readonly List<Target> _targets = new List<Target>();
        private bool _scanned;
        private bool _resolved;
        private float _nextResolve;
        private int _preloadClip;

        private static LookVideo _current;

        public LookVideo(GameContext context)
        {
            _context = context;
            _current = this;
        }

        /// <summary>For the test harness's "videos" command: drawings found, video quads and what each shows.</summary>
        internal static string DescribeCurrent()
        {
            var look = _current;
            if (look == null)
                return "not installed";
            var sb = new StringBuilder();
            sb.Append(look._clips.Count).Append(" clip(s) with drawings, ").Append(look._resolved ? "resolved" : "not resolved")
                .Append(", ").Append(look._targets.Count).Append(" target(s)");
            foreach (var target in look._targets)
            {
                sb.Append("\n    ").Append(target.Video != null ? target.Video.name + "#" + target.Video.GetInstanceID() : "-")
                    .Append(": on screen ").Append(target.Top != null ? target.Top.name + " (" + (target.Top.clip != null ? target.Top.clip.name : "-") + ")" : "none")
                    .Append(", shows ").Append(target.Shown != null ? target.Shown.name : "-")
                    .Append(", players");
                foreach (var vp in target.Players)
                    sb.Append(' ').Append(vp != null ? vp.name : "-");
                sb.Append(", materials");
                foreach (var m in target.Materials)
                    sb.Append(' ').Append(m != null ? m.name + "#" + m.GetInstanceID() : "-");
            }
            return sb.ToString();
        }

        /// <summary>A new world: the video players and quads are new objects.</summary>
        public void OnWorldLoaded()
        {
            RestoreAll();
            _targets.Clear();
            _resolved = false;
            _nextResolve = 0f;
        }

        public void Tick()
        {
            if (!_context.Patcher.IsEnabled(PresenceFeature.LookVideo))
                return;
            try
            {
                bool female = _context.Look == PlayerLook.Female && _context.Scene == GameScene.Main && _context.WorldReady;
                if (!female)
                {
                    RestoreAll();
                    return;
                }
                if (!_scanned)
                    Scan();
                if (_clips.Count == 0)
                    return;
                if (!_resolved && Time.unscaledTime >= _nextResolve)
                    Resolve();
                foreach (var target in _targets)
                    Show(target);
                Preload();
            }
            catch (Exception e)
            {
                RestoreAll();
                _context.Patcher.ReportFailure(PresenceFeature.LookVideo, e);
            }
        }

        // ---- Drawings ------------------------------------------------------------------------------------

        private void Scan()
        {
            _scanned = true;
            string root;
            try
            {
                root = Path.Combine(Path.GetDirectoryName(typeof(LookVideo).Assembly.Location) ?? ".", FolderName, "female");
            }
            catch (Exception e)
            {
                _context.Warn($"{Log}Female video drawings: plugin folder unknown ({e.Message}).");
                return;
            }
            if (!Directory.Exists(root))
            {
                _context.Info($"{Log}No female video drawings at {root}; your own videos stay as they are.");
                return;
            }
            int drawings = 0;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var keys = new List<KeyValuePair<int, string>>();
                foreach (var file in Directory.GetFiles(dir, "*.png"))
                {
                    int frame = LookFrames.ParseFrame(Path.GetFileNameWithoutExtension(file));
                    if (frame >= 0)
                        keys.Add(new KeyValuePair<int, string>(frame, file));
                }
                if (keys.Count == 0)
                    continue;
                keys.Sort((a, b) => a.Key.CompareTo(b.Key));
                _clips[Path.GetFileName(dir)] = new ClipDrawings(keys);
                drawings += keys.Count;
            }
            _context.Info($"{Log}Found {drawings} female video drawing(s) for {_clips.Count} clip(s) in {root}.");
        }

        /// <summary>Loads a couple of drawings per frame in the background, so a clip rarely waits on its first look.</summary>
        private void Preload()
        {
            if (_preloadClip < 0)
                return;
            int budget = PreloadPerTick;
            var all = new List<ClipDrawings>(_clips.Values);
            while (budget > 0 && _preloadClip < all.Count)
            {
                if (all[_preloadClip].LoadNext(_context))
                    budget--;
                else
                    _preloadClip++;
            }
            if (_preloadClip >= all.Count)
            {
                _preloadClip = -1;
                _context.Info($"{Log}Female video drawings loaded.");
            }
        }

        // ---- Video quads ---------------------------------------------------------------------------------

        private void Resolve()
        {
            _nextResolve = Time.unscaledTime + 2f;
            _targets.Clear();
            var players = new List<VideoPlayer>();
            var golf = Compat.FindAny<GolfVideo>(true);
            if (golf != null && AccessTools.Field(typeof(GolfVideo), "m_videoPlayer")?.GetValue(golf) is VideoPlayer golfPlayer)
                players.Add(golfPlayer);
            var hands = Compat.FindAny<FirstPersonVideo>(true);
            if (hands != null && hands.m_videoPlayer != null)
                players.Add(hands.m_videoPlayer);
            if (players.Count == 0)
                return;
            // The hands' Crouch player (not referenced by the game's code) draws into the hands' texture too.
            var extra = new List<VideoPlayer>();
            foreach (var vp in Compat.FindAll<VideoPlayer>(true))
            {
                if (vp == null || players.Contains(vp) || vp.targetTexture == null)
                    continue;
                foreach (var p in players)
                {
                    if (p.targetTexture == vp.targetTexture)
                        extra.Add(vp);
                }
            }
            var materials = new Dictionary<Texture, List<Material>>();
            foreach (var renderer in Compat.FindAll<Renderer>(true))
            {
                if (renderer == null)
                    continue;
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null || !material.HasProperty(BaseMap))
                        continue;
                    var texture = material.GetTexture(BaseMap);
                    if (texture == null)
                        continue;
                    if (!materials.TryGetValue(texture, out var list))
                        materials[texture] = list = new List<Material>();
                    if (!list.Contains(material))
                        list.Add(material);
                }
            }
            foreach (var p in players)
            {
                var rt = p.targetTexture;
                if (rt == null || !materials.TryGetValue(rt, out var list))
                    continue;
                var target = new Target(rt, list);
                target.Players.Add(p);
                foreach (var vp in extra)
                {
                    if (vp.targetTexture == rt)
                        target.Players.Add(vp);
                }
                _targets.Add(target);
            }
            _resolved = _targets.Count == players.Count;
            if (_resolved)
                _context.Info($"{Log}Female videos: {_targets.Count} video quad(s) found.");
        }

        private void Show(Target target)
        {
            // Several players can play into one texture (the hands and their Crouch player); the one that started its
            // clip last is the one on screen, as it is in the game's own footage. A player that has finished (a swing
            // ends without looping) or is still preparing its clip keeps its picture in the texture, so it counts too:
            // otherwise the game's own last frame (the man) would show once the swing is over.
            VideoPlayer top = null;
            float topSince = float.MinValue;
            foreach (var vp in target.Players)
            {
                bool showing = vp != null && vp.isActiveAndEnabled && vp.clip != null;
                float since = target.Started(vp, showing ? vp.clip : null);
                if (showing && since > topSince)
                {
                    top = vp;
                    topSince = since;
                }
            }
            if (top != target.Top)
            {
                target.Top = top;
                NetLog.Debug($"{Log}Video on {target.Video.name}: {(top != null ? top.name + " playing " + top.clip.name : "none playing")}.");
            }
            Texture texture = null;
            if (top != null && _clips.TryGetValue(top.clip.name, out var clip))
                texture = clip.Get(LookFrames.Hold(clip.Keys, target.FrameOf(top)), _context);
            target.Apply(texture);
        }

        private void RestoreAll()
        {
            foreach (var target in _targets)
                target.Apply(null);
        }

        private sealed class Target
        {
            public readonly RenderTexture Video;
            public readonly List<Material> Materials;
            public readonly List<VideoPlayer> Players = new List<VideoPlayer>();
            private readonly Dictionary<VideoPlayer, KeyValuePair<Object, float>> _started = new Dictionary<VideoPlayer, KeyValuePair<Object, float>>();
            private readonly Dictionary<VideoPlayer, KeyValuePair<Object, long>> _lastFrame = new Dictionary<VideoPlayer, KeyValuePair<Object, long>>();
            public VideoPlayer Top;
            private Texture _shown;

            public Texture Shown => _shown;

            public Target(RenderTexture video, List<Material> materials)
            {
                Video = video;
                Materials = materials;
                _shown = video;
            }

            /// <summary>The frame <paramref name="player"/> shows: while it plays, its own; once it has stopped, the last
            /// one it played of the same clip (what stays in the texture), whatever the stopped player reports.</summary>
            public long FrameOf(VideoPlayer player)
            {
                if (player.isPlaying && player.frame >= 0)
                {
                    _lastFrame[player] = new KeyValuePair<Object, long>(player.clip, player.frame);
                    return player.frame;
                }
                return _lastFrame.TryGetValue(player, out var last) && last.Key == player.clip ? last.Value : player.frame;
            }

            /// <summary>When <paramref name="player"/> started playing <paramref name="clip"/> (null: not playing).</summary>
            public float Started(VideoPlayer player, Object clip)
            {
                if (player == null)
                    return float.MinValue;
                if (!_started.TryGetValue(player, out var entry) || entry.Key != clip)
                    _started[player] = entry = new KeyValuePair<Object, float>(clip, Time.unscaledTime);
                return entry.Value;
            }

            /// <summary>Shows <paramref name="drawing"/> on the video's quad; null puts the game's video back.</summary>
            public void Apply(Texture drawing)
            {
                var texture = drawing != null ? drawing : Video;
                if (texture == _shown)
                    return;
                _shown = texture;
                foreach (var material in Materials)
                {
                    if (material != null)
                        material.SetTexture(BaseMap, texture);
                }
            }
        }

        private sealed class ClipDrawings
        {
            public readonly int[] Keys;
            private readonly string[] _files;
            private readonly Texture2D[] _textures;
            private readonly bool[] _failed;
            private int _next;

            public ClipDrawings(List<KeyValuePair<int, string>> keys)
            {
                Keys = new int[keys.Count];
                _files = new string[keys.Count];
                for (int i = 0; i < keys.Count; i++)
                {
                    Keys[i] = keys[i].Key;
                    _files[i] = keys[i].Value;
                }
                _textures = new Texture2D[keys.Count];
                _failed = new bool[keys.Count];
            }

            public Texture2D Get(int index, GameContext context)
            {
                if (index < 0 || index >= _textures.Length)
                    return null;
                if (_textures[index] == null && !_failed[index])
                    Load(index, context);
                return _textures[index];
            }

            /// <summary>Loads the next drawing not loaded yet; false when all are.</summary>
            public bool LoadNext(GameContext context)
            {
                while (_next < _textures.Length && (_textures[_next] != null || _failed[_next]))
                    _next++;
                if (_next >= _textures.Length)
                    return false;
                Load(_next, context);
                return true;
            }

            private void Load(int index, GameContext context)
            {
                string file = _files[index];
                try
                {
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                    {
                        name = "NGGMP female " + Path.GetFileName(Path.GetDirectoryName(file)) + "/" + Path.GetFileNameWithoutExtension(file),
                        wrapMode = TextureWrapMode.Clamp,
                        filterMode = FilterMode.Bilinear,
                    };
                    if (!texture.LoadImage(File.ReadAllBytes(file), false))
                    {
                        Object.Destroy(texture);
                        _failed[index] = true;
                        context.Warn($"{Log}Female video drawing {file} is not a readable PNG.");
                        return;
                    }
                    // Block-compressed (a quarter of the memory); only when the size allows it.
                    if (texture.width % 4 == 0 && texture.height % 4 == 0)
                        texture.Compress(true);
                    texture.Apply(false, true);
                    Object.DontDestroyOnLoad(texture);
                    _textures[index] = texture;
                }
                catch (Exception e)
                {
                    _failed[index] = true;
                    context.Warn($"{Log}Loading female video drawing {file} failed: {e.Message}");
                }
            }
        }
    }
}
