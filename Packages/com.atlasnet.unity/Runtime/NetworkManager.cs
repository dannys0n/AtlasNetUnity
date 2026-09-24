using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace AtlasNet
{
    [Serializable]
    public struct NetworkPrefab
    {
        public string id;
        public NetworkObject prefab;
    }

    /// <summary>Single-server local runtime. The public API has no worker or socket addresses.</summary>
    public sealed class NetworkManager : MonoBehaviour
    {
        private enum Packet : byte { Welcome = 1, Spawn = 2, Despawn = 3, Variable = 4, Rpc = 5, Transform = 6, Hide = 7 }
        [SerializeField] private NetworkPrefab[] prefabs;
        [SerializeField, Range(10, 120)] private int tickRate = 30;
        [SerializeField] private int port = 7777;
        [SerializeField] private string localAddress = "127.0.0.1";

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
        public bool IsRunning => transport != null;
        public SessionId LocalSession { get; private set; }
        public uint Tick { get; private set; }
        public int TickRate => tickRate;
        public int SpawnedCount => spawned.Count;
        public IEnumerable<NetworkObject> SpawnedObjects => spawned.Values;
        public int RemoteClientCount => transport?.PeerCount ?? 0;
        public int ObserverCopies
        {
            get
            {
                if (!IsServer || transport == null) return 0;
                int total = 0;
                foreach (var id in spawned.Keys)
                    foreach (var session in transport.Sessions)
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
            if (prefabs == null) return;
            foreach (var entry in prefabs)
            {
                if (string.IsNullOrWhiteSpace(entry.id) || entry.prefab == null)
                    throw new InvalidOperationException("AtlasNet prefab registration needs an ID and NetworkObject prefab");
                if (entry.prefab.PrefabId != entry.id)
                    throw new InvalidOperationException($"Registry ID '{entry.id}' does not match prefab ID '{entry.prefab.PrefabId}'");
                if (registry.ContainsKey(entry.id))
                    throw new InvalidOperationException($"Duplicate AtlasNet prefab ID '{entry.id}'");
                registry.Add(entry.id, entry.prefab);
            }
        }

        public void StartServer() => StartLocal(true, false);
        public void StartHost() => StartLocal(true, true);
        public void StartClient() => StartLocal(false, true);

        private void StartLocal(bool server, bool client)
        {
            if (IsRunning) throw new InvalidOperationException("NetworkManager is already running");
            ValidateRegistry();
            transport = new LocalTcpTransport(server, localAddress, port);
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;
            IsServer = server;
            IsClient = client;
            LocalSession = server && client ? new SessionId(1) : new SessionId(0);
            Tick = 0;
            accumulator = 0;
            if (LocalSession.Value != 0) SessionJoined?.Invoke(LocalSession);
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
            hidden.Clear();
            IsServer = false;
            IsClient = false;
            LocalSession = new SessionId(0);
        }

        private void OnDestroy() => Stop();

        private void Update()
        {
            if (!IsRunning) return;
            transport.Poll();
            accumulator += Time.deltaTime;
            float interval = 1f / tickRate;
            while (accumulator >= interval)
            {
                accumulator -= interval;
                long before = Stopwatch.GetTimestamp();
                long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                Tick++;
                tickObjects.Clear();
                tickObjects.AddRange(spawned.Values);
                foreach (var obj in tickObjects)
                    if (obj != null && obj.IsSpawned)
                        foreach (var behaviour in obj.Behaviours) behaviour.OnNetworkTick();
                LastTickMilliseconds = (Stopwatch.GetTimestamp() - before) * 1000.0 / Stopwatch.Frequency;
                AllocatedBytesLastTick = allocationCounterSupported
                    ? GC.GetAllocatedBytesForCurrentThread() - allocatedBefore : -1;
                BytesSentLastTick = transport.BytesSent - lastBytes;
                lastBytes = transport.BytesSent;
            }
        }

        public NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can canonically spawn an object");
            if (!registry.TryGetValue(prefabId, out var prefab))
                throw new InvalidOperationException($"Prefab ID '{prefabId}' is not registered on NetworkManager");
            var obj = Instantiate(prefab, position, rotation);
            var id = new EntityId(nextEntity++);
            obj.Initialize(this, id, owner);
            spawned.Add(id, obj);
            SendToObservers(id, MakeSpawn(obj));
            return obj;
        }

        public void Despawn(NetworkObject obj)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server can canonically despawn an object");
            if (obj == null || obj.Manager != this || !spawned.ContainsKey(obj.EntityId))
                throw new InvalidOperationException("Object is not spawned by this manager");
            EntityId id = obj.EntityId;
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Despawn);
                writer.Write(id);
                SendToObservers(id, writer.ToArray());
            }
            spawned.Remove(id);
            hidden.Remove(id);
            obj.Shutdown();
            Destroy(obj.gameObject);
        }

        /// <summary>Remove one client's observer copy; the server's canonical entity remains alive.</summary>
        public void HideFrom(NetworkObject obj, SessionId session)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server changes observers");
            if (IsClient && session == LocalSession)
                throw new InvalidOperationException("A host shares its canonical object with the local view; host observer hiding is unsupported");
            if (obj.Manager != this) throw new InvalidOperationException("Object is not spawned here");
            if (!hidden.TryGetValue(obj.EntityId, out var excluded))
                hidden[obj.EntityId] = excluded = new HashSet<SessionId>();
            if (!excluded.Add(session)) return;
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Hide);
                writer.Write(obj.EntityId);
                transport.SendTo(session, writer.ToArray());
            }
        }

        public void ShowTo(NetworkObject obj, SessionId session)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server changes observers");
            if (obj.Manager != this) throw new InvalidOperationException("Object is not spawned here");
            if (hidden.TryGetValue(obj.EntityId, out var excluded) && excluded.Remove(session))
                transport.SendTo(session, MakeSpawn(obj));
        }

        public bool TryGet(EntityId id, out NetworkObject obj) => spawned.TryGetValue(id, out obj);
        internal bool CanSimulate(NetworkObject obj) => IsServer && obj != null && obj.Manager == this;

        private bool IsObserver(EntityId id, SessionId session) =>
            !hidden.TryGetValue(id, out var excluded) || !excluded.Contains(session);

        private void SendToObservers(EntityId id, byte[] packet)
        {
            foreach (var session in transport.Sessions)
                if (IsObserver(id, session)) transport.SendTo(session, packet);
        }

        private byte[] MakeSpawn(NetworkObject obj)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Spawn);
                writer.Write(obj.EntityId);
                writer.Write(obj.PrefabId);
                writer.Write(obj.OwnerSession);
                writer.Write(obj.transform.position);
                writer.Write(obj.transform.rotation);
                obj.WriteSnapshot(writer);
                return writer.ToArray();
            }
        }

        private void OnConnected(SessionId session)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Welcome);
                writer.Write(session);
                transport.SendTo(session, writer.ToArray());
            }
            foreach (var obj in spawned.Values)
                if (IsObserver(obj.EntityId, session)) transport.SendTo(session, MakeSpawn(obj));
            SessionJoined?.Invoke(session);
        }

        private void OnDisconnected(SessionId session)
        {
            if (IsServer)
            {
                SessionLeft?.Invoke(session);
                foreach (var obj in new List<NetworkObject>(spawned.Values))
                    if (obj.OwnerSession == session) Despawn(obj);
            }
            else
            {
                Debug.LogWarning("AtlasNet local server disconnected");
                Stop();
            }
        }

        internal void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value)
        {
            if (!IsServer) throw new InvalidOperationException("Only the server writes a NetworkVariable");
            using (var writer = new NetWriter())
            using (var content = new NetWriter())
            {
                value.Write(content);
                writer.Write((byte)Packet.Variable);
                writer.Write(behaviour.NetworkObject.EntityId);
                writer.Write(behaviour.BehaviourIndex);
                writer.Write(id);
                writer.WriteBytes(content.ToArray());
                SendToObservers(behaviour.NetworkObject.EntityId, writer.ToArray());
            }
        }

        internal void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, ushort method, Action<NetWriter> write)
        {
            var obj = behaviour.NetworkObject;
            if (destination == RpcDestination.Authority && !obj.IsOwner)
                throw new InvalidOperationException("Only the controlling session can send an AuthorityRpc");
            if (destination != RpcDestination.Authority && !IsServer)
                throw new InvalidOperationException("Only the server can send ObserversRpc or TargetRpc");
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
                if (destination == RpcDestination.Authority)
                {
                    if (IsServer) behaviour.ReceiveRpc(method, bytes, LocalSession);
                    else transport.SendToServer(data);
                }
                else if (destination == RpcDestination.Observers)
                {
                    if (IsClient) behaviour.ReceiveRpc(method, bytes, LocalSession);
                    SendToObservers(obj.EntityId, data);
                }
                else
                {
                    if (IsClient && target == LocalSession) behaviour.ReceiveRpc(method, bytes, LocalSession);
                    else if (IsObserver(obj.EntityId, target)) transport.SendTo(target, data);
                }
            }
        }

        internal void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Transform);
                writer.Write(component.NetworkObject.EntityId);
                writer.Write(component.BehaviourIndex);
                writer.Write(flags);
                if ((flags & 1) != 0) writer.Write(position);
                if ((flags & 2) != 0) writer.Write(rotation);
                byte[] data = writer.ToArray();
                if (IsServer) SendToObservers(component.NetworkObject.EntityId, data);
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
                            if (!IsServer) RemoveLocal(reader.ReadEntityId());
                            break;
                        case Packet.Variable:
                            if (!IsServer) ReceiveVariable(reader);
                            break;
                        case Packet.Rpc:
                            ReceiveRpc(sender, reader);
                            break;
                        case Packet.Transform:
                            ReceiveTransform(sender, reader);
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
        {
            EntityId id = reader.ReadEntityId();
            string prefabId = reader.ReadString();
            SessionId owner = reader.ReadSessionId();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            if (spawned.ContainsKey(id)) throw new InvalidOperationException($"Duplicate entity {id}");
            if (!registry.TryGetValue(prefabId, out var prefab))
                throw new InvalidOperationException($"Server spawned unregistered prefab '{prefabId}'. Register the same ID on every client.");
            var obj = Instantiate(prefab, position, rotation);
            try
            {
                obj.Initialize(this, id, owner);
                obj.ReadSnapshot(reader);
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
            Destroy(obj.gameObject);
        }

        private NetworkBehaviour FindBehaviour(EntityId id, byte index)
        {
            if (!spawned.TryGetValue(id, out var obj)) return null;
            if (index >= obj.Behaviours.Length) throw new InvalidOperationException($"Behaviour index {index} missing on entity {id}");
            return obj.Behaviours[index];
        }

        private void ReceiveVariable(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            ushort variable = reader.ReadUShort();
            byte[] payload = reader.ReadBytes();
            FindBehaviour(id, index)?.ReadVariable(variable, payload);
        }

        private void ReceiveRpc(SessionId sender, NetReader reader)
        {
            RpcDestination destination = (RpcDestination)reader.ReadByte();
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            SessionId target = reader.ReadSessionId();
            ushort method = reader.ReadUShort();
            byte[] payload = reader.ReadBytes();
            var behaviour = FindBehaviour(id, index);
            if (behaviour == null) return;
            if (IsServer)
            {
                if (destination != RpcDestination.Authority || behaviour.OwnerSession != sender)
                    throw new InvalidOperationException($"Unauthorized RPC {method} for entity {id} from session {sender}");
                behaviour.ReceiveRpc(method, payload, sender);
            }
            else if (destination == RpcDestination.Observers || (destination == RpcDestination.Target && target == LocalSession))
                behaviour.ReceiveRpc(method, payload, new SessionId(0));
        }

        private void ReceiveTransform(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            byte index = reader.ReadByte();
            byte flags = reader.ReadByte();
            Vector3 position = (flags & 1) != 0 ? reader.ReadVector3() : default;
            Quaternion rotation = (flags & 2) != 0 ? reader.ReadQuaternion() : default;
            var transform = FindBehaviour(id, index) as NetworkTransform;
            if (transform == null) return;
            if (IsServer)
            {
                if (!transform.AcceptOwnerState(sender, flags, position, rotation))
                    throw new InvalidOperationException($"Unauthorized transform update for entity {id} from session {sender}");
                using (var writer = new NetWriter())
                {
                    writer.Write((byte)Packet.Transform);
                    writer.Write(id);
                    writer.Write(index);
                    writer.Write(flags);
                    if ((flags & 1) != 0) writer.Write(position);
                    if ((flags & 2) != 0) writer.Write(rotation);
                    SendToObservers(id, writer.ToArray());
                }
            }
            else transform.AcceptServerState(flags, position, rotation);
        }
    }
}
