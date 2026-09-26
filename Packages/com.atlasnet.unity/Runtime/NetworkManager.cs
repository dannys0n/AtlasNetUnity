using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AtlasNet
{
    /// <summary>Local runtime. Gameplay code addresses entities and sessions, not sockets.</summary>
    [AddComponentMenu("AtlasNet/Network Manager")]
    [DisallowMultipleComponent]
    public sealed partial class NetworkManager : MonoBehaviour
    {
        private enum Packet : byte { Welcome = 1, Spawn = 2, Despawn = 3, Variable = 4, Rpc = 5, Transform = 6, Hide = 7, Animator = 8,
            ClientHello = 9, WorkerHello = 10, WorkerWelcome = 11, WorkerSpawn = 12, WorkerPrepare = 13,
            WorkerPrepared = 14, WorkerCommit = 15, WorkerTransform = 16, WorkerAuthorityRpc = 17,
            WorkerVariable = 18, WorkerOutboundRpc = 19, AuthorityChange = 20,
            WorkerExportRequest = 21, WorkerExport = 22, WorkerAbort = 23, WorkerAnimator = 24,
            WorkerRegion = 25, WorkerRegionRequest = 26, WorkerSpawnRequest = 27, WorkerDespawnRequest = 28,
            WorkerInteractionRpc = 29, ClientDebugRegionRequest = 30, ClientDebugRegion = 31, WorkerState = 32 }
        private const ushort LocalProtocolVersion = 9;
        [SerializeField, InspectorName("Player Prefab")] private NetworkObject playerPrefab;
        [SerializeField, InspectorName("Network Prefabs Lists")] private NetworkPrefabsList[] networkPrefabsLists;
        [SerializeField, Range(10, 120)] private int tickRate = 30;
        [SerializeField] private int port = 7777;
        [SerializeField] private string localAddress = "127.0.0.1";
        [SerializeField, Tooltip("Development-only: hand off server-owned objects between joined local workers by X/Z region.")]
        private bool automaticLocalHandoffs;
        [SerializeField] private Vector2 localWorldMin = new Vector2(-20, -20);
        [SerializeField] private Vector2 localWorldMax = new Vector2(20, 20);
        [SerializeField, Min(0)] private float localBoundaryMargin = 0.75f;

        private readonly Dictionary<string, NetworkObject> registry = new Dictionary<string, NetworkObject>();
        private readonly Dictionary<EntityId, NetworkObject> spawned = new Dictionary<EntityId, NetworkObject>();
        private readonly Dictionary<EntityId, HashSet<SessionId>> hidden = new Dictionary<EntityId, HashSet<SessionId>>();
        private readonly List<NetworkObject> tickObjects = new List<NetworkObject>();
        private IMessageTransport transport;
        private ulong nextEntity = 1;
        private float accumulator;
        private long lastBytes;
        private bool allocationCounterSupported;
        public bool IsServer { get; private set; }
        public bool IsClient { get; private set; }
        public bool IsHost => IsServer && IsClient;
        public bool IsRunning => transport != null;
        public SessionId LocalSession { get; private set; }
        public uint Tick { get; private set; }
        public int TickRate => tickRate;
        public bool AutomaticLocalHandoffs => automaticLocalHandoffs;
        public NetworkObject PlayerPrefab => playerPrefab;
        /// <summary>Optional spawn placement for automatically created players. Defaults to the prefab's position.</summary>
        public Func<SessionId, Vector3> PlayerSpawnPosition { get; set; }
        /// <summary>Unity replicas currently resident in this process, not the world-wide entity count.</summary>
        public int SpawnedCount => spawned.Count;
        /// <summary>Unity replicas currently resident in this process.</summary>
        public IEnumerable<NetworkObject> SpawnedObjects => spawned.Values;
        /// <summary>Directory entries known to the local coordinator; other instances report local replicas.</summary>
        public int TrackedEntityCount => IsServer && !isWorker ? directory.Count : spawned.Count;
        public int RemoteClientCount => IsServer && !isWorker ? clientSessions.Count : 0;
        public int ObserverCopies
        {
            get
            {
                if (!IsServer || transport == null) return 0;
                int total = 0;
                foreach (var id in directory.Keys)
                    foreach (var session in clientSessions)
                        if (IsObserver(id, session)) total++;
                return total;
            }
        }
        public long BytesSent => transport?.BytesSent ?? 0;
        public long BytesSentLastTick { get; private set; }
        public long AllocatedBytesLastTick { get; private set; }
        public double LastTickMilliseconds { get; private set; }
        public event Action<SessionId> SessionJoined;
        public event Action<SessionId> SessionLeft;

        private void Awake()
        {
            ValidateRegistry();
            long before = GC.GetAllocatedBytesForCurrentThread();
            byte[] probe = new byte[1024];
            GC.KeepAlive(probe);
            allocationCounterSupported = GC.GetAllocatedBytesForCurrentThread() > before;
        }

        public void ValidateRegistry()
        {
            registry.Clear();
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
                        if (registry.TryGetValue(prefab.PrefabId, out var existing))
                        {
                            if (existing == prefab) continue;
                            throw new InvalidOperationException($"Duplicate AtlasNet prefab ID '{prefab.PrefabId}' in Network Prefabs Lists");
                        }
                        registry.Add(prefab.PrefabId, prefab);
                    }
                }
            }
            if (playerPrefab != null &&
                (!registry.TryGetValue(playerPrefab.PrefabId, out var registeredPlayer) || registeredPlayer != playerPrefab))
                throw new InvalidOperationException($"AtlasNet Player Prefab '{playerPrefab.name}' must be registered in a Network Prefabs List assigned to NetworkManager");
        }

        public void StartServer() => StartLocal(true, false);
        public void StartHost() => StartLocal(true, true);
        public void StartClient() => StartLocal(false, true);

        private void StartLocal(bool server, bool client)
        {
            if (IsRunning) throw new InvalidOperationException("NetworkManager is already running");
            ValidateRegistry();
            ILocalAuthorityPlacement world = null;
            if (server)
            {
                world = new LocalVoronoi(localWorldMin.x, localWorldMax.x, localWorldMin.y, localWorldMax.y);
                world.AddWorker(0);
            }
            transport = new LocalTcpTransport(server, localAddress, port);
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;
            IsServer = server;
            IsClient = client;
            localWorld = world;
            LocalSession = server && client ? new SessionId(1) : new SessionId(0);
            Tick = 0;
            accumulator = 0;
            if (LocalSession.Value != 0)
            {
                SpawnPlayer(LocalSession);
                SessionJoined?.Invoke(LocalSession);
            }
            if (!server)
            {
                using (var hello = new NetWriter())
                {
                    hello.Write((byte)Packet.ClientHello);
                    hello.Write(LocalProtocolVersion);
                    transport.SendToServer(hello.ToArray());
                }
            }
        }

        public void Stop()
        {
            if (transport == null) return;
            transport.Dispose();
            transport = null;
            foreach (var obj in new List<NetworkObject>(spawned.Values))
            {
                obj.Shutdown();
                if (obj != null) Destroy(obj.gameObject);
            }
            spawned.Clear();
            directory.Clear();
            hidden.Clear();
            clientSessions.Clear();
            clientResidents.Clear();
            workerSessions.Clear();
            workerResidents.Clear();
            pendingHandoffs.Clear();
            queuedAuthorityRpcs.Clear();
            lastHandoffTick.Clear();
            localWorld = null;
            debugRegionSubscribers.Clear();
            clientDebugRegionSubscribers.Clear();
            debugRegionRequested = false;
            SetLocalRegion(Array.Empty<Vector2>());
            ClearClientDebugRegion();
            isWorker = false;
            localWorkerId = 0;
            IsServer = false;
            IsClient = false;
            LocalSession = new SessionId(0);
        }

        private void OnDestroy() => Stop();

        private void Update()
        {
            if (!IsRunning) return;
            transport.Poll();
            if (!IsRunning) return;
            accumulator += Time.deltaTime;
            float interval = 1f / tickRate;
            while (accumulator >= interval)
            {
                accumulator -= interval;
                long before = Stopwatch.GetTimestamp();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                Tick++;
                CheckHandoffTimeouts();
                tickObjects.Clear();
                tickObjects.AddRange(spawned.Values);
                foreach (var obj in tickObjects)
                    if (obj != null && obj.IsSpawned)
                        foreach (var behaviour in obj.Behaviours) behaviour.OnNetworkTick();
                if (isWorker) SendWorkerSnapshots();
                else if (IsServer)
                    foreach (var obj in tickObjects)
                        if (obj != null && obj.IsSpawned &&
                            (obj.HasAuthority || pendingHandoffs.ContainsKey(obj.EntityId)) &&
                            directory.TryGetValue(obj.EntityId, out var record))
                            record.Capture(obj);
                RefreshWorkerInterest();
                CheckAutomaticHandoffs();
                RefreshClientInterest();
                RefreshCoordinatorResidency();
                LastTickMilliseconds = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
                AllocatedBytesLastTick = allocationCounterSupported
                    ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore : -1;
                BytesSentLastTick = transport.BytesSent - lastBytes;
                lastBytes = transport.BytesSent;
            }
        }

        public NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!IsServer || isWorker) throw new InvalidOperationException("Only the first server can canonically spawn an object in local mode");
            return SpawnCanonical(prefabId, position, rotation, owner, 0);
        }

        private NetworkObject SpawnCanonical(string prefabId, Vector3 position, Quaternion rotation, SessionId owner, ulong simulationWorker)
        {
            if (!registry.TryGetValue(prefabId, out var prefab))
                throw new InvalidOperationException($"Prefab ID '{prefabId}' is not registered on NetworkManager");
            var obj = Instantiate(prefab, position, rotation);
            var id = new EntityId(nextEntity++);
            obj.Initialize(this, id, owner, deferSpawnCallbacks: true);
            obj.SetSimulationAuthority(simulationWorker, 0);
            obj.CompleteSpawn();
            spawned.Add(id, obj);
            RegisterCanonical(obj);
            AddSpawnToWorkerInterest(obj);
            AddSpawnToClientInterest(obj);
            return obj;
        }

        /// <summary>Spawn a registered prefab without repeating its serialized ID in gameplay code.</summary>
        public NetworkObject Spawn(NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            if (!registry.TryGetValue(prefab.PrefabId, out var registered) || registered != prefab)
                throw new InvalidOperationException($"Prefab '{prefab.name}' is not registered on this NetworkManager");
            return Spawn(prefab.PrefabId, position, rotation, owner);
        }

        /// <summary>Ask the local coordinator to spawn a registered object on this entity's simulation worker.
        /// Worker requests complete asynchronously; use OnNetworkSpawn on the new object.</summary>
        public void RequestSpawn(NetworkObject source, NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!IsServer || source == null || source.Manager != this || !source.HasAuthority)
                throw new InvalidOperationException("Only a spawned authority can request a spawn");
            if (prefab == null || !registry.TryGetValue(prefab.PrefabId, out var registered) || registered != prefab)
                throw new InvalidOperationException("Requested prefab is not registered on this NetworkManager");
            if (!isWorker)
            {
                SpawnCanonical(prefab.PrefabId, position, rotation, owner, 0);
                return;
            }
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.WorkerSpawnRequest);
                writer.Write(source.EntityId);
                writer.Write(source.AuthorityEpoch);
                writer.Write(prefab.PrefabId);
                writer.Write(position);
                writer.Write(rotation);
                writer.Write(owner);
                transport.SendToServer(writer.ToArray());
            }
        }

        public void Despawn(NetworkObject obj)
        {
            if (isWorker)
            {
                if (obj == null || obj.Manager != this || !obj.HasAuthority)
                    throw new InvalidOperationException("Only the authoritative worker can request despawn");
                using (var writer = new NetWriter())
                {
                    writer.Write((byte)Packet.WorkerDespawnRequest);
                    writer.Write(obj.EntityId);
                    writer.Write(obj.AuthorityEpoch);
                    transport.SendToServer(writer.ToArray());
                }
                return;
            }
            if (!IsServer) throw new InvalidOperationException("Only a server can despawn an object");
            if (obj == null || obj.Manager != this || !spawned.ContainsKey(obj.EntityId))
                throw new InvalidOperationException("Object is not spawned by this manager");
            DespawnCanonical(obj.EntityId);
        }

        private void DespawnCanonical(EntityId id)
        {
            if (!directory.ContainsKey(id)) throw new InvalidOperationException($"Unknown entity {id}");
            spawned.TryGetValue(id, out var obj);
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Despawn);
                writer.Write(id);
                SendToObservers(id, writer.ToArray());
            }
            spawned.Remove(id);
            directory.Remove(id);
            hidden.Remove(id);
            foreach (var residents in clientResidents.Values) residents.Remove(id);
            pendingHandoffs.Remove(id);
            queuedAuthorityRpcs.Remove(id);
            lastHandoffTick.Remove(id);
            RemoveFromWorkerInterest(id);
            if (obj != null)
            {
                obj.Shutdown();
                obj.gameObject.SetActive(false);
                Destroy(obj.gameObject);
            }
        }

        /// <summary>Remove one client's observer copy; the server's canonical entity remains alive.</summary>
        public void HideFrom(NetworkObject obj, SessionId session)
        {
            if (!IsServer || isWorker) throw new InvalidOperationException("Only the first server changes observers in local mode");
            if (IsClient && session == LocalSession)
                throw new InvalidOperationException("A host shares its canonical object with the local view; host observer hiding is unsupported");
            if (workerSessions.Contains(session)) throw new InvalidOperationException("Workers are not gameplay observers");
            if (obj.Manager != this) throw new InvalidOperationException("Object is not spawned here");
            if (!hidden.TryGetValue(obj.EntityId, out var excluded))
                hidden[obj.EntityId] = excluded = new HashSet<SessionId>();
            bool wasObserver = IsObserver(obj.EntityId, session);
            if (!excluded.Add(session)) return;
            if (clientResidents.TryGetValue(session, out var residents)) residents.Remove(obj.EntityId);
            if (wasObserver) transport.SendTo(session, MakeEntityPacket(Packet.Hide, obj.EntityId));
        }

        public void ShowTo(NetworkObject obj, SessionId session)
        {
            if (!IsServer || isWorker) throw new InvalidOperationException("Only the first server changes observers in local mode");
            if (workerSessions.Contains(session)) throw new InvalidOperationException("Workers are not gameplay observers");
            if (obj.Manager != this) throw new InvalidOperationException("Object is not spawned here");
            if (!hidden.TryGetValue(obj.EntityId, out var excluded) || !excluded.Remove(session)) return;
            if (clientResidents.TryGetValue(session, out var residents))
            {
                if (ShouldClientHold(session, directory[obj.EntityId], false) && residents.Add(obj.EntityId))
                    transport.SendTo(session, MakeSpawn(directory[obj.EntityId], session));
            }
            else transport.SendTo(session, MakeSpawn(directory[obj.EntityId], session));
        }

        /// <summary>Find a local replica; false does not mean the entity is absent from the world.</summary>
        public bool TryGet(EntityId id, out NetworkObject obj) => spawned.TryGetValue(id, out obj);
        internal bool CanSimulate(NetworkObject obj) => IsServer && obj != null && obj.Manager == this &&
            obj.SimulationWorker == localWorkerId && (!isWorker || localWorkerId != 0) &&
            !pendingHandoffs.ContainsKey(obj.EntityId);

        private bool IsExplicitlyVisible(EntityId id, SessionId session) =>
            !hidden.TryGetValue(id, out var excluded) || !excluded.Contains(session);

        private bool IsObserver(EntityId id, SessionId session) =>
            IsExplicitlyVisible(id, session) &&
            (!clientResidents.TryGetValue(session, out var residents) || residents.Contains(id));

        private void SendToObservers(EntityId id, byte[] packet, SessionId except = default)
        {
            foreach (var session in clientSessions)
                if (session != except && IsObserver(id, session)) transport.SendTo(session, packet);
        }

        private byte[] MakeSpawn(CanonicalEntity entity, SessionId reader)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Spawn);
                WriteReplicaState(writer, entity, reader);
                return writer.ToArray();
            }
        }

        private static void WriteReplicaState(NetWriter writer, CanonicalEntity entity, SessionId reader)
        {
            writer.Write(entity.Id);
            writer.Write(entity.PrefabId);
            writer.Write(entity.Owner);
            writer.Write(entity.Position);
            writer.Write(entity.Rotation);
            writer.Write(entity.Worker);
            writer.Write(entity.Epoch);
            writer.WriteRaw(reader == entity.Owner ? entity.OwnerSnapshot : entity.PublicSnapshot);
        }

        private void OnConnected(SessionId session)
        {
            // A connection announces whether it is a gameplay client or a Unity worker.
            // Do not create a player until that distinction is known.
        }

        private void SpawnPlayer(SessionId session)
        {
            if (!IsServer || playerPrefab == null) return;
            Vector3 position = PlayerSpawnPosition != null
                ? PlayerSpawnPosition(session) : playerPrefab.transform.position;
            Spawn(playerPrefab, position, playerPrefab.transform.rotation, session);
        }

        private void OnDisconnected(SessionId session)
        {
            if (isWorker)
            {
                Debug.LogWarning("AtlasNet local coordinator disconnected");
                Stop();
                return;
            }
            if (workerSessions.Remove(session))
            {
                AbortPendingHandoffs(session);
                workerResidents.Remove(session);
                debugRegionSubscribers.Remove(session);
                localWorld?.RemoveWorker(session.Value);
                RefreshWorkerInterest();
                BroadcastLocalRegions();
                Debug.LogWarning($"AtlasNet worker {session} disconnected; its entities remain frozen until authority is recovered");
                return;
            }
            clientSessions.Remove(session);
            clientResidents.Remove(session);
            clientDebugRegionSubscribers.Remove(session);
            if (IsServer)
            {
                SessionLeft?.Invoke(session);
                var owned = new List<EntityId>();
                foreach (var record in directory.Values)
                    if (record.Owner == session) owned.Add(record.Id);
                foreach (var id in owned) DespawnCanonical(id);
            }
            else
            {
                Debug.LogWarning("AtlasNet local server disconnected");
                Stop();
            }
        }

        internal void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value)
        {
            using (var writer = new NetWriter())
            using (var content = new NetWriter())
            {
                value.Write(content);
                writer.Write((byte)Packet.Variable);
                writer.Write(behaviour.NetworkObject.EntityId);
                writer.Write(behaviour.BehaviourIndex);
                writer.Write(id);
                writer.WriteBytes(content.ToArray());
                byte[] data = writer.ToArray();
                if (isWorker)
                {
                    if (!behaviour.HasAuthority)
                        throw new InvalidOperationException("A worker ghost cannot write a NetworkVariable");
                    SendWorkerVariable(behaviour, id, content.ToArray());
                }
                else if (IsServer)
                {
                    if (value.WritePermission == NetworkVariableWritePermission.Owner && behaviour.IsOwner)
                        RememberOwnerUpdate(behaviour.NetworkObject.EntityId, data);
                    foreach (var session in clientSessions)
                        if (IsObserver(behaviour.NetworkObject.EntityId, session) &&
                            (value.ReadPermission == NetworkVariableReadPermission.Everyone || session == behaviour.OwnerSession))
                            transport.SendTo(session, data);
                    SendToWorkers(behaviour.NetworkObject.EntityId, data);
                }
                else if (value.WritePermission == NetworkVariableWritePermission.Owner && behaviour.IsOwner)
                    transport.SendToServer(data);
                else throw new InvalidOperationException("Only the permitted writer can send a NetworkVariable");
            }
        }

        internal void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write)
        {
            var obj = behaviour.NetworkObject;
            if (destination == RpcDestination.Authority && !obj.IsOwner && !obj.HasAuthority)
                throw new InvalidOperationException("Only the controlling session or local authority can send an authority-targeted RPC");
            if (destination != RpcDestination.Authority && destination != RpcDestination.Everyone && !IsServer)
                throw new InvalidOperationException("Only the server can send observer- or client-targeted RPCs");
            if (destination == RpcDestination.Everyone && !IsServer &&
                (behaviour.GetRpcPermission(method) == RpcInvokePermission.Server ||
                 (behaviour.GetRpcPermission(method) == RpcInvokePermission.Owner && !obj.IsOwner)))
                throw new InvalidOperationException("This client cannot invoke the Everyone RPC");
            if (destination == RpcDestination.Everyone && IsServer &&
                behaviour.GetRpcPermission(method) == RpcInvokePermission.Owner && !obj.IsOwner)
                throw new InvalidOperationException("Only the owner may invoke this Everyone RPC");
            if (destination == RpcDestination.Target && target.Value == 0)
                throw new InvalidOperationException("A client-targeted RPC needs a valid target session");
            using (var payload = new NetWriter())
            using (var packet = new NetWriter())
            {
                write?.Invoke(payload);
                byte[] bytes = payload.ToArray();
                packet.Write((byte)Packet.Rpc);
                packet.Write((byte)destination);
                packet.Write(obj.EntityId);
                packet.Write(behaviour.BehaviourIndex);
                packet.Write(target);
                packet.Write(method);
                packet.WriteBytes(bytes);
                byte[] data = packet.ToArray();
                if (isWorker)
                {
                    if (!obj.HasAuthority) throw new InvalidOperationException("A worker ghost cannot send RPCs");
                    if (destination == RpcDestination.Authority)
                        behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                    else
                    {
                        if (destination == RpcDestination.Everyone)
                            behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                        SendWorkerOutboundRpc(obj, data);
                    }
                    return;
                }
                if (destination == RpcDestination.Authority)
                {
                    if (IsServer && (obj.SimulationWorker != 0 || pendingHandoffs.ContainsKey(obj.EntityId)))
                        ForwardAuthorityRpc(obj.EntityId, behaviour.BehaviourIndex, method, bytes, LocalSession);
                    else if (IsServer) behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                    else transport.SendToServer(data);
                }
                else if (destination == RpcDestination.Observers)
                {
                    if (IsClient) behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                    SendToObservers(obj.EntityId, data);
                    SendToWorkers(obj.EntityId, data);
                }
                else if (destination == RpcDestination.Everyone)
                {
                    behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                    if (IsServer)
                    {
                        SendToObservers(obj.EntityId, data);
                        SendToWorkers(obj.EntityId, data);
                    }
                    else transport.SendToServer(data);
                }
                else
                {
                    if (IsClient && target == LocalSession) behaviour.ReceiveRpc(method, bytes, LocalSession, destination, target);
                    else if (IsObserver(obj.EntityId, target)) transport.SendTo(target, data);
                }
            }
        }

        internal void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write)
        {
            if (!IsServer || source == null || !source.HasAuthority || source.Manager != this ||
                target == null || target.NetworkObject == null || target.NetworkObject.Manager != this ||
                target.GetRpcDestination(method) != RpcDestination.Authority ||
                target.GetRpcPermission(method) != RpcInvokePermission.Server)
                throw new InvalidOperationException("Invalid server entity interaction");
            using (var payload = new NetWriter())
            {
                write(payload);
                byte[] bytes = payload.ToArray();
                if (target.HasAuthority)
                    target.ReceiveRpc(method, bytes, default, RpcDestination.Authority, default);
                else if (isWorker)
                {
                    using (var packet = new NetWriter())
                    {
                        packet.Write((byte)Packet.WorkerInteractionRpc);
                        packet.Write(source.EntityId);
                        packet.Write(source.AuthorityEpoch);
                        packet.Write(target.NetworkObject.EntityId);
                        packet.Write(target.BehaviourIndex);
                        packet.Write(method);
                        packet.WriteBytes(bytes);
                        transport.SendToServer(packet.ToArray());
                    }
                }
                else ForwardAuthorityRpc(target.NetworkObject.EntityId, target.BehaviourIndex, method, bytes, default);
            }
        }

        internal void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation)
        {
            if (isWorker)
            {
                using (var update = new NetWriter())
                {
                    update.Write((byte)Packet.WorkerTransform);
                    update.Write(component.NetworkObject.EntityId);
                    update.Write(component.NetworkObject.AuthorityEpoch);
                    update.Write(component.BehaviourIndex);
                    update.Write(flags);
                    if ((flags & 1) != 0) update.Write(position);
                    if ((flags & 2) != 0) update.Write(rotation);
                    transport.SendToServer(update.ToArray());
                }
                return;
            }
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Transform);
                writer.Write(component.NetworkObject.EntityId);
                writer.Write(component.BehaviourIndex);
                writer.Write(flags);
                if ((flags & 1) != 0) writer.Write(position);
                if ((flags & 2) != 0) writer.Write(rotation);
                byte[] data = writer.ToArray();
                if (IsServer)
                {
                    if (component.IsOwner &&
                        (((flags & 1) != 0 && component.PositionWriter == TransformWriter.Owner) ||
                         ((flags & 2) != 0 && component.RotationWriter == TransformWriter.Owner)))
                        RememberOwnerUpdate(component.NetworkObject.EntityId, data);
                    SendToObservers(component.NetworkObject.EntityId, data);
                    SendToWorkers(component.NetworkObject.EntityId, data);
                }
                else transport.SendToServer(data);
            }
        }

        internal void SendAnimator(NetworkAnimator component, byte[] payload)
        {
            if (component.Writer == AnimatorWriter.Owner ? !component.IsOwner : !component.HasAuthority)
                throw new InvalidOperationException("Only the configured Animator writer can send animation state");
            if (isWorker)
            {
                SendWorkerAnimator(component, payload);
                return;
            }
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Animator);
                writer.Write(component.NetworkObject.EntityId);
                writer.Write(component.BehaviourIndex);
                writer.WriteBytes(payload);
                byte[] data = writer.ToArray();
                if (IsServer)
                {
                    if (component.Writer == AnimatorWriter.Owner && component.IsOwner)
                        RememberOwnerUpdate(component.NetworkObject.EntityId, data);
                    SendToObservers(component.NetworkObject.EntityId, data);
                    SendToWorkers(component.NetworkObject.EntityId, data);
                }
                else transport.SendToServer(data);
            }
        }

        private void OnReceived(SessionId sender, byte[] bytes)
        {
            try
            {
                using (var reader = new NetReader(bytes))
                {
                    switch ((Packet)reader.ReadByte())
                    {
                        case Packet.ClientHello:
                            if (IsServer && !isWorker) { ValidateHello(reader); AcceptClient(sender); }
                            break;
                        case Packet.WorkerHello:
                            if (IsServer && !isWorker) { ValidateHello(reader); AcceptWorker(sender); }
                            break;
                        case Packet.WorkerWelcome:
                            if (isWorker) localWorkerId = reader.ReadULong();
                            break;
                        case Packet.WorkerSpawn:
                            if (isWorker) ReceiveWorkerSpawn(reader);
                            break;
                        case Packet.WorkerPrepare:
                            if (isWorker) ReceiveWorkerPrepare(reader);
                            break;
                        case Packet.WorkerPrepared:
                            if (!isWorker) ReceiveWorkerPrepared(sender, reader);
                            break;
                        case Packet.WorkerCommit:
                            if (isWorker) ReceiveWorkerCommit(reader);
                            break;
                        case Packet.WorkerTransform:
                            if (!isWorker) ReceiveWorkerTransform(sender, reader);
                            break;
                        case Packet.WorkerState:
                            if (!isWorker) ReceiveWorkerState(sender, reader);
                            break;
                        case Packet.WorkerAuthorityRpc:
                            if (isWorker) ReceiveWorkerAuthorityRpc(reader);
                            break;
                        case Packet.WorkerVariable:
                            if (!isWorker) ReceiveWorkerVariable(sender, reader);
                            break;
                        case Packet.WorkerOutboundRpc:
                            if (!isWorker) ReceiveWorkerOutboundRpc(sender, reader);
                            break;
                        case Packet.AuthorityChange:
                            if (!IsServer) ReceiveAuthorityChange(reader);
                            break;
                        case Packet.WorkerExportRequest:
                            if (isWorker) ReceiveWorkerExportRequest(reader);
                            break;
                        case Packet.WorkerExport:
                            if (!isWorker) ReceiveWorkerExport(sender, reader);
                            break;
                        case Packet.WorkerAbort:
                            if (isWorker) ReceiveWorkerAbort(reader);
                            break;
                        case Packet.WorkerAnimator:
                            if (!isWorker) ReceiveWorkerAnimator(sender, reader);
                            break;
                        case Packet.WorkerRegion:
                            if (isWorker) ReceiveWorkerRegion(reader);
                            break;
                        case Packet.WorkerRegionRequest:
                            if (IsServer && !isWorker) ReceiveWorkerRegionRequest(sender, reader);
                            break;
                        case Packet.ClientDebugRegionRequest:
                            if (IsServer && !isWorker) ReceiveClientDebugRegionRequest(sender, reader);
                            break;
                        case Packet.ClientDebugRegion:
                            if (!IsServer) ReceiveClientDebugRegion(reader);
                            break;
                        case Packet.WorkerSpawnRequest:
                            if (IsServer && !isWorker) ReceiveWorkerSpawnRequest(sender, reader);
                            break;
                        case Packet.WorkerDespawnRequest:
                            if (IsServer && !isWorker) ReceiveWorkerDespawnRequest(sender, reader);
                            break;
                        case Packet.WorkerInteractionRpc:
                            if (IsServer && !isWorker) ReceiveWorkerInteractionRpc(sender, reader);
                            break;
                        case Packet.Welcome:
                            if (IsServer) break;
                            LocalSession = reader.ReadSessionId();
                            SessionJoined?.Invoke(LocalSession);
                            break;
                        case Packet.Spawn:
                            if (!IsServer) ReceiveSpawn(reader);
                            break;
                        case Packet.Despawn:
                        case Packet.Hide:
                            if (!IsServer || isWorker) RemoveLocal(reader.ReadEntityId());
                            break;
                        case Packet.Variable:
                            ReceiveVariable(sender, reader);
                            break;
                        case Packet.Rpc:
                            ReceiveRpc(sender, reader);
                            break;
                        case Packet.Transform:
                            ReceiveTransform(sender, reader);
                            break;
                        case Packet.Animator:
                            ReceiveAnimator(sender, reader);
                            break;
                        default:
                            throw new InvalidOperationException("Unknown AtlasNet packet type");
                    }
                }
            }
            catch (Exception error)
            {
                Debug.LogError($"AtlasNet rejected packet: {error}");
            }
        }

        private void ReceiveSpawn(NetReader reader)
            => ReceiveReplicaSpawn(reader, "Client");

        // Both local client and worker packets feed the same replica lifecycle.
        // A future backend adapter can supply this information without exposing
        // its ingress or node-to-node packet format to NetworkObject.
        private void ReceiveReplicaSpawn(NetReader reader, string recipient)
        {
            EntityId id = reader.ReadEntityId();
            string prefabId = reader.ReadString();
            SessionId owner = reader.ReadSessionId();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            ulong authorityWorker = reader.ReadULong();
            uint authorityEpoch = reader.ReadUInt();
            if (spawned.ContainsKey(id)) throw new InvalidOperationException($"Duplicate {recipient.ToLowerInvariant()} entity {id}");
            if (!registry.TryGetValue(prefabId, out var prefab))
                throw new InvalidOperationException($"{recipient} lacks registered prefab '{prefabId}' for entity {id}. Register the same prefab ID on every instance.");
            var obj = Instantiate(prefab, position, rotation);
            try
            {
                obj.Initialize(this, id, owner, deferSpawnCallbacks: true);
                obj.SetSimulationAuthority(authorityWorker, authorityEpoch);
                obj.ReadSnapshot(reader);
                obj.CompleteSpawn();
                spawned.Add(id, obj);
            }
            catch
            {
                Destroy(obj.gameObject);
                throw;
            }
        }

        private void RemoveLocal(EntityId id)
        {
            if (!spawned.TryGetValue(id, out var obj)) return;
            spawned.Remove(id);
            obj.Shutdown();
            obj.gameObject.SetActive(false);
            Destroy(obj.gameObject);
        }

        private NetworkBehaviour FindBehaviour(EntityId id, byte index)
        {
            if (!spawned.TryGetValue(id, out var obj)) return null;
            if (index >= obj.Behaviours.Length) throw new InvalidOperationException($"Behaviour index {index} missing on entity {id}");
            return obj.Behaviours[index];
        }

        private void ReceiveVariable(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            ushort variable = reader.ReadUShort();
            byte[] payload = reader.ReadBytes();
            if (IsServer && !isWorker)
            {
                if (!clientSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                    sender != entity.Owner ||
                    !Schema(id, index).Variables.TryGetValue(variable, out var permission) ||
                    permission.Write != NetworkVariableWritePermission.Owner)
                    throw new InvalidOperationException($"Unauthorized variable update for entity {id} from session {sender}");
                FindBehaviour(id, index)?.ReadVariable(variable, payload);
                byte[] update = MakeVariablePacket(id, index, variable, payload);
                RememberOwnerUpdate(id, update);
                SendToWorkers(id, update);
                foreach (var session in clientSessions)
                    if (session != sender && IsObserver(id, session) &&
                        (permission.Read == NetworkVariableReadPermission.Everyone || session == entity.Owner))
                        transport.SendTo(session, update);
                return;
            }
            var behaviour = FindBehaviour(id, index);
            if (behaviour == null) return;
            var state = behaviour.GetVariable(variable);
            if (isWorker)
            {
                behaviour.ReadVariable(variable, payload);
            }
            else
            {
                if (state.ReadPermission == NetworkVariableReadPermission.Owner && !behaviour.IsOwner)
                    throw new InvalidOperationException($"Private variable {variable} sent to non-owner for entity {id}");
                behaviour.ReadVariable(variable, payload);
            }
        }

        private static byte[] MakeVariablePacket(EntityId id, byte index, ushort variable, byte[] payload)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Variable);
                writer.Write(id);
                writer.Write(index);
                writer.Write(variable);
                writer.WriteBytes(payload);
                return writer.ToArray();
            }
        }

        private void ReceiveRpc(SessionId sender, NetReader reader)
        {
            RpcDestination destination = (RpcDestination)reader.ReadByte();
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            SessionId target = reader.ReadSessionId();
            uint method = reader.ReadUInt();
            byte[] payload = reader.ReadBytes();
            if (IsServer && !isWorker)
            {
                if (!clientSessions.Contains(sender) || !directory.TryGetValue(id, out var entity))
                    throw new InvalidOperationException($"Unknown RPC sender or entity {id}");
                var rpc = RpcSchemaFor(Schema(id, index), method);
                bool ownerCall = entity.Owner == sender;
                bool allowed = (destination == RpcDestination.Authority && ownerCall &&
                     rpc.Permission != RpcInvokePermission.Server) ||
                    (destination == RpcDestination.Everyone && IsObserver(id, sender) &&
                     (rpc.Permission == RpcInvokePermission.Everyone ||
                      (rpc.Permission == RpcInvokePermission.Owner && ownerCall)));
                if (!allowed || rpc.Destination != destination)
                    throw new InvalidOperationException($"Unauthorized RPC {method} for entity {id} from session {sender}");
                if (destination == RpcDestination.Authority &&
                    (entity.Worker != 0 || pendingHandoffs.ContainsKey(id)))
                    ForwardAuthorityRpc(id, index, method, payload, sender);
                else FindBehaviour(id, index)?.ReceiveRpc(method, payload, sender, destination, target);
                if (destination == RpcDestination.Everyone)
                {
                    using (var writer = new NetWriter())
                    {
                        writer.Write((byte)Packet.Rpc);
                        writer.Write((byte)destination);
                        writer.Write(id);
                        writer.Write(index);
                        writer.Write(target);
                        writer.Write(method);
                        writer.WriteBytes(payload);
                        SendToObservers(id, writer.ToArray(), sender);
                        SendToWorkers(id, writer.ToArray());
                    }
                }
                return;
            }
            var behaviour = FindBehaviour(id, index);
            if (behaviour == null) return;
            if (isWorker)
            {
                if (destination == RpcDestination.Observers || destination == RpcDestination.Everyone ||
                    (destination == RpcDestination.Target && target == LocalSession))
                    behaviour.ReceiveRpc(method, payload, new SessionId(0), destination, target);
            }
            else if (destination == RpcDestination.Observers || destination == RpcDestination.Everyone ||
                (destination == RpcDestination.Target && target == LocalSession))
                behaviour.ReceiveRpc(method, payload, new SessionId(0), destination, target);
        }

        private void ReceiveTransform(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            byte flags = reader.ReadByte();
            Vector3 position = (flags & 1) != 0 ? reader.ReadVector3() : default;
            Quaternion rotation = (flags & 2) != 0 ? reader.ReadQuaternion() : default;
            if (IsServer && !isWorker)
            {
                if (!clientSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                    entity.Owner != sender || flags == 0 || (flags & ~3) != 0)
                    throw new InvalidOperationException($"Unauthorized transform update for entity {id} from session {sender}");
                var schema = Schema(id, index);
                if (!schema.IsTransform ||
                    ((flags & 1) != 0 && (!schema.SyncPosition || schema.PositionWriter != TransformWriter.Owner)) ||
                    ((flags & 2) != 0 && (!schema.SyncRotation || schema.RotationWriter != TransformWriter.Owner)))
                    throw new InvalidOperationException($"Unauthorized transform channel for entity {id}");
                if (FindBehaviour(id, index) is NetworkTransform local)
                    local.AcceptOwnerState(sender, flags, position, rotation);
                using (var writer = new NetWriter())
                {
                    writer.Write((byte)Packet.Transform);
                    writer.Write(id);
                    writer.Write(index);
                    writer.Write(flags);
                    if ((flags & 1) != 0) writer.Write(position);
                    if ((flags & 2) != 0) writer.Write(rotation);
                    byte[] update = writer.ToArray();
                    RememberOwnerUpdate(id, update);
                    SendToObservers(id, update);
                    SendToWorkers(id, update);
                }
                return;
            }
            var transform = FindBehaviour(id, index) as NetworkTransform;
            if (transform == null) return;
            if (isWorker)
            {
                transform.AcceptServerState(flags, position, rotation);
            }
            else transform.AcceptServerState(flags, position, rotation);
        }

        private void ReceiveAnimator(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            byte[] payload = reader.ReadBytes();
            if (IsServer && !isWorker)
            {
                if (!clientSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                    entity.Owner != sender || !Schema(id, index).IsAnimator ||
                    Schema(id, index).AnimatorWriter != AnimatorWriter.Owner)
                    throw new InvalidOperationException($"Unauthorized Animator update for entity {id} from session {sender}");
                if (FindBehaviour(id, index) is NetworkAnimator local) local.AcceptOwnerState(sender, payload);
                using (var writer = new NetWriter())
                {
                    writer.Write((byte)Packet.Animator);
                    writer.Write(id);
                    writer.Write(index);
                    writer.WriteBytes(payload);
                    byte[] update = writer.ToArray();
                    RememberOwnerUpdate(id, update);
                    SendToObservers(id, update, sender);
                    SendToWorkers(id, update);
                }
                return;
            }
            var component = FindBehaviour(id, index) as NetworkAnimator;
            if (component == null) return;
            if (isWorker)
            {
                component.AcceptServerState(payload);
            }
            else component.AcceptServerState(payload);
        }
    }
}
