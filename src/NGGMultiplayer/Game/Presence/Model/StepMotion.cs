using System;

namespace NGGMultiplayer.Game.Presence
{
    /// <summary>
    /// How a cutout with no walk drawings moves while its player walks: a hop per step, and for a crouch-walker a
    /// rock from foot to foot (the cutout tilts about its soles), so a still drawing does not just slide along.
    /// Plain floats so it can be unit tested.
    /// </summary>
    internal static class StepMotion
    {
        public const float StepRate = 1.8f;
        public const float StepLift = 0.035f;

        // A crouch-walker takes shorter, quicker steps and waddles.
        public const float CrouchStepRate = 2.4f;
        public const float CrouchStepLift = 0.05f;
        public const float CrouchRollDegrees = 7f;

        /// <summary>
        /// The lift (metres) and sideways tilt (degrees about the view axis) <paramref name="elapsed"/>
        /// seconds into a walk. Each step hops once, the tilt swings to the other side every step, and both are zero
        /// at the start of a step. Zero for a non-finite or negative time.
        /// </summary>
        public static void Sample(float elapsed, bool crouched, out float lift, out float roll)
        {
            lift = 0f;
            roll = 0f;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f)
                return;
            float phase = (float)Math.Sin(elapsed * (crouched ? CrouchStepRate : StepRate) * Math.PI);
            lift = Math.Abs(phase) * (crouched ? CrouchStepLift : StepLift);
            if (crouched)
                roll = phase * CrouchRollDegrees;
        }
    }
}
