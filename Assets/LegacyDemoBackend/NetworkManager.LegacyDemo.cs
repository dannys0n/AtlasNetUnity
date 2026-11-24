using UnityEngine;

namespace AtlasNet
{
    /// <summary>Optional graceful scale-down support for the local development backend.</summary>
    public interface ILocalWorkerDrainProvider
    {
        bool BeginWorkerDrain(ulong worker);
        bool IsWorkerDrained(ulong worker);
    }

    public sealed partial class NetworkManager
    {
        [SerializeField, Tooltip("Development-only: hand off server-owned objects between joined local workers by X/Z region.")]
        internal bool automaticLocalHandoffs;
        [SerializeField, Tooltip("Dummy backend only. Its position is sampled when the coordinating server starts; null uses world zero.")] internal Transform localPartitionCenter;
        [SerializeField, Min(1), Tooltip("Half-size of the debug polygon window. This does not limit ownership or interest.")] internal float localRegionDebugExtent = 10000f;
        [SerializeField, Min(0)] internal float localBoundaryMargin = 0.75f;

        public bool AutomaticLocalHandoffs => automaticLocalHandoffs;

        /// <summary>Configure a stopped, embedded development worker from a local coordinator.</summary>
        public void ConfigureLocalDemoWorker(NetworkManager coordinator)
        {
            if (IsRunning) throw new System.InvalidOperationException("Stop the worker before configuring it.");
            SetBackend(coordinator.backendProvider);
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
    }
}
