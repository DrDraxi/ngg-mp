using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Testing
{
    /// <summary>One object in the loaded world, as <c>find</c>, <c>goto</c> and <c>interact</c> see it.</summary>
    public sealed class SceneObject
    {
        public SceneObject(string category, string name, string path, string id, NetVector3 position, string detail, object handle)
        {
            Category = category ?? string.Empty;
            Name = name ?? string.Empty;
            Path = path ?? string.Empty;
            Id = id ?? string.Empty;
            Position = position;
            Detail = detail ?? string.Empty;
            Handle = handle;
        }

        /// <summary>One of <see cref="PlayArgs.Categories"/>.</summary>
        public string Category { get; }

        /// <summary>The GameObject's name.</summary>
        public string Name { get; }

        /// <summary>"Scene:Root/Child/Object".</summary>
        public string Path { get; }

        /// <summary>A stable id where the category has one (a bounty's clean set name, a gate id, an interact id).</summary>
        public string Id { get; }

        public NetVector3 Position { get; }

        /// <summary>What the list shows after the name (interaction type, state).</summary>
        public string Detail { get; }

        /// <summary>The Unity object; opaque outside the implementation.</summary>
        public object Handle { get; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Category).Append("] '").Append(Name).Append('\'');
            if (Id.Length > 0 && Id != Name)
                sb.Append(" id ").Append(Id);
            if (Detail.Length > 0)
                sb.Append(' ').Append(Detail);
            sb.Append(" at ").Append(PlayText.Vector(Position)).Append(" (").Append(Path).Append(')');
            return sb.ToString();
        }
    }

    /// <summary>The outcome of <see cref="ObjectMatcher.Match"/>.</summary>
    public sealed class ObjectMatch
    {
        public ObjectMatch(SceneObject chosen, IReadOnlyList<SceneObject> candidates, string how)
        {
            Chosen = chosen;
            Candidates = candidates ?? Array.Empty<SceneObject>();
            How = how ?? string.Empty;
        }

        /// <summary>The object; null when nothing or more than one matched.</summary>
        public SceneObject Chosen { get; }

        /// <summary>Every object of the best tier that matched.</summary>
        public IReadOnlyList<SceneObject> Candidates { get; }

        /// <summary>"exact id", "exact name", "id contains", "name contains", "path contains", or empty.</summary>
        public string How { get; }
    }

    /// <summary>Finds the object a <c>goto</c> / <c>interact</c> query names.</summary>
    public static class ObjectMatcher
    {
        public const int MaxListed = 40;

        /// <summary>
        /// Case-insensitive, in tiers: the whole id, the whole name, part of the id, part of the name, part of the path.
        /// The first tier with a match decides: one object is chosen; several are ambiguous unless
        /// <paramref name="pick"/> (1-based) picks one. An empty query matches every object (one is chosen when it is
        /// the only one, as for <c>goto remote</c> with one other player).
        /// </summary>
        public static ObjectMatch Match(IReadOnlyList<SceneObject> objects, string query, int pick = 0)
        {
            if (objects == null || objects.Count == 0)
                return new ObjectMatch(null, null, string.Empty);
            string q = (query ?? string.Empty).Trim();
            if (q.Length == 0)
                return Decide(new List<SceneObject>(objects), pick, "any");
            var tiers = new[]
            {
                ("exact id", (Func<SceneObject, bool>)(o => o.Id.Length > 0 && string.Equals(o.Id, q, StringComparison.OrdinalIgnoreCase))),
                ("exact name", o => string.Equals(o.Name, q, StringComparison.OrdinalIgnoreCase)),
                ("id contains", o => o.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0),
                ("name contains", o => o.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0),
                ("path contains", o => o.Path.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
            };
            foreach (var (how, test) in tiers)
            {
                var found = new List<SceneObject>();
                foreach (var o in objects)
                {
                    if (o != null && test(o))
                        found.Add(o);
                }
                if (found.Count > 0)
                    return Decide(found, pick, how);
            }
            return new ObjectMatch(null, null, string.Empty);
        }

        private static ObjectMatch Decide(List<SceneObject> found, int pick, string how)
        {
            if (pick > 0)
                return new ObjectMatch(pick <= found.Count ? found[pick - 1] : null, found, how);
            return new ObjectMatch(found.Count == 1 ? found[0] : null, found, how);
        }

        /// <summary>"N object(s):" then one numbered line each (the first <see cref="MaxListed"/>).</summary>
        public static string Describe(IReadOnlyList<SceneObject> objects, int max = MaxListed)
        {
            int count = objects?.Count ?? 0;
            var sb = new StringBuilder();
            sb.Append(count).Append(" object(s)").Append(count == 0 ? string.Empty : ":");
            for (int i = 0; i < count && i < max; i++)
                sb.Append("\n  ").Append(i + 1).Append(". ").Append(objects[i]);
            if (count > max)
                sb.Append("\n  ... ").Append(count - max).Append(" more (narrow the text)");
            return sb.ToString();
        }

        /// <summary>The objects whose id, name or path contains <paramref name="filter"/> (all for an empty one).</summary>
        public static List<SceneObject> Filter(IReadOnlyList<SceneObject> objects, string filter)
        {
            var result = new List<SceneObject>();
            if (objects == null)
                return result;
            string f = (filter ?? string.Empty).Trim();
            foreach (var o in objects)
            {
                if (o == null)
                    continue;
                if (f.Length == 0 || o.Id.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || o.Name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                    || o.Path.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(o);
            }
            return result;
        }
    }

    /// <summary>A parsed <c>goto</c> or <c>interact</c> target.</summary>
    public sealed class TargetArgs
    {
        public string Category;
        public string Query = string.Empty;
        public int Pick;
        public bool HasPosition;
        public NetVector3 Position;

        /// <summary>`goto x y z yaw`: the heading to face there (degrees); null keeps the current one.</summary>
        public float? Yaw;
    }

    /// <summary>A parsed <c>shoot</c>.</summary>
    public sealed class ShotRequest
    {
        public const float DefaultPower = 0.7f;
        public const float MaxAngle = 90f;

        /// <summary>Fraction of the club's full swing, (0, 1].</summary>
        public float Power = DefaultPower;

        /// <summary>The aim panel's angle (Spin.m_angle); null keeps the panel's.</summary>
        public float? Angle;

        /// <summary>A ShotType member name (Good, Perfect, Thin, ...).</summary>
        public string ShotType = "Good";

        public override string ToString() =>
            "power " + Power.ToString("0.###", CultureInfo.InvariantCulture) +
            (Angle.HasValue ? ", angle " + Angle.Value.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty) +
            ", " + ShotType;
    }

    /// <summary>Arguments of the play commands (find, goto, interact, shoot, mode, bounty, dialogue).</summary>
    public static class PlayArgs
    {
        public const string AnyObject = "object";
        public const string Interactable = "interactable";
        public const string Tee = "tee";
        public const string Pad = "pad";
        public const string Trigger = "trigger";
        public const string Lock = "lock";
        public const string Remote = "remote";

        /// <summary>The categories of <see cref="IHarnessPlay.FindObjects"/>, in the order the help names them.</summary>
        public static readonly string[] Categories = { AnyObject, Interactable, Tee, Pad, Trigger, Lock, Remote };

        /// <summary>What <c>interact</c> searches without a category: everything the game lets a player use.</summary>
        public static readonly string[] Usable = { Interactable, Tee, Pad, Trigger };

        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "object", AnyObject }, { "objects", AnyObject }, { "obj", AnyObject },
            { "interactable", Interactable }, { "interactables", Interactable }, { "fps", Interactable },
            { "tee", Tee }, { "tees", Tee },
            { "pad", Pad }, { "pads", Pad }, { "bounty", Pad },
            { "trigger", Trigger }, { "triggers", Trigger }, { "dialogue", Trigger },
            { "lock", Lock }, { "locks", Lock }, { "door", Lock }, { "gate", Lock },
            { "remote", Remote }, { "remotes", Remote }, { "player", Remote }
        };

        /// <summary>The category a word names, or null.</summary>
        public static string Category(string word) =>
            word != null && Aliases.TryGetValue(word, out var category) ? category : null;

        /// <summary>
        /// <c>goto</c> / <c>interact</c> words after the command: "[category] text... [@n]", or for <c>goto</c>
        /// "x y z". Without a category the target is <paramref name="defaultCategory"/> (null: every usable kind).
        /// </summary>
        public static bool TryParseTarget(IReadOnlyList<string> words, bool allowPosition, string defaultCategory,
            out TargetArgs args, out string error)
        {
            args = new TargetArgs { Category = defaultCategory };
            error = null;
            var list = new List<string>();
            if (words != null)
            {
                foreach (var w in words)
                {
                    if (!string.IsNullOrEmpty(w))
                        list.Add(w);
                }
            }
            if (allowPosition && (list.Count == 3 || list.Count == 4) && HarnessCommand.TryParseFloat(list[0], out float x)
                && HarnessCommand.TryParseFloat(list[1], out float y) && HarnessCommand.TryParseFloat(list[2], out float z))
            {
                float yaw = 0f;
                if (list.Count == 4 && !HarnessCommand.TryParseFloat(list[3], out yaw))
                {
                    error = "the fourth number is the heading in degrees";
                    return false;
                }
                args.HasPosition = true;
                args.Position = new NetVector3(x, y, z);
                args.Yaw = list.Count == 4 ? yaw : (float?)null;
                return true;
            }
            if (list.Count > 0)
            {
                string last = list[list.Count - 1];
                if (last.Length > 1 && last[0] == '@')
                {
                    if (!HarnessCommand.TryParseInt(last.Substring(1), out int pick) || pick < 1)
                    {
                        error = "@n must be a number from 1";
                        return false;
                    }
                    args.Pick = pick;
                    list.RemoveAt(list.Count - 1);
                }
            }
            if (list.Count > 0)
            {
                string category = Category(list[0]);
                if (category != null)
                {
                    args.Category = category;
                    list.RemoveAt(0);
                }
            }
            args.Query = string.Join(" ", list);
            // A bare "goto"/"interact" names nothing; "goto remote" may (the only other player).
            if (args.Query.Length == 0 && args.Category != Remote)
            {
                error = "name an object (its name, id or a part of its path)";
                return false;
            }
            return true;
        }

        /// <summary>"shoot [power 0..1] [angle] [ShotType]" (any order after the power: a number is the angle).</summary>
        public static bool TryParseShot(IReadOnlyList<string> words, out ShotRequest shot, out string error)
        {
            shot = new ShotRequest();
            error = null;
            int numbers = 0;
            if (words == null)
                return true;
            foreach (var word in words)
            {
                if (string.IsNullOrEmpty(word))
                    continue;
                if (HarnessCommand.TryParseFloat(word, out float value))
                {
                    if (numbers == 0)
                    {
                        if (!(value > 0f && value <= 1f))
                        {
                            error = "the power is a fraction of the club's full swing, above 0 and at most 1";
                            return false;
                        }
                        shot.Power = value;
                    }
                    else if (numbers == 1)
                    {
                        if (Math.Abs(value) > ShotRequest.MaxAngle)
                        {
                            error = "the angle must be between -" + ShotRequest.MaxAngle.ToString("0", CultureInfo.InvariantCulture) +
                                    " and " + ShotRequest.MaxAngle.ToString("0", CultureInfo.InvariantCulture);
                            return false;
                        }
                        shot.Angle = value;
                    }
                    else
                    {
                        error = "too many numbers (power, then angle)";
                        return false;
                    }
                    numbers++;
                    continue;
                }
                if (!IsIdentifier(word))
                {
                    error = "'" + word + "' is neither a number nor a ShotType name";
                    return false;
                }
                shot.ShotType = word;
            }
            return true;
        }

        /// <summary>"golf" / "walk" (also "on"/"off" for golf).</summary>
        public static bool TryParseMode(string word, out bool golf)
        {
            golf = false;
            switch ((word ?? string.Empty).ToLowerInvariant())
            {
                case "golf":
                case "swing":
                case "stance":
                    golf = true;
                    return true;
                case "walk":
                case "fps":
                case "move":
                    golf = false;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsIdentifier(string word)
        {
            foreach (char c in word)
            {
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '_')))
                    return false;
            }
            return word.Length > 0 && char.IsLetter(word[0]);
        }
    }

    /// <summary>Where <c>goto</c> puts the player: beside an object, facing it.</summary>
    public static class Placement
    {
        public const float MaxRadius = 12f;
        public const float DefaultMargin = 1.2f;

        /// <summary>
        /// The spot <paramref name="radius"/> + <paramref name="margin"/> from <paramref name="center"/> on the side of
        /// <paramref name="from"/> (the player's current spot; straight "south" when it is on the center), at the
        /// center's height (the caller snaps it to the ground), and the yaw in degrees that faces the center (Unity:
        /// 0 looks along +z, 90 along +x). The radius is capped at <see cref="MaxRadius"/> (terrain-sized bounds).
        /// </summary>
        public static NetVector3 StandBeside(NetVector3 from, NetVector3 center, float radius, float margin, out float yawDegrees)
        {
            if (!(radius >= 0f) || float.IsInfinity(radius))
                radius = 0f;
            radius = Math.Min(radius, MaxRadius);
            if (!(margin >= 0f) || float.IsInfinity(margin))
                margin = DefaultMargin;
            float dx = from.x - center.x;
            float dz = from.z - center.z;
            float length = (float)Math.Sqrt(dx * dx + dz * dz);
            if (!(length > 0.01f) || float.IsInfinity(length))
            {
                dx = 0f;
                dz = -1f;
                length = 1f;
            }
            float distance = radius + margin;
            var stand = new NetVector3(center.x + dx / length * distance, center.y, center.z + dz / length * distance);
            yawDegrees = YawTowards(stand, center);
            return stand;
        }

        /// <summary>The yaw (degrees, 0..360) that looks from <paramref name="from"/> towards <paramref name="to"/>.</summary>
        public static float YawTowards(NetVector3 from, NetVector3 to)
        {
            double yaw = Math.Atan2(to.x - from.x, to.z - from.z) * 180.0 / Math.PI;
            if (yaw < 0)
                yaw += 360.0;
            return (float)yaw;
        }

        /// <summary>Horizontal distance (metres).</summary>
        public static float FlatDistance(NetVector3 a, NetVector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }

    public enum LockState
    {
        Unknown,
        Locked,
        Open
    }

    /// <summary>Lock states from what the game shows (ObjectStates' padlock and opened-padlock objects).</summary>
    public static class LockText
    {
        /// <summary>
        /// A gate or door of ObjectStates: open when its opened padlock shows, or its padlock is gone; locked while the
        /// padlock shows; unknown without either object. Null: the object does not exist.
        /// </summary>
        public static LockState Classify(bool? padlockActive, bool? openedActive)
        {
            if (openedActive == true)
                return LockState.Open;
            if (padlockActive == true)
                return LockState.Locked;
            if (padlockActive == false)
                return LockState.Open;
            return LockState.Unknown;
        }

        public static string Name(LockState state) =>
            state == LockState.Open ? "OPEN" : state == LockState.Locked ? "LOCKED" : "unknown";

        /// <summary>"on" / "off" / "none" for an optional object's activity.</summary>
        public static string Active(bool? active) => active == null ? "none" : active.Value ? "on" : "off";
    }

    /// <summary>The swing panel's power for a <c>shoot</c> fraction.</summary>
    public static class ShotPower
    {
        /// <summary>The value the game's full swing reaches when the club's own top is unknown.</summary>
        public const float FallbackFullSwing = 10f;

        /// <summary>
        /// ClubSwing.m_power for <paramref name="fraction"/> of the club's full swing: m_power runs 0..powerVisualMult
        /// (answers #53, #127). <paramref name="usedFallback"/> when the club's value is missing or not positive.
        /// </summary>
        public static float ToPanelPower(float fraction, float? powerVisualMult, out bool usedFallback)
        {
            float f = fraction > 0f && fraction <= 1f ? fraction : ShotRequest.DefaultPower;
            usedFallback = !(powerVisualMult.HasValue && powerVisualMult.Value > 0f && !float.IsInfinity(powerVisualMult.Value));
            float full = usedFallback ? FallbackFullSwing : powerVisualMult.Value;
            return f * full;
        }
    }

    /// <summary>Number formats of the play commands' answers (invariant, for the scenario scripts' regexes).</summary>
    public static class PlayText
    {
        public static string Vector(NetVector3 v) =>
            string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})", v.x, v.y, v.z);

        public static string Number(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
