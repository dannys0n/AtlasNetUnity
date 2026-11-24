using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    public enum NetworkLogLevel { Full, Warnings, Errors, None }

    /// <summary>Unity component and gameplay API. Backend routing is an internal implementation detail.</summary>
    [AddComponentMenu("AtlasNet/Network Manager")]
    [DisallowMultipleComponent]
    public sealed partial class NetworkManager : MonoBehaviour
    {
        [SerializeField, InspectorName("Player Prefab")] internal NetworkObject playerPrefab;
        [SerializeField, InspectorName("Network Prefabs Lists")] internal NetworkPrefabsList[] networkPrefabsLists;
        [SerializeField, Range(10, 120)] internal int tickRate = 30;
        [SerializeField, Tooltip("AtlasNet messages only. Warnings includes errors; None suppresses explicit AtlasNet logging, not Unity engine diagnostics or thrown exceptions.")]
        private NetworkLogLevel logLevel = NetworkLogLevel.Warnings;
        public NetworkLogLevel LogLevel
        {
            get => logLevel;
            set
            {
                if (value < NetworkLogLevel.Full || value > NetworkLogLevel.None) throw new ArgumentOutOfRangeException(nameof(value));
                logLevel = value;
            }
        }
        public bool ShouldLog(LogType severity)
        {
            if (logLevel == NetworkLogLevel.None) return false;
            if (severity == LogType.Error || severity == LogType.Exception || severity == LogType.Assert) return true;
            if (severity == LogType.Warning) return logLevel <= NetworkLogLevel.Warnings;
            return logLevel == NetworkLogLevel.Full;
        }
        // Guard interpolated messages with ShouldLog at the call site to avoid formatting suppressed logs.
        public void Log(LogType severity, string message)
        {
            if (ShouldLog(severity)) Debug.unityLogger.Log(severity, (object)message, this);
        }
        [SerializeField] internal int port = 7777;
        [SerializeField] internal string localAddress = "127.0.0.1";
        internal readonly Dictionary<EntityId, NetworkObject> Replicas = new Dictionary<EntityId, NetworkObject>();
        [SerializeField] private WorldBackendProvider backendProvider;
        [SerializeField] private bool editorLoopback;
        public bool UsesEditorLoopback => editorLoopback;
        [SerializeField] internal NetworkTransportProvider transportProvider;
        [SerializeField] internal ServerTransport serverTransport=ServerTransport.UdpAndWebSocket;
        [SerializeField] internal ClientTransport clientTransport=ClientTransport.Udp;
        [SerializeField] internal string listenAddress="0.0.0.0";
        private IWorldBackend backend;
        private bool backendUsesEditorLoopback;
        [SerializeField, Min(0), Tooltip("WebSocket inactivity timeout in milliseconds. Zero disables idle disconnects; configure before starting networking.")]
        private int webSocketDisconnectTimeoutMS = 30000;
        public int WebSocketDisconnectTimeoutMS
        {
            get => webSocketDisconnectTimeoutMS;
            set
            {
                if (IsRunning) throw new InvalidOperationException("Configure the timeout before starting networking.");
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                webSocketDisconnectTimeoutMS = value;
            }
        }
        [SerializeField, Min(0), Tooltip("UDP inactivity timeout in milliseconds. Zero disables idle disconnects; configure before starting networking.")]
        private int udpDisconnectTimeoutMS = 30000;
        public int UdpDisconnectTimeoutMS
        {
            get => udpDisconnectTimeoutMS;
            set
            {
                if (IsRunning) throw new InvalidOperationException("Configure the timeout before starting networking.");
                if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
                udpDisconnectTimeoutMS = value;
            }
        }
        public ServerTransport ServerConnection=>serverTransport;
        public ClientTransport ClientConnection=>clientTransport;
        public string Address=>localAddress;
        public string ListenAddress=>listenAddress;
        public int Port=>port;
        // Configure before starting; serialized prefabs remain the normal workflow.
        public void ConfigureConnection(string address,int port,ClientTransport client=ClientTransport.Udp,ServerTransport server=ServerTransport.UdpAndWebSocket)
        {
            if(IsRunning)throw new InvalidOperationException("Stop networking before changing connection settings.");
            if(port<1||port>65535)throw new ArgumentOutOfRangeException(nameof(port));
            localAddress=address;this.port=port;clientTransport=client;serverTransport=server;
        }
        public void SetBackend(WorldBackendProvider provider)
        {
            if(backend!=null&&backend.IsRunning)throw new InvalidOperationException("Stop networking before changing backend.");
            backend?.Stop();backend=null;backendProvider=provider;
        }
        public Func<SessionId,byte[],bool> ConnectionApproval { get; set; }
        public byte[] ConnectionData { get; set; }=Array.Empty<byte>();
        private IWorldBackend Backend
        {
            get
            {
                if (backend == null || (!backend.IsRunning && backendUsesEditorLoopback != editorLoopback))
                {
                    var spawnPosition = backend?.PlayerSpawnPosition;
                    backendUsesEditorLoopback = editorLoopback;
                    if (editorLoopback)
                    {
#if UNITY_EDITOR
                        backend = new EditorLoopbackBackend(this);
#else
                        throw new InvalidOperationException("Editor Loopback is unavailable in player builds.");
#endif
                    }
                    else if (backendProvider)
                        backend = backendProvider.CreateBackend(this);
                    else
#if ATLASNET_STANDALONE
                        throw new InvalidOperationException("Set a backend provider before starting standalone tests.");
#else
                        backend = new NativeShardBackend(this, null);
#endif
                    if(backend==null)throw new InvalidOperationException("Backend provider returned null.");
                    if (spawnPosition != null) backend.PlayerSpawnPosition = spawnPosition;
                    backend.SessionJoined += HandleSessionJoined;
                    backend.SessionLeft += HandleSessionLeft;
                    backend.ServerStarted += () => OnServerStarted?.Invoke();
                    backend.Stopped += HandleStopped;
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
        public TransportCapabilities GetTransportCapabilities(SessionId peer=default)=>Backend.GetTransportCapabilities(peer);
        public int ObserverCopies => Backend.ObserverCopies;
        public bool TryGetPeerMetrics(SessionId session, out long sent, out float ping)
        {
            if (backend is IWorldBackendTelemetry telemetry)
                return telemetry.TryGetPeerMetrics(session, out sent, out ping);
            sent = 0;
            ping = -1;
            return false;
        }

        public long BytesSent => Backend.BytesSent;
        public long BytesSentLastTick => Backend.BytesSentLastTick;
        public long AllocatedBytesLastTick => Backend.AllocatedBytesLastTick;
        public double LastTickMilliseconds => Backend.LastTickMilliseconds;
        public int TickRate => tickRate;
        internal float TickInterpolation => Backend is IWorldBackendTelemetry telemetry ? Mathf.Clamp01(telemetry.TickInterpolation) : 1f;
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
        private void LateUpdate() => (backend as IFlushableWorldBackend)?.FlushSend();
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
        /// <summary>Coordinator-only despawn, including entities whose Unity replica is resident on another worker.</summary>
        public bool TryDespawn(EntityId id) => Backend.TryDespawn(id);
        public void HideFrom(NetworkObject obj, SessionId session) => Backend.HideFrom(obj, session);
        public void ShowTo(NetworkObject obj, SessionId session) => Backend.ShowTo(obj, session);
        internal bool CanSimulate(NetworkObject obj) => Backend.CanSimulate(obj);
        internal void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value) => Backend.SendVariable(behaviour, id, value);
        internal void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write) => Backend.SendRpc(behaviour, destination, target, method, write);
        internal void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write) => Backend.SendAuthorityInteraction(source, target, method, write);
        internal void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation, Vector3 scale) => Backend.SendTransform(component, flags, position, rotation, scale);
        internal void SendAnimator(NetworkAnimator component, byte[] payload) => Backend.SendAnimator(component, payload);
        public bool TryGet(EntityId id, out NetworkObject obj) => Replicas.TryGetValue(id, out obj);

        private readonly Dictionary<string, NetworkObject> prefabRegistry = new(StringComparer.Ordinal);
        private readonly List<NetworkObject> backendTickObjects = new();

        /// <summary>Resolve a prefab previously registered by ValidatePrefabRegistry.</summary>
        public bool TryGetRegisteredPrefab(string prefabId, out NetworkObject prefab)
            => prefabRegistry.TryGetValue(prefabId, out prefab);

        /// <summary>Run behaviour ticks once for this backend tick, after updating the backend clock.</summary>
        public void TickBackendReplicas() => TickBackendReplicas(backendTickObjects);

        internal void TickBackendReplicas(List<NetworkObject> tickObjects)
        {
            tickObjects.Clear();
            tickObjects.AddRange(Replicas.Values);
            foreach (var obj in tickObjects)
                if (obj != null && obj.IsSpawned)
                    foreach (var behaviour in obj.Behaviours)
                        behaviour.OnNetworkTick();
        }

        /// <summary>Capture initial state filtered for the receiving session.</summary>
        public byte[] CaptureReplicaSnapshot(EntityId id, SessionId recipient)
        {
            if (!Replicas.TryGetValue(id, out var obj))
                throw new InvalidOperationException($"No replica {id} to capture.");
            using var writer = new NetWriter();
            obj.WriteSnapshot(writer, recipient);
            return writer.ToArray();
        }

        /// <summary>Refresh an existing replica from an authoritative backend snapshot.</summary>
        public void ApplyBackendReplicaSnapshot(EntityId id, NetReader snapshot)
        {
            if (Replicas.TryGetValue(id, out var obj)) obj.ReadSnapshot(snapshot);
        }

        /// <summary>Apply a committed authority change after updating backend routing.</summary>
        public void SetBackendReplicaAuthority(EntityId id, ulong worker, uint epoch)
        {
            if (Replicas.TryGetValue(id, out var obj)) obj.SetSimulationAuthority(worker, epoch);
        }

        /// <summary>Apply a variable update after validating its sender and read permission.</summary>
        public void DispatchBackendVariable(EntityId id, byte behaviour, ushort variable, byte[] payload)
            => GetReplicaBehaviour<NetworkBehaviour>(id, behaviour)?.ReadVariable(variable, payload);

        /// <summary>Apply a pose relayed by the authoritative backend.</summary>
        public void DispatchBackendTransform(EntityId id, byte behaviour, byte flags, Vector3 position, Quaternion rotation, Vector3 scale = default)
        {
            if ((flags & ~7) != 0) throw new ArgumentOutOfRangeException(nameof(flags));
            GetReplicaBehaviour<NetworkTransform>(id, behaviour)?.AcceptServerState(flags, position, rotation, scale);
        }

        /// <summary>Apply animation data relayed by the authoritative backend.</summary>
        public void DispatchBackendAnimator(EntityId id, byte behaviour, byte[] payload)
            => GetReplicaBehaviour<NetworkAnimator>(id, behaviour)?.AcceptServerState(payload);

        private T GetReplicaBehaviour<T>(EntityId id, byte index) where T : NetworkBehaviour
        {
            if (!Replicas.TryGetValue(id, out var obj)) return null;
            if (index >= obj.Behaviours.Length || !(obj.Behaviours[index] is T behaviour))
                throw new InvalidOperationException($"Invalid backend behaviour index {index} for entity {id}.");
            return behaviour;
        }

        /// <summary>Validate and index the configured lists for backend adapter lookup.</summary>
        public void ValidatePrefabRegistry()
        {
            prefabRegistry.Clear();
            if (networkPrefabsLists != null)
            {
                foreach (var list in networkPrefabsLists)
                {
                    if (list == null)
                        throw new InvalidOperationException("AtlasNet Network Prefabs Lists contains a missing list asset");
                    for (int prefabIndex = 0; prefabIndex < list.Prefabs.Count; prefabIndex++)
                    {
                        var prefab = list.Prefabs[prefabIndex];
                        if (prefab == null)
                            throw new InvalidOperationException($"AtlasNet Network Prefabs List '{list.name}' has a missing NetworkObject prefab at index {prefabIndex}");
                        if (string.IsNullOrWhiteSpace(prefab.PrefabId))
                            throw new InvalidOperationException($"AtlasNet prefab '{prefab.name}' in list '{list.name}' has no generated Prefab ID. Reimport or resave its prefab asset");
                        if (prefabRegistry.TryGetValue(prefab.PrefabId, out var existing))
                        {
                            if (existing == prefab) continue;
                            throw new InvalidOperationException($"Duplicate AtlasNet prefab ID '{prefab.PrefabId}' in Network Prefabs Lists");
                        }
                        prefabRegistry.Add(prefab.PrefabId, prefab);
                    }
                }
            }
            if (playerPrefab != null &&
                (!prefabRegistry.TryGetValue(playerPrefab.PrefabId, out var registeredPlayer) || registeredPlayer != playerPrefab))
                throw new InvalidOperationException($"AtlasNet Player Prefab '{playerPrefab.name}' must be registered in a Network Prefabs List assigned to NetworkManager");
        }

        // Native adapters deliver lifecycle notifications on Unity's main thread.
        // Keep GameObject ownership here so replacing routing does not replace the Unity component.
        private NetworkObject InstantiateReplica(NetworkObject prefab, Vector3 position, Quaternion rotation)
            => Instantiate(prefab, position, rotation);

        public NetworkObject CreateReplica(NetworkObject prefab, EntityId id, SessionId owner,
            Vector3 position, Quaternion rotation, ulong worker, uint epoch, NetReader snapshot = null, bool migration = false)
            => CreateReplica(prefab, id, owner, position, rotation, worker, epoch, snapshot, migration, null);

        // The dummy directory can retain newer variable deltas than its last full
        // worker snapshot. Apply those before gameplay's spawn callback, like NGO.
        internal NetworkObject CreateReplica(NetworkObject prefab, EntityId id, SessionId owner,
            Vector3 position, Quaternion rotation, ulong worker, uint epoch, NetReader snapshot, bool migration,
            Action<NetworkObject> initializeState)
        {
            if (Replicas.ContainsKey(id)) throw new InvalidOperationException($"Duplicate local replica {id}");
            var obj = InstantiateReplica(prefab, position, rotation);
            try
            {
                obj.Initialize(this, id, owner, deferSpawnCallbacks: true);
                obj.SetSimulationAuthority(worker, epoch);
                if (snapshot != null) { if(migration)obj.ReadHandoff(snapshot);else obj.ReadSnapshot(snapshot); }
                Replicas.Add(id, obj);
                initializeState?.Invoke(obj);
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

        // Adapter API matching native extract/restore callbacks. Capturing does
        // not delete the source; the backend commits or rolls back residency.
        public byte[] CaptureMigrationState(EntityId id)
        {
            if(!Replicas.TryGetValue(id,out var obj))throw new InvalidOperationException($"No replica {id} to capture.");
            using var writer=new NetWriter();obj.WriteHandoff(writer);return writer.ToArray();
        }
        public NetworkObject RestoreMigratedReplica(NetworkObject prefab,EntityId id,SessionId owner,ulong worker,uint epoch,byte[] payload)
        {
            using var reader=new NetReader(payload);
            return CreateReplica(prefab,id,owner,Vector3.zero,Quaternion.identity,worker,epoch,reader,migration:true);
        }
        public void DispatchBackendRpc(EntityId id,byte behaviour,uint method,byte[] payload,SessionId sender,RpcDestination destination,SessionId target=default)
        {
            GetReplicaBehaviour<NetworkBehaviour>(id, behaviour)?.ReceiveRpc(method, payload, sender, destination, target);
        }

        public void RemoveReplica(EntityId id)
        {
            if (!Replicas.TryGetValue(id, out var obj)) return;
            Replicas.Remove(id);
            DestroyReplicaObject(obj);
        }
    }
}
