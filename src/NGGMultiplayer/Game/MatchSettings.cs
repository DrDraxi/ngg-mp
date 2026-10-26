using NGGMultiplayer.Game.Presence;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// [Match] config: the competitive "Just Play Nine" round in a session (the host's settings decide for everyone).
    /// </summary>
    public struct MatchSettings
    {
        public MatchSettings(bool enabled, MatchMode turns, int maxStrokes)
        {
            Enabled = enabled;
            Turns = turns;
            MaxStrokes = maxStrokes;
        }

        public static MatchSettings Default => new MatchSettings(true, MatchMode.Free, DefaultMaxStrokes);

        public const int DefaultMaxStrokes = 10;

        /// <summary>Off: everyone plays the nine holes on their own, as in 1.0.</summary>
        public bool Enabled;

        public MatchMode Turns;

        /// <summary>Strokes on a hole after which a player picks up; 0 = no limit.</summary>
        public int MaxStrokes;
    }
}
