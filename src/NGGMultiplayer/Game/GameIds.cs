using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using NGGMultiplayer.Core.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// Stable network ids for things in the game world, identical on every peer running the same build:
    /// <list type="bullet">
    /// <item><c>so:&lt;m_id&gt;</c>: the object (or its nearest parent) carries a <see cref="SaveableObject"/>; its GUID.</item>
    /// <item><c>path:&lt;A/B#1/C&gt;</c>: the hierarchy path in the active scene, names cleaned of "(Clone)", with
    /// <c>#n</c> for the n-th (0-based) earlier sibling of the same name.</item>
    /// </list>
    /// Asset names (challenge sets, clubs, dialogue lines) are compared with <see cref="Clean(string)"/>.
    /// Main thread only. The lookup cache is invalidated on every scene load by <see cref="GameContext"/>.
    /// </summary>
    public static class GameIds
    {
        public const string SaveablePrefix = "so:";
        public const string PathPrefix = "path:";

        private const string CloneSuffix = "(Clone)";
        private const double RebuildCooldownSeconds = 1.0;

        private static readonly Dictionary<string, SaveableObject> SaveablesById = new Dictionary<string, SaveableObject>(StringComparer.Ordinal);
        private static bool _saveablesBuilt;
        private static double _lastRebuild = double.NegativeInfinity;
        private static bool _removeCloneBroken;

        /// <summary>The game's own <c>Utilities.CleanString.RemoveClone</c>, with a local fallback if it is missing.</summary>
        public static string Clean(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            if (!_removeCloneBroken)
            {
                try
                {
                    return GameRemoveClone(name) ?? name;
                }
                catch (Exception e)
                {
                    _removeCloneBroken = true;
                    NetLog.Warn($"[Game] Utilities.CleanString.RemoveClone failed ({e.GetType().Name}); using the mod's own clean-up.");
                }
            }
            return StripClone(name);
        }

        public static string Clean(UnityEngine.Object obj) => obj == null ? string.Empty : Clean(obj.name);

        /// <summary>Cleaned name of a challenge set ("BO_x", "ML_NormalGolf"); empty for null.</summary>
        public static string ChallengeSetName(ChallengeSet set) => set == null ? string.Empty : Clean(set.name);

        /// <summary>
        /// The set in <c>GreatConnector.instance.m_allChallenges</c> with this clean name, or null: the list
        /// <c>GreatConnector.GetChallengeSet</c> searches, without the Debug.LogError that call writes for a name it
        /// does not know (answers #12, #87).
        /// </summary>
        public static ChallengeSet FindConnectorSet(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            var connector = GreatConnector.instance;
            var all = connector != null ? connector.m_allChallenges : null;
            if (all == null)
                return null;
            foreach (var set in all)
            {
                if (set != null && ChallengeSetName(set) == name)
                    return set;
            }
            return null;
        }

        /// <summary>The id of <paramref name="component"/>'s object: its (or a parent's) SaveableObject GUID, else its path.</summary>
        public static string ForObject(Component component)
        {
            if (component == null)
                return string.Empty;
            for (var t = component.transform; t != null; t = t.parent)
            {
                var saveable = t.GetComponent<SaveableObject>();
                if (saveable != null && !string.IsNullOrEmpty(saveable.m_id))
                    return SaveablePrefix + saveable.m_id;
            }
            return PathPrefix + HierarchyPath(component.transform);
        }

        /// <summary>Path from the scene root, e.g. "World/Shop/Cash#2".</summary>
        public static string HierarchyPath(Transform transform)
        {
            if (transform == null)
                return string.Empty;
            var segments = new List<string>();
            for (var t = transform; t != null; t = t.parent)
                segments.Add(Segment(t));
            segments.Reverse();
            return string.Join("/", segments);
        }

        /// <summary>The object an id names in the loaded world, or null.</summary>
        public static GameObject Resolve(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;
            if (id.StartsWith(SaveablePrefix, StringComparison.Ordinal))
            {
                var saveable = ResolveSaveable(id.Substring(SaveablePrefix.Length));
                return saveable != null ? saveable.gameObject : null;
            }
            if (id.StartsWith(PathPrefix, StringComparison.Ordinal))
                return ResolvePath(id.Substring(PathPrefix.Length));
            return null;
        }

        /// <summary>The component on (or under) the object an id names, or null.</summary>
        public static T Resolve<T>(string id) where T : Component
        {
            var go = Resolve(id);
            if (go == null)
                return null;
            var component = go.GetComponent<T>();
            return component != null ? component : go.GetComponentInChildren<T>(true);
        }

        /// <summary>The SaveableObject with this <c>m_id</c> (without the "so:" prefix), or null.</summary>
        public static SaveableObject ResolveSaveable(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;
            if (!_saveablesBuilt)
                RebuildSaveables();
            if (SaveablesById.TryGetValue(guid, out var found) && found != null)
                return found;
            // Objects created after the first lookup (or a stale entry): rebuild, but not every frame.
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastRebuild < RebuildCooldownSeconds)
                return null;
            RebuildSaveables();
            return SaveablesById.TryGetValue(guid, out found) && found != null ? found : null;
        }

        /// <summary>Every SaveableObject of the loaded world, active or not, by <c>m_id</c>.</summary>
        public static IReadOnlyDictionary<string, SaveableObject> Saveables
        {
            get
            {
                if (!_saveablesBuilt)
                    RebuildSaveables();
                return SaveablesById;
            }
        }

        /// <summary>Drops cached lookups (scene change).</summary>
        public static void InvalidateCache()
        {
            SaveablesById.Clear();
            _saveablesBuilt = false;
        }

        private static void RebuildSaveables()
        {
            _lastRebuild = Time.realtimeSinceStartupAsDouble;
            _saveablesBuilt = true;
            SaveablesById.Clear();
            SaveableObject[] all;
            try
            {
                all = Compat.FindAll<SaveableObject>(true);
            }
            catch (Exception e)
            {
                NetLog.Error($"[Game] Listing SaveableObjects failed: {e}");
                return;
            }
            int duplicates = 0;
            foreach (var saveable in all)
            {
                if (saveable == null || string.IsNullOrEmpty(saveable.m_id))
                    continue;
                if (SaveablesById.ContainsKey(saveable.m_id))
                {
                    duplicates++;
                    continue;
                }
                SaveablesById.Add(saveable.m_id, saveable);
            }
            if (duplicates > 0)
                NetLog.Warn($"[Game] {duplicates} SaveableObjects share an m_id with another; only the first is addressable.");
        }

        private static GameObject ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            string[] segments = path.Split('/');
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
                return null;
            Transform current = null;
            var roots = scene.GetRootGameObjects();
            foreach (var root in roots)
            {
                if (root != null && RootSegment(root.transform, roots) == segments[0])
                {
                    current = root.transform;
                    break;
                }
            }
            for (int i = 1; current != null && i < segments.Length; i++)
                current = FindChild(current, segments[i]);
            return current != null ? current.gameObject : null;
        }

        private static Transform FindChild(Transform parent, string segment)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (Segment(child) == segment)
                    return child;
            }
            return null;
        }

        private static string Segment(Transform t)
        {
            string name = Clean(t.name);
            int index = 0;
            var parent = t.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var sibling = parent.GetChild(i);
                    if (sibling == t)
                        break;
                    if (Clean(sibling.name) == name)
                        index++;
                }
            }
            else if (t.gameObject.scene.IsValid())
            {
                return RootSegment(t, t.gameObject.scene.GetRootGameObjects());
            }
            return index == 0 ? name : name + "#" + index;
        }

        private static string RootSegment(Transform t, GameObject[] roots)
        {
            string name = Clean(t.name);
            int index = 0;
            foreach (var root in roots)
            {
                if (root == null)
                    continue;
                if (root.transform == t)
                    break;
                if (Clean(root.name) == name)
                    index++;
            }
            return index == 0 ? name : name + "#" + index;
        }

        private static string StripClone(string name)
        {
            string trimmed = name;
            while (trimmed.EndsWith(CloneSuffix, StringComparison.Ordinal))
                trimmed = trimmed.Substring(0, trimmed.Length - CloneSuffix.Length).TrimEnd();
            return trimmed;
        }

        // Out of line: a missing game method fails when this is compiled, inside the caller's try block.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string GameRemoveClone(string name) => global::Utilities.CleanString.RemoveClone(name);

        /// <summary>For logs: "Type 'name'".</summary>
        public static string Describe(Component component)
        {
            if (component == null)
                return "null";
            var builder = new StringBuilder();
            builder.Append(component.GetType().Name).Append(" '").Append(HierarchyPath(component.transform)).Append('\'');
            return builder.ToString();
        }
    }
}
