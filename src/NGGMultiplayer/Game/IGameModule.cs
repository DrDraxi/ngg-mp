using NGGMultiplayer.Core.Session;
using UnityEngine;

namespace NGGMultiplayer.Game
{
    /// <summary>
    /// One self-contained part of the game integration (Lifecycle, Presence, Progression). Created and driven by
    /// <see cref="NggGameAdapter"/>; every member runs on Unity's main thread. A module talks to the others only
    /// through <see cref="GameContext"/> and the shared contracts, never by referencing another module's types.
    /// </summary>
    public interface IGameModule
    {
        /// <summary>Short name for logs and feature keys.</summary>
        string Name { get; }

        /// <summary>
        /// Once, at plugin load: apply Harmony patches (through <see cref="GameContext.Patcher"/>), register RPCs,
        /// install providers and subscribe to context events. Throwing disables the module.
        /// </summary>
        void Install(GameContext context);

        /// <summary>Plugin teardown. Undo subscriptions and destroy objects; Harmony patches are removed by the plugin.</summary>
        void Uninstall();

        /// <summary>Every frame (Update), after the network was polled. <paramref name="deltaTime"/> is unscaled.</summary>
        void Tick(float deltaTime);

        /// <summary>Host: the session started. Client: the host's save was loaded (first snapshot applied).</summary>
        void OnSessionReady(bool isHost);

        /// <summary>The session ended. On a client the player's own save has been restored by now.</summary>
        void OnSessionEnded();
    }

    /// <summary>
    /// Supplies the local player's poses and the visuals of other players. Implemented by the Presence module and
    /// installed with <see cref="GameContext.SetPresenceProvider"/>; the adapter forwards to it.
    /// </summary>
    public interface IPresenceProvider
    {
        /// <summary>The local golfer; false while there is none (menus, loading). Set <see cref="GamePose.Flags"/>.</summary>
        bool TryGetLocalPlayerPose(out GamePose pose);

        /// <summary>The local player's ball; false while there is none.</summary>
        bool TryGetLocalBallPose(out GamePose pose);

        /// <summary>Another player's avatar; parented by the caller. Null uses a placeholder.</summary>
        GameObject CreateRemotePlayerVisual(PlayerInfo player);

        /// <summary>Another player's ghost ball; parented by the caller. Null uses a placeholder.</summary>
        GameObject CreateRemoteBallVisual(PlayerInfo player);
    }

    /// <summary>
    /// Optional component on a remote visual's root (any MonoBehaviour implementing it). The remote view calls it
    /// with every applied pose, so the visual can switch walk/golf poses or tint a moving ball.
    /// </summary>
    public interface IRemoteVisual
    {
        /// <summary>
        /// <paramref name="flags"/> are the owner's <see cref="PoseFlags"/>; velocity is world space, m/s.
        /// <paramref name="teleport"/> is set while the pose is a teleport (Core snapped the view to it instead of
        /// interpolating); it stays set until the next state is reached, so a visual reacting to the snap looks for its
        /// rising edge. Called after the view's transform was moved to the pose.
        /// </summary>
        void OnRemotePose(byte flags, Vector3 velocity, bool teleport);
    }
}
