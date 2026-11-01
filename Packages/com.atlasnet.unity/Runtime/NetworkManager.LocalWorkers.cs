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

    /// <summary>Optional graceful scale-down support for the local development backend.</summary>
    public interface ILocalWorkerDrainProvider
    {
        bool BeginWorkerDrain(ulong worker);
        bool IsWorkerDrained(ulong worker);
    }

    public sealed partial class NetworkManager
    {
        /// <summary>Configure a stopped, embedded development worker from a local coordinator.</summary>
        public void ConfigureLocalDemoWorker(NetworkManager coordinator)
        {
            if (IsRunning) throw new System.InvalidOperationException("Stop the worker before configuring it.");
            if (coordinator.backendProvider != null) throw new System.InvalidOperationException("Local workers require the default dummy backend.");
            playerPrefab = coordinator.playerPrefab;
            networkPrefabsLists = coordinator.networkPrefabsLists;
            tickRate = coordinator.tickRate; logLevel = coordinator.logLevel;
            localPartitionCenter = coordinator.localPartitionCenter;
            localRegionDebugExtent = coordinator.localRegionDebugExtent;
            localBoundaryMargin = coordinator.localBoundaryMargin;
            transportProvider = coordinator.transportProvider;
            ConfigureConnection("127.0.0.1", coordinator.Port);
        }
        public void SetLocalDemoHandoffs(bool enabled) => automaticLocalHandoffs = enabled;

        public bool BeginLocalWorkerDrain(ulong worker) => Backend is ILocalWorkerDrainProvider drain && drain.BeginWorkerDrain(worker);
        public bool IsLocalWorkerDrained(ulong worker) => Backend is ILocalWorkerDrainProvider drain && drain.IsWorkerDrained(worker);
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
        /// <summary>Include coordinator replicas held only for viewer interest in the local debug view. Does not change subscriptions.</summary>
        public bool ShowViewerInterestForDebug { get; set; } = true;
        public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj) => Backend.ShouldRenderWorkerCopyForDebug(obj);
    }
}
