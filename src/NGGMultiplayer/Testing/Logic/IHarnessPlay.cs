using System.Collections.Generic;
using NGGMultiplayer.Core.Protocol;

namespace NGGMultiplayer.Testing
{
    /// <summary>
    /// What the harness needs to play the game in this copy like the player at the PC would, without keyboard or mouse
    /// (two test copies side by side: only one can have focus, and the Input System drops an unfocused window's keys):
    /// move the local player next to an object, use objects, start bounties, switch golf / walk, swing, and read the
    /// state that the multiplayer scenarios check (locks, dialogue, bounty, remote views). Everything goes through the
    /// game's own code (its teleport, Interact, StartBounty, swing), so the mod's patches see it like real play.
    /// Implemented over the game by <c>HarnessPlay</c>; tests use a fake. Every member reports what it cannot do
    /// instead of throwing.
    /// </summary>
    public interface IHarnessPlay
    {
        /// <summary>Objects of a <see cref="PlayArgs.Categories"/> category in the loaded world; null with an error
        /// when there is no world.</summary>
        IReadOnlyList<SceneObject> FindObjects(string category, out string error);

        /// <summary>Moves the walking local player beside <paramref name="target"/> (onto a tee, pad spot or trigger).</summary>
        bool Goto(SceneObject target, out string message);

        /// <summary>
        /// Moves the walking local player to <paramref name="feet"/> (snapped to the ground below), facing
        /// <paramref name="yaw"/> degrees when given.
        /// </summary>
        bool GotoPosition(NetVector3 feet, float? yaw, out string message);

        /// <summary>Uses <paramref name="target"/> as the game does: FPSInteractable.Interact, StandHere.Activate, the
        /// pad's StartBounty, a DialogueTrigger entered by the player.</summary>
        bool Interact(SceneObject target, out string message);

        /// <summary>Starts the bounty of the set with this clean name at its pad.</summary>
        bool StartBounty(string setName, out string message);

        /// <summary>Leaves the bounty being played (ChallengeSystem.ExitBountyChallenge).</summary>
        bool ExitBounty(out string message);

        /// <summary>"key value" lines: the active pad, the loaded and main sets, the current challenge.</summary>
        string DescribeBounty();

        /// <summary>A real local shot through the game's swing code.</summary>
        bool Shoot(ShotRequest shot, out string message);

        /// <summary>HitSequeneceManager.Restart: <paramref name="resume"/> plays the ball where it lies.</summary>
        bool RestartShot(bool resume, out string message);

        /// <summary>Golf stance (true) or walking (false), through the game's transitions.</summary>
        bool SetMode(bool golf, out string message);

        /// <summary>Gates, doors, sliding doors, padlocks, RFID readers and bounty pad locks with their state.</summary>
        string DescribeLocks(string filter);

        /// <summary>Dialogue / cutscene state: m_moveLocked, the dialogue manager's flags, playing videos.</summary>
        string DescribeDialogue();

        /// <summary>DialogueManager.NextButtonPressed (the dialogue's next button).</summary>
        bool DialogueNext(out string message);

        /// <summary>The remote players' views (avatars, ghost balls) with their renderers and trails.</summary>
        string DescribeRemotes();

        /// <summary>The local player and ball: position, mode, shot state.</summary>
        string DescribePlayer();
    }
}
