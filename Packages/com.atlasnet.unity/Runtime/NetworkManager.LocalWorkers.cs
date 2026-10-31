using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    public enum HandoffDebugState { None, Entering, Exiting }

    /// <summary>Optional local diagnostics; never used to decide authority.</summary>
    public interface IHandoffDebugProvider
    {
        HandoffDebugState GetHandoffStateForDebug(NetworkObject obj);
    }

    public sealed partial class NetworkManager
    {
        public bool IsWorker => Backend.IsWorker;
        public ulong LocalWorkerId => Backend.LocalWorkerId;
        public int WorkerCount => Backend.WorkerCount;
        public int PendingHandoffCount => Backend.PendingHandoffCount;
        public IReadOnlyList<Vector2> LocalRegion => Backend.LocalRegion;
        public int LocalRegionVersion => Backend.LocalRegionVersion;
        public IReadOnlyList<Vector2> ClientDebugRegion => Backend.ClientDebugRegion;
        public EntityId ClientDebugRegionEntity => Backend.ClientDebugRegionEntity;
        public ulong ClientDebugRegionWorker => Backend.ClientDebugRegionWorker;
        public uint ClientDebugRegionEpoch => Backend.ClientDebugRegionEpoch;
        public int ClientDebugRegionVersion => Backend.ClientDebugRegionVersion;
        public int LocalAuthorityCount => Backend.LocalAuthorityCount;
        public int GhostCount => Backend.GhostCount;

        public HandoffDebugState GetHandoffStateForDebug(NetworkObject obj)
            => Backend is IHandoffDebugProvider debug ? debug.GetHandoffStateForDebug(obj) : HandoffDebugState.None;

        public bool SetClientDebugRegionEnabled(NetworkObject ownedPlayer, bool enabled) => Backend.SetClientDebugRegionEnabled(ownedPlayer, enabled);
        public bool RequestLocalRegionForDebug() => Backend.RequestLocalRegionForDebug();
        public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj) => Backend.ShouldRenderWorkerCopyForDebug(obj);
    }
}
