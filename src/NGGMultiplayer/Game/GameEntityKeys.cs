namespace NGGMultiplayer.Game
{
    /// <summary>Prefab keys of the plugin's networked entities and the meaning of their state flags.</summary>
    public static class GameEntityKeys
    {
        public const string Player = "ngg.player";
        public const string Ball = "ngg.ball";

        public static readonly string[] PerPlayer = { Player, Ball };
    }

    /// <summary>Bits of <c>TransformState.Flags</c> used by the plugin's entities.</summary>
    public static class PoseFlags
    {
        /// <summary>The owner has a pose for this object; without it the remote view stays hidden.</summary>
        public const byte Visible = 1;

        /// <summary>Player: in golf mode (pose is the golf stance root, m_golferHolder), else walking.</summary>
        public const byte PlayerGolf = 1 << 1;

        /// <summary>Player: crouched (walk mode).</summary>
        public const byte PlayerCrouched = 1 << 2;

        /// <summary>Player: not on the ground (walk mode).</summary>
        public const byte PlayerAirborne = 1 << 3;

        /// <summary>Ball: in flight or rolling (not kinematic, shot not complete).</summary>
        public const byte BallMoving = 1 << 1;

        /// <summary>Ball: holed or killed; shown hidden by the remote view's visual.</summary>
        public const byte BallHoled = 1 << 2;
    }
}
