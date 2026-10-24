using NGGMultiplayer.Core.Net;

namespace NGGMultiplayer.Core.Sync
{
    /// <summary>
    /// The game-side object behind a networked entity (implemented by the plugin's adapters). Callbacks run during
    /// <see cref="NetEntityRegistry.Update"/> or packet dispatch, on the thread driving the session; exceptions are
    /// logged and swallowed.
    /// </summary>
    public interface INetEntity
    {
        /// <summary>
        /// Called on the owner at the send rate. Return false to skip this tick. <see cref="TransformState.Time"/>
        /// is overwritten with the registry's clock. Set <see cref="TransformState.Teleport"/> for one capture
        /// after the entity jumps.
        /// </summary>
        bool CaptureState(out TransformState state);

        /// <summary>Called on non-owners every <see cref="NetEntityRegistry.Update"/> with the interpolated pose.</summary>
        void ApplyState(in TransformState state, SnapshotSampleKind kind);

        /// <summary>Ownership moved. <paramref name="isLocal"/> is true when this peer now owns and simulates the entity.</summary>
        void OnOwnershipChanged(PeerId owner, bool isLocal);

        /// <summary>The entity left the session; the registry no longer references this object.</summary>
        void OnDespawned(DespawnReason reason);
    }

    /// <summary>Creates game objects for entities spawned by the host (on clients, and on the host when no instance is given).</summary>
    public interface INetEntityFactory
    {
        /// <summary>
        /// Builds the object for <paramref name="entity"/>; its <see cref="NetEntity.LastState"/> is the spawn pose.
        /// Returning null keeps the entity tracked without a game object.
        /// </summary>
        INetEntity CreateEntity(NetEntity entity);
    }
}
