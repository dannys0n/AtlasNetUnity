using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>Unity component and gameplay API. Backend routing is an internal implementation detail.</summary>
    [AddComponentMenu("AtlasNet/Network Manager")]
    [DisallowMultipleComponent]
    public sealed partial class NetworkManager : MonoBehaviour
    {
        [SerializeField, InspectorName("Player Prefab")] internal NetworkObject playerPrefab;
        [SerializeField, InspectorName("Network Prefabs Lists")] internal NetworkPrefabsList[] networkPrefabsLists;
        [SerializeField, Range(10, 120)] internal int tickRate = 30;
        [SerializeField] internal int port = 7777;
        [SerializeField] internal string localAddress = "127.0.0.1";
        [SerializeField, Tooltip("Development-only: hand off server-owned objects between joined local workers by X/Z region.")]
        internal bool automaticLocalHandoffs;
        [SerializeField] internal Vector2 localWorldMin = new Vector2(-20, -20);
        [SerializeField] internal Vector2 localWorldMax = new Vector2(20, 20);
        [SerializeField, Min(0)] internal float localBoundaryMargin = 0.75f;


        internal readonly Dictionary<EntityId, NetworkObject> Replicas = new Dictionary<EntityId, NetworkObject>();
        private IWorldBackend backend;
        private IWorldBackend Backend
        {
            get
            {
                if (backend == null)
                {
                    backend = new LocalWorldBackend(this);
                    backend.SessionJoined += session => SessionJoined?.Invoke(session);
                    backend.SessionLeft += session => SessionLeft?.Invoke(session);
                }
                return backend;
            }
        }

        public bool IsServer => Backend.IsServer;
        public bool IsClient => Backend.IsClient;
        public bool IsHost => Backend.IsHost;
        public bool IsRunning => Backend.IsRunning;
        public SessionId LocalSession => Backend.LocalSession;
        public uint Tick => Backend.Tick;
        public int TrackedEntityCount => Backend.TrackedEntityCount;
        public int RemoteClientCount => Backend.RemoteClientCount;
        public int ObserverCopies => Backend.ObserverCopies;
        public long BytesSent => Backend.BytesSent;
        public long BytesSentLastTick => Backend.BytesSentLastTick;
        public long AllocatedBytesLastTick => Backend.AllocatedBytesLastTick;
        public double LastTickMilliseconds => Backend.LastTickMilliseconds;
        public int TickRate => tickRate;
        public bool AutomaticLocalHandoffs => automaticLocalHandoffs;
        public NetworkObject PlayerPrefab => playerPrefab;
        public int SpawnedCount => Replicas.Count;
        public IEnumerable<NetworkObject> SpawnedObjects => Replicas.Values;
        public Func<SessionId, Vector3> PlayerSpawnPosition
        {
            get => Backend.PlayerSpawnPosition;
            set => Backend.PlayerSpawnPosition = value;
        }
        public event Action<SessionId> SessionJoined;
        public event Action<SessionId> SessionLeft;

        private void Awake() => ValidateRegistry();
        private void Update() => Backend.PollAndTick(Time.deltaTime);
        private void OnDestroy() { if (backend != null) backend.Stop(); }

        public void ValidateRegistry() => Backend.ValidateRegistry();
        public void StartServer() => Backend.StartServer();
        public void StartHost() => Backend.StartHost();
        public void StartClient() => Backend.StartClient();
        public void StartWorker() => Backend.StartWorker();
        public void Stop() => Backend.Stop();
        public NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default) => Backend.Spawn(prefabId, position, rotation, owner);
        public NetworkObject Spawn(NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default) => Backend.Spawn(prefab, position, rotation, owner);
        public void RequestSpawn(NetworkObject source, NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default) => Backend.RequestSpawn(source, prefab, position, rotation, owner);
        public void Despawn(NetworkObject obj) => Backend.Despawn(obj);
        public void HideFrom(NetworkObject obj, SessionId session) => Backend.HideFrom(obj, session);
        public void ShowTo(NetworkObject obj, SessionId session) => Backend.ShowTo(obj, session);
        internal bool CanSimulate(NetworkObject obj) => Backend.CanSimulate(obj);
        internal void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value) => Backend.SendVariable(behaviour, id, value);
        internal void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write) => Backend.SendRpc(behaviour, destination, target, method, write);
        internal void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write) => Backend.SendAuthorityInteraction(source, target, method, write);
        internal void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation) => Backend.SendTransform(component, flags, position, rotation);
        internal void SendAnimator(NetworkAnimator component, byte[] payload) => Backend.SendAnimator(component, payload);
        public bool TryGet(EntityId id, out NetworkObject obj) => Replicas.TryGetValue(id, out obj);

        // Native adapters deliver lifecycle notifications on Unity's main thread.
        // Keep GameObject ownership here so replacing routing does not replace the Unity component.
        private NetworkObject InstantiateReplica(NetworkObject prefab, Vector3 position, Quaternion rotation)
            => Instantiate(prefab, position, rotation);

        internal NetworkObject CreateReplica(NetworkObject prefab, EntityId id, SessionId owner,
            Vector3 position, Quaternion rotation, ulong worker, uint epoch, NetReader snapshot = null)
        {
            if (Replicas.ContainsKey(id)) throw new InvalidOperationException($"Duplicate local replica {id}");
            var obj = InstantiateReplica(prefab, position, rotation);
            try
            {
                obj.Initialize(this, id, owner, deferSpawnCallbacks: true);
                obj.SetSimulationAuthority(worker, epoch);
                if (snapshot != null) obj.ReadSnapshot(snapshot);
                Replicas.Add(id, obj);
                obj.CompleteSpawn();
                return obj;
            }
            catch
            {
                Replicas.Remove(id);
                DestroyReplicaObject(obj);
                throw;
            }
        }

        internal void DestroyReplicaObject(NetworkObject obj)
        {
            if (obj == null) return;
            if (obj.IsSpawned) obj.Shutdown();
            obj.gameObject.SetActive(false);
            Destroy(obj.gameObject);
        }

        internal void RemoveReplica(EntityId id)
        {
            if (!Replicas.TryGetValue(id, out var obj)) return;
            Replicas.Remove(id);
            DestroyReplicaObject(obj);
        }
    }
}
