using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace AtlasNet
{
    // Local development topology: the first server is the coordinator and client ingress.
    // Additional Unity servers connect to it as workers, never directly to each other.
    public sealed partial class NetworkManager
    {
        private struct PendingHandoff
        {
            public SessionId Source;
            public SessionId Destination;
            public uint Epoch;
            public uint StartedAtTick;
            public List<byte[]> OwnerUpdates;
        }

        // Coordinator directory data is deliberately not a Unity replica. Only `spawned`
        // contains GameObjects which this process currently simulates or observes.
        private sealed class CanonicalEntity
        {
            public EntityId Id;
            public string PrefabId;
            public SessionId Owner;
            public ulong Worker;
            public uint Epoch;
            public Vector3 Position;
            public Quaternion Rotation;
            public byte[] PublicSnapshot;
            public byte[] OwnerSnapshot;
            public bool HasInterestSource;
            public bool InterestSource;
            public float InterestRadius;
            public float ExitPadding;
            public bool CanHandoff;
            public BehaviourSchema[] Behaviours;

            public void Capture(NetworkObject obj)
            {
                Position = obj.transform.position;
                Rotation = obj.transform.rotation;
                using (var publicState = new NetWriter())
                using (var ownerState = new NetWriter())
                {
                    // Session zero can mean an unowned entity; use a non-client
                    // sentinel so public snapshots never include owner-only values.
                    obj.WriteSnapshot(publicState, new SessionId(ulong.MaxValue));
                    obj.WriteSnapshot(ownerState, Owner);
                    PublicSnapshot = publicState.ToArray();
                    OwnerSnapshot = ownerState.ToArray();
                }
            }
        }

        private sealed class BehaviourSchema
        {
            public readonly Dictionary<ushort, (NetworkVariableReadPermission Read, NetworkVariableWritePermission Write)> Variables =
                new Dictionary<ushort, (NetworkVariableReadPermission, NetworkVariableWritePermission)>();
            public readonly Dictionary<uint, (RpcDestination Destination, RpcInvokePermission Permission)> Rpcs =
                new Dictionary<uint, (RpcDestination, RpcInvokePermission)>();
            public bool IsTransform;
            public bool SyncPosition;
            public bool SyncRotation;
            public TransformWriter PositionWriter;
            public TransformWriter RotationWriter;
            public bool IsAnimator;
            public AnimatorWriter AnimatorWriter;
        }

        private struct QueuedAuthorityRpc
        {
            public byte Behaviour;
            public uint Method;
            public byte[] Payload;
            public SessionId Sender;
        }

        private readonly HashSet<SessionId> workerSessions = new HashSet<SessionId>();
        private readonly Dictionary<EntityId, CanonicalEntity> directory = new Dictionary<EntityId, CanonicalEntity>();
        private readonly Dictionary<SessionId, HashSet<EntityId>> workerResidents =
            new Dictionary<SessionId, HashSet<EntityId>>();
        private readonly HashSet<SessionId> clientSessions = new HashSet<SessionId>();
        // Present only for clients whose player prefab opts into radius interest.
        private readonly Dictionary<SessionId, HashSet<EntityId>> clientResidents =
            new Dictionary<SessionId, HashSet<EntityId>>();
        private readonly Dictionary<EntityId, PendingHandoff> pendingHandoffs = new Dictionary<EntityId, PendingHandoff>();
        private readonly Dictionary<EntityId, uint> lastHandoffTick = new Dictionary<EntityId, uint>();
        private readonly Dictionary<EntityId, List<QueuedAuthorityRpc>> queuedAuthorityRpcs =
            new Dictionary<EntityId, List<QueuedAuthorityRpc>>();
        private ILocalAuthorityPlacement localWorld;
        private bool isWorker;
        private ulong localWorkerId;
        private Vector2[] localRegion = Array.Empty<Vector2>();
        private int localRegionVersion;
        private bool debugRegionRequested;
        private readonly HashSet<SessionId> debugRegionSubscribers = new HashSet<SessionId>();
        private readonly Dictionary<SessionId, EntityId> clientDebugRegionSubscribers =
            new Dictionary<SessionId, EntityId>();
        private Vector2[] clientDebugRegion = Array.Empty<Vector2>();
        private EntityId clientDebugRegionEntity;
        private ulong clientDebugRegionWorker;
        private uint clientDebugRegionEpoch;
        private int clientDebugRegionVersion;

        private CanonicalEntity RegisterCanonical(NetworkObject obj)
        {
            var source = obj.GetComponent<NetworkInterestSource>();
            var movement = obj.GetComponent<NetworkTransform>();
            var record = new CanonicalEntity
            {
                Id = obj.EntityId, PrefabId = obj.PrefabId, Owner = obj.OwnerSession,
                Worker = obj.SimulationWorker, Epoch = obj.AuthorityEpoch,
                HasInterestSource = source != null,
                InterestSource = source != null && source.isActiveAndEnabled,
                InterestRadius = source != null ? source.Radius : 0f,
                ExitPadding = source != null ? source.ExitPadding : 0f,
                CanHandoff = movement != null && movement.SyncPosition && movement.Target == obj.transform &&
                    (movement.PositionWriter != TransformWriter.Owner || obj.OwnerSession.Value != 0),
                Behaviours = new BehaviourSchema[obj.Behaviours.Length]
            };
            for (int i = 0; i < record.Behaviours.Length; i++)
            {
                var behaviour = obj.Behaviours[i];
                var schema = new BehaviourSchema();
                foreach (var pair in behaviour.VariableSchema)
                    schema.Variables.Add(pair.Key, (pair.Value.ReadPermission, pair.Value.WritePermission));
                foreach (var pair in behaviour.RpcSchema)
                    schema.Rpcs.Add(pair.Key, (RpcMethods.DestinationOf(RpcMethods.TargetOf(pair.Value).Value),
                        pair.Value.GetCustomAttribute<RpcAttribute>().InvokePermission));
                if (behaviour is NetworkTransform transform)
                {
                    schema.IsTransform = true;
                    schema.SyncPosition = transform.SyncPosition;
                    schema.SyncRotation = transform.SyncRotation;
                    schema.PositionWriter = transform.PositionWriter;
                    schema.RotationWriter = transform.RotationWriter;
                }
                if (behaviour is NetworkAnimator animator)
                {
                    schema.IsAnimator = true;
                    schema.AnimatorWriter = animator.Writer;
                }
                record.Behaviours[i] = schema;
            }
            record.Capture(obj);
            directory.Add(record.Id, record);
            return record;
        }

        private BehaviourSchema Schema(EntityId id, byte index)
        {
            if (!directory.TryGetValue(id, out var record) || index >= record.Behaviours.Length)
                throw new InvalidOperationException($"Unknown behaviour {index} on entity {id}");
            return record.Behaviours[index];
        }

        private static (RpcDestination Destination, RpcInvokePermission Permission) RpcSchemaFor(BehaviourSchema schema, uint method)
        {
            if (!schema.Rpcs.TryGetValue(method, out var rpc))
                throw new InvalidOperationException($"Unknown RPC {method} on canonical entity");
            return rpc;
        }

        private bool ShouldClientHold(SessionId session, CanonicalEntity target, bool alreadyResident)
        {
            if (target.Owner == session) return true;
            foreach (var source in directory.Values)
            {
                if (source.Owner != session || !source.HasInterestSource) continue;
                if (target.Worker == source.Worker) return true;
                if (!source.InterestSource) continue;
                if (LocalWorldPolicy.WithinRadius(source.Position, target.Position,
                    source.InterestRadius + (alreadyResident ? source.ExitPadding : 0f))) return true;
            }
            return false;
        }

        private bool ShouldWorkerHold(SessionId worker, CanonicalEntity target, bool alreadyResident)
        {
            bool handoffPending = pendingHandoffs.TryGetValue(target.Id, out var pending);
            if (target.Worker == worker.Value ||
                (handoffPending && (pending.Source == worker || pending.Destination == worker))) return true;
            if (localWorld == null) return false;
            foreach (var source in directory.Values)
            {
                if (source.Id == target.Id || source.Owner.Value == 0 ||
                    source.Worker != worker.Value || !source.InterestSource) continue;
                float radius = source.InterestRadius + (alreadyResident ? source.ExitPadding : 0f);
                if (!localWorld.IsWithinInterest(target.Worker, source.Position.x, source.Position.z, radius) &&
                    !handoffPending && localWorld.OwnerAt(target.Position.x, target.Position.z) == target.Worker)
                    continue;
                if (LocalWorldPolicy.WithinRadius(source.Position, target.Position, radius)) return true;
            }
            return false;
        }

        public bool IsWorker => isWorker;
        public ulong LocalWorkerId => localWorkerId;
        public int WorkerCount => workerSessions.Count + (IsServer ? 1 : 0);
        public int PendingHandoffCount => pendingHandoffs.Count;
        /// <summary>Optional debug-only X/Z outline of this server's shard. Empty until requested or if unavailable.</summary>
        public IReadOnlyList<Vector2> LocalRegion => localRegion;
        public int LocalRegionVersion => localRegionVersion;
        /// <summary>Optional debug outline for the owned player's current worker on a joined client.</summary>
        public IReadOnlyList<Vector2> ClientDebugRegion => clientDebugRegion;
        public EntityId ClientDebugRegionEntity => clientDebugRegionEntity;
        public ulong ClientDebugRegionWorker => clientDebugRegionWorker;
        public uint ClientDebugRegionEpoch => clientDebugRegionEpoch;
        public int ClientDebugRegionVersion => clientDebugRegionVersion;
        /// <summary>Subscribe to the owned player's current region for local visualization only.</summary>
        public bool SetClientDebugRegionEnabled(NetworkObject ownedPlayer, bool enabled)
        {
            if (!IsRunning || !IsClient || IsServer) return false;
            if (enabled && (ownedPlayer == null || ownedPlayer.Manager != this || !ownedPlayer.IsOwner))
                return false;
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.ClientDebugRegionRequest);
                packet.Write(enabled);
                if (enabled) packet.Write(ownedPlayer.EntityId);
                transport.SendToServer(packet.ToArray());
            }
            if (!enabled) ClearClientDebugRegion();
            return true;
        }
        /// <summary>Request diagnostic region geometry; never use it to decide gameplay authority.</summary>
        public bool RequestLocalRegionForDebug()
        {
            if (!IsServer || !IsRunning) return false;
            if (debugRegionRequested) return true;
            if (isWorker)
            {
                if (localWorkerId == ulong.MaxValue) return false;
                using (var packet = new NetWriter())
                {
                    packet.Write((byte)Packet.WorkerRegionRequest);
                    transport.SendToServer(packet.ToArray());
                }
            }
            else UpdateCoordinatorDebugRegion();
            debugRegionRequested = true;
            return true;
        }
        public int LocalAuthorityCount
        {
            get
            {
                int count = 0;
                foreach (var obj in spawned.Values) if (obj != null && obj.HasAuthority) count++;
                return count;
            }
        }
        public int GhostCount => IsServer ? SpawnedCount - LocalAuthorityCount : 0;

        /// <summary>Sample diagnostics only; the coordinator directory is not a Unity replica.</summary>
        public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj)
        {
            if (!IsServer || obj == null || obj.Manager != this) return false;
            if (obj.HasAuthority) return true;
            if (isWorker) return true; // Workers only receive subscribed copies.
            return true;
        }

        /// <summary>Join the first local server as another Unity simulation worker.</summary>
        public void StartWorker()
        {
            if (IsRunning) throw new InvalidOperationException("NetworkManager is already running");
            ValidateRegistry();
            transport = new LocalTcpTransport(false, localAddress, port);
            transport.Connected += OnConnected;
            transport.Disconnected += OnDisconnected;
            transport.Received += OnReceived;
            isWorker = true;
            localWorkerId = ulong.MaxValue;
            IsServer = true;
            IsClient = false;
            LocalSession = default;
            Tick = 0;
            accumulator = 0;
            using (var hello = new NetWriter())
            {
                hello.Write((byte)Packet.WorkerHello);
                hello.Write(LocalProtocolVersion);
                transport.SendToServer(hello.ToArray());
            }
        }

        private void AcceptClient(SessionId session)
        {
            if (workerSessions.Contains(session) || !clientSessions.Add(session)) return;
            bool radiusInterest = playerPrefab != null &&
                playerPrefab.GetComponent<NetworkInterestSource>() != null;
            if (radiusInterest) clientResidents.Add(session, new HashSet<EntityId>());
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.Welcome);
                writer.Write(session);
                transport.SendTo(session, writer.ToArray());
            }
            if (radiusInterest)
            {
                // Establish the owned source first to identify its authoring worker.
                SpawnPlayer(session);
                RefreshClientInterest(session);
            }
            else
            {
                foreach (var entity in directory.Values)
                    if (IsObserver(entity.Id, session)) transport.SendTo(session, MakeSpawn(entity, session));
                SpawnPlayer(session);
            }
            SessionJoined?.Invoke(session);
        }

        private void RefreshClientInterest(SessionId session)
        {
            if (!clientResidents.TryGetValue(session, out var residents)) return;
            foreach (var entity in directory.Values)
            {
                EntityId id = entity.Id;
                bool excluded = hidden.TryGetValue(id, out var sessions) && sessions.Contains(session);
                bool wanted = !excluded && ShouldClientHold(session, entity, residents.Contains(id));
                if (wanted && residents.Add(id)) transport.SendTo(session, MakeSpawn(entity, session));
                else if (!wanted && residents.Remove(id))
                    transport.SendTo(session, MakeEntityPacket(Packet.Hide, id));
            }
        }

        private void RefreshClientInterest()
        {
            if (!IsServer || isWorker) return;
            foreach (var session in clientSessions) RefreshClientInterest(session);
        }

        private void AddSpawnToClientInterest(NetworkObject obj)
        {
            var entity = directory[obj.EntityId];
            foreach (var session in clientSessions)
            {
                if (!clientResidents.TryGetValue(session, out var residents))
                {
                    if (IsObserver(entity.Id, session)) transport.SendTo(session, MakeSpawn(entity, session));
                }
                else if (ShouldClientHold(session, entity, false) && IsExplicitlyVisible(entity.Id, session) &&
                         residents.Add(entity.Id))
                    transport.SendTo(session, MakeSpawn(entity, session));
            }
        }

        private static void ValidateHello(NetReader reader)
        {
            ushort version = reader.ReadUShort();
            if (version != LocalProtocolVersion || reader.HasRemaining)
                throw new InvalidOperationException($"AtlasNet local protocol mismatch: peer {version}, expected {LocalProtocolVersion}. Use the same package version in every instance.");
        }

        private void AcceptWorker(SessionId session)
        {
            if (clientSessions.Contains(session) || !workerSessions.Add(session)) return;
            workerResidents.Add(session, new HashSet<EntityId>());
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.WorkerWelcome);
                writer.Write(session.Value);
                transport.SendTo(session, writer.ToArray());
            }
            localWorld?.AddWorker(session.Value);
            RefreshWorkerInterest();
            BroadcastLocalRegions();
            Debug.Log($"AtlasNet local worker {session} joined with {workerResidents[session].Count} relevant entities");
        }

        private void EnsureWorkerResident(SessionId worker, CanonicalEntity entity)
        {
            if (workerResidents.TryGetValue(worker, out var residents) && residents.Add(entity.Id))
                transport.SendTo(worker, MakeWorkerSpawn(entity));
        }

        private void RefreshWorkerInterest()
        {
            if (!IsServer || isWorker || workerResidents.Count == 0) return;
            foreach (var pair in workerResidents)
            {
                SessionId worker = pair.Key;
                var residents = pair.Value;
                foreach (var entity in directory.Values)
                {
                    bool wanted = ShouldWorkerHold(worker, entity, residents.Contains(entity.Id));
                    if (wanted) EnsureWorkerResident(worker, entity);
                    else if (residents.Remove(entity.Id))
                        transport.SendTo(worker, MakeEntityPacket(Packet.Despawn, entity.Id));
                }
            }
        }

        private void AddSpawnToWorkerInterest(NetworkObject obj)
        {
            var entity = directory[obj.EntityId];
            foreach (var worker in workerSessions)
                if (ShouldWorkerHold(worker, entity, false)) EnsureWorkerResident(worker, entity);
        }

        private void RemoveFromWorkerInterest(EntityId id)
        {
            foreach (var pair in workerResidents)
                if (pair.Value.Remove(id))
                    transport.SendTo(pair.Key, MakeEntityPacket(Packet.Despawn, id));
        }

        private void SetLocalRegion(Vector2[] polygon)
        {
            localRegion = polygon;
            localRegionVersion++;
        }

        private void UpdateCoordinatorDebugRegion()
        {
            if (localWorld is IAuthorityRegionDebug regions && regions.TryGetRegion(0, out var polygon))
                SetLocalRegion(polygon);
        }

        private void BroadcastLocalRegions()
        {
            if (localWorld is not IAuthorityRegionDebug) return;
            if (debugRegionRequested) UpdateCoordinatorDebugRegion();
            foreach (var worker in debugRegionSubscribers) SendDebugRegion(worker);
            foreach (var pair in clientDebugRegionSubscribers)
                if (directory.TryGetValue(pair.Value, out var player)) SendClientDebugRegion(pair.Key, player);
        }

        private void SendClientDebugRegion(SessionId client, CanonicalEntity player)
        {
            if (!clientSessions.Contains(client) || player.Owner != client) return;
            Vector2[] polygon = Array.Empty<Vector2>();
            if (localWorld is IAuthorityRegionDebug regions)
                regions.TryGetRegion(player.Worker, out polygon);
            polygon ??= Array.Empty<Vector2>();
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.ClientDebugRegion);
                packet.Write(player.Id);
                packet.Write(player.Worker);
                packet.Write(player.Epoch);
                packet.Write(polygon.Length);
                foreach (var vertex in polygon)
                {
                    packet.Write(vertex.x);
                    packet.Write(vertex.y);
                }
                transport.SendTo(client, packet.ToArray());
            }
        }

        private void ReceiveClientDebugRegionRequest(SessionId sender, NetReader reader)
        {
            if (!clientSessions.Contains(sender)) throw new InvalidOperationException("Unknown client debug-region subscriber");
            bool enabled = reader.ReadBool();
            if (!enabled)
            {
                if (reader.HasRemaining) throw new InvalidOperationException("Extra client debug-region request data");
                clientDebugRegionSubscribers.Remove(sender);
                return;
            }
            EntityId id = reader.ReadEntityId();
            if (reader.HasRemaining || !directory.TryGetValue(id, out var player) || player.Owner != sender)
                throw new InvalidOperationException("Client may only request its owned entity's debug region");
            clientDebugRegionSubscribers[sender] = id;
            SendClientDebugRegion(sender, player);
        }

        private void ReceiveClientDebugRegion(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            ulong worker = reader.ReadULong();
            uint epoch = reader.ReadUInt();
            int count = reader.ReadInt();
            if (count < 0 || count > 1024) throw new InvalidOperationException("Invalid client debug-region outline");
            var polygon = new Vector2[count];
            for (int i = 0; i < count; i++) polygon[i] = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            if (reader.HasRemaining) throw new InvalidOperationException("Extra client debug-region data");
            if (!spawned.TryGetValue(id, out var player) || !player.IsOwner || epoch < player.AuthorityEpoch) return;
            clientDebugRegionEntity = id;
            clientDebugRegionWorker = worker;
            clientDebugRegionEpoch = epoch;
            clientDebugRegion = polygon;
            clientDebugRegionVersion++;
        }

        private void ClearClientDebugRegion()
        {
            clientDebugRegion = Array.Empty<Vector2>();
            clientDebugRegionEntity = default;
            clientDebugRegionWorker = 0;
            clientDebugRegionEpoch = 0;
            clientDebugRegionVersion++;
        }

        private void SendDebugRegion(SessionId worker)
        {
            if (localWorld is not IAuthorityRegionDebug regions ||
                !regions.TryGetRegion(worker.Value, out var polygon)) return;
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerRegion);
                packet.Write(polygon.Length);
                foreach (var vertex in polygon)
                {
                    packet.Write(vertex.x);
                    packet.Write(vertex.y);
                }
                transport.SendTo(worker, packet.ToArray());
            }
        }

        private void ReceiveWorkerRegionRequest(SessionId sender, NetReader reader)
        {
            if (!workerSessions.Contains(sender) || reader.HasRemaining)
                throw new InvalidOperationException("Invalid worker debug-region request");
            debugRegionSubscribers.Add(sender);
            SendDebugRegion(sender);
        }

        private void ReceiveWorkerRegion(NetReader reader)
        {
            int count = reader.ReadInt();
            if (count < 3 || count > 1024) throw new InvalidOperationException("Invalid local worker region outline");
            var polygon = new Vector2[count];
            for (int i = 0; i < count; i++)
                polygon[i] = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            if (reader.HasRemaining) throw new InvalidOperationException("Extra local worker region data");
            SetLocalRegion(polygon);
        }

        private byte[] MakeWorkerSpawn(CanonicalEntity entity)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.WorkerSpawn);
                WriteReplicaState(writer, entity, entity.Owner);
                return writer.ToArray();
            }
        }

        private void ReceiveWorkerSpawn(NetReader reader)
        {
            ReceiveReplicaSpawn(reader, "Worker");
        }

        private void EnsureCoordinatorResident(CanonicalEntity entity)
        {
            if (spawned.ContainsKey(entity.Id)) return;
            if (!registry.TryGetValue(entity.PrefabId, out var prefab))
                throw new InvalidOperationException($"Missing prefab {entity.PrefabId} for entity {entity.Id}");
            var obj = Instantiate(prefab, entity.Position, entity.Rotation);
            try
            {
                obj.Initialize(this, entity.Id, entity.Owner, deferSpawnCallbacks: true);
                obj.SetSimulationAuthority(entity.Worker, entity.Epoch);
                using (var state = new NetReader(entity.OwnerSnapshot)) obj.ReadSnapshot(state);
                obj.CompleteSpawn();
                spawned.Add(entity.Id, obj);
            }
            catch
            {
                Destroy(obj.gameObject);
                throw;
            }
        }

        private void RefreshCoordinatorResidency()
        {
            if (!IsServer || isWorker) return;
            foreach (var entity in directory.Values)
            {
                bool resident = spawned.ContainsKey(entity.Id);
                bool hostView = IsClient && (playerPrefab == null ||
                    playerPrefab.GetComponent<NetworkInterestSource>() == null ||
                    ShouldClientHold(LocalSession, entity, resident));
                bool wanted = entity.Worker == 0 || pendingHandoffs.ContainsKey(entity.Id) ||
                    ShouldWorkerHold(default, entity, resident) || hostView;
                if (wanted) EnsureCoordinatorResident(entity);
                else if (resident) RemoveLocal(entity.Id);
            }
        }

        // Development relay only: the authoritative worker supplies late-join state
        // to the coordinator's data directory without a permanent Unity ghost there.
        private void SendWorkerSnapshots()
        {
            if (!isWorker || localWorkerId == ulong.MaxValue) return;
            foreach (var obj in spawned.Values)
            {
                if (obj == null || !obj.HasAuthority) continue;
                using (var publicState = new NetWriter())
                using (var ownerState = new NetWriter())
                using (var packet = new NetWriter())
                {
                    obj.WriteSnapshot(publicState, new SessionId(ulong.MaxValue));
                    obj.WriteSnapshot(ownerState, obj.OwnerSession);
                    packet.Write((byte)Packet.WorkerState);
                    packet.Write(obj.EntityId);
                    packet.Write(obj.AuthorityEpoch);
                    packet.Write(obj.transform.position);
                    packet.Write(obj.transform.rotation);
                    packet.WriteBytes(publicState.ToArray());
                    packet.WriteBytes(ownerState.ToArray());
                    transport.SendToServer(packet.ToArray());
                }
            }
        }

        private void ReceiveWorkerState(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            byte[] publicState = reader.ReadBytes();
            byte[] ownerState = reader.ReadBytes();
            if (reader.HasRemaining || !workerSessions.Contains(sender) ||
                !directory.TryGetValue(id, out var entity) || entity.Worker != sender.Value ||
                entity.Epoch != epoch)
                throw new InvalidOperationException($"Stale or unauthorized worker state for entity {id}");
            entity.Position = position;
            entity.Rotation = rotation;
            entity.PublicSnapshot = publicState;
            entity.OwnerSnapshot = ownerState;
        }

        private void ReceiveWorkerSpawnRequest(SessionId sender, NetReader reader)
        {
            EntityId sourceId = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            string prefabId = reader.ReadString();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            SessionId owner = reader.ReadSessionId();
            if (reader.HasRemaining || !workerSessions.Contains(sender) ||
                !directory.TryGetValue(sourceId, out var source) ||
                source.Worker != sender.Value || source.Epoch != epoch ||
                pendingHandoffs.ContainsKey(sourceId))
                throw new InvalidOperationException("Stale or unauthorized worker spawn request");
            if (owner.Value != 0 && !clientSessions.Contains(owner) && (!IsClient || owner != LocalSession))
                throw new InvalidOperationException("Worker requested an unknown owner session");
            if (float.IsNaN(position.x) || float.IsInfinity(position.x) ||
                float.IsNaN(position.y) || float.IsInfinity(position.y) ||
                float.IsNaN(position.z) || float.IsInfinity(position.z) ||
                float.IsNaN(rotation.x) || float.IsInfinity(rotation.x) ||
                float.IsNaN(rotation.y) || float.IsInfinity(rotation.y) ||
                float.IsNaN(rotation.z) || float.IsInfinity(rotation.z) ||
                float.IsNaN(rotation.w) || float.IsInfinity(rotation.w))
                throw new InvalidOperationException("Invalid worker spawn pose");
            SpawnCanonical(prefabId, position, rotation, owner, sender.Value);
        }

        private void ReceiveWorkerDespawnRequest(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            if (reader.HasRemaining || !workerSessions.Contains(sender) ||
                !directory.TryGetValue(id, out var obj) ||
                obj.Worker != sender.Value || obj.Epoch != epoch ||
                pendingHandoffs.ContainsKey(id))
                throw new InvalidOperationException("Stale or unauthorized worker despawn request");
            DespawnCanonical(id);
        }

        private void ReceiveWorkerInteractionRpc(SessionId sender, NetReader reader)
        {
            EntityId sourceId = reader.ReadEntityId();
            uint sourceEpoch = reader.ReadUInt();
            EntityId targetId = reader.ReadEntityId();
            byte index = reader.ReadByte();
            uint method = reader.ReadUInt();
            byte[] payload = reader.ReadBytes();
            var targetRpc = RpcSchemaFor(Schema(targetId, index), method);
            if (reader.HasRemaining || !workerSessions.Contains(sender) ||
                !directory.TryGetValue(sourceId, out var source) ||
                source.Worker != sender.Value || source.Epoch != sourceEpoch ||
                pendingHandoffs.ContainsKey(sourceId) ||
                !workerResidents.TryGetValue(sender, out var residents) || !residents.Contains(targetId) ||
                targetRpc.Destination != RpcDestination.Authority ||
                targetRpc.Permission != RpcInvokePermission.Server)
                throw new InvalidOperationException("Stale or unauthorized worker entity interaction");
            if (spawned.TryGetValue(targetId, out var localTarget) && localTarget.HasAuthority)
                localTarget.Behaviours[index].ReceiveRpc(method, payload, default, RpcDestination.Authority, default);
            else ForwardAuthorityRpc(targetId, index, method, payload, default);
        }

        /// <summary>Called by local spatial orchestration; gameplay does not choose workers.</summary>
        internal void HandoffToWorker(NetworkObject obj, ulong destinationWorker)
        {
            if (!IsServer || isWorker) throw new InvalidOperationException("Only the first local server coordinates handoffs");
            if (obj == null || obj.Manager != this || !directory.ContainsKey(obj.EntityId))
                throw new InvalidOperationException("Object is not spawned on this coordinator");
            HandoffToWorker(directory[obj.EntityId], destinationWorker);
        }

        private void HandoffToWorker(CanonicalEntity entity, ulong destinationWorker)
        {
            var destination = new SessionId(destinationWorker);
            if (destinationWorker != 0 && !workerSessions.Contains(destination))
                throw new InvalidOperationException($"Worker {destinationWorker} is not connected");
            if (entity.Worker == destinationWorker)
                throw new InvalidOperationException($"Entity {entity.Id} is already simulated by worker {destinationWorker}");
            if (pendingHandoffs.ContainsKey(entity.Id)) throw new InvalidOperationException("Entity handoff is already pending");
            EnsureCoordinatorResident(entity); // Temporary state bridge for owner channels during transfer.
            var obj = spawned[entity.Id];
            if (destinationWorker != 0) EnsureWorkerResident(destination, entity);
            uint nextEpoch = checked(entity.Epoch + 1);
            var pending = new PendingHandoff
            {
                Source = new SessionId(entity.Worker), Destination = destination,
                Epoch = nextEpoch, StartedAtTick = Tick, OwnerUpdates = new List<byte[]>()
            };
            byte[] localState = null;
            if (entity.Worker == 0)
            {
                using (var state = new NetWriter())
                {
                    obj.WriteHandoff(state);
                    localState = state.ToArray();
                }
            }
            pendingHandoffs.Add(entity.Id, pending);
            if (entity.Worker == 0) obj.NotifySimulationAuthorityChanged();
            if (entity.Worker != 0)
            {
                using (var request = new NetWriter())
                {
                    request.Write((byte)Packet.WorkerExportRequest);
                    request.Write(entity.Id);
                    request.Write(entity.Epoch);
                    request.Write(nextEpoch);
                    request.Write(destinationWorker);
                    transport.SendTo(pending.Source, request.ToArray());
                }
                Debug.Log($"AtlasNet requested handoff state for {entity.Id} from worker {pending.Source}");
                return;
            }
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerPrepare);
                packet.Write(entity.Id);
                packet.Write(nextEpoch);
                packet.Write(Tick);
                packet.WriteBytes(localState);
                transport.SendTo(destination, packet.ToArray());
            }
            Debug.Log($"AtlasNet preparing handoff of {entity.Id} to worker {destinationWorker}, epoch {nextEpoch}");
        }

        private static byte[] CaptureOwnerState(NetworkObject obj)
        {
            using (var state = new NetWriter())
            {
                obj.WriteOwnerState(state);
                return state.ToArray();
            }
        }

        private void RememberOwnerUpdate(EntityId id, byte[] packet)
        {
            if (!pendingHandoffs.TryGetValue(id, out var pending)) return;
            if (pending.OwnerUpdates.Count >= 512)
                throw new InvalidOperationException($"Owner update queue is full during handoff of {id}");
            pending.OwnerUpdates.Add(packet);
        }

        private void ReplayOwnerUpdates(NetworkObject obj, List<byte[]> updates)
        {
            if (updates == null) return;
            foreach (var packet in updates)
            using (var reader = new NetReader(packet))
            {
                Packet type = (Packet)reader.ReadByte();
                EntityId id = reader.ReadEntityId();
                byte index = reader.ReadByte();
                if (id != obj.EntityId || index >= obj.Behaviours.Length)
                    throw new InvalidOperationException($"Invalid queued owner state for {obj.EntityId}");
                var behaviour = obj.Behaviours[index];
                switch (type)
                {
                    case Packet.Variable:
                        behaviour.ReadVariable(reader.ReadUShort(), reader.ReadBytes());
                        break;
                    case Packet.Transform:
                        byte flags = reader.ReadByte();
                        Vector3 position = (flags & 1) != 0 ? reader.ReadVector3() : default;
                        Quaternion rotation = (flags & 2) != 0 ? reader.ReadQuaternion() : default;
                        if (behaviour is not NetworkTransform movement ||
                            !movement.AcceptOwnerState(obj.OwnerSession, flags, position, rotation))
                            throw new InvalidOperationException($"Invalid queued owner transform for {obj.EntityId}");
                        break;
                    case Packet.Animator:
                        if (behaviour is not NetworkAnimator animator ||
                            !animator.AcceptOwnerState(obj.OwnerSession, reader.ReadBytes()))
                            throw new InvalidOperationException($"Invalid queued owner Animator for {obj.EntityId}");
                        break;
                    default:
                        throw new InvalidOperationException($"Invalid queued owner packet for {obj.EntityId}");
                }
                if (reader.HasRemaining)
                    throw new InvalidOperationException($"Extra queued owner state for {obj.EntityId}");
            }
        }

        private static void ApplyOwnerState(NetworkObject obj, byte[] state)
        {
            using (var reader = new NetReader(state))
            {
                obj.ReadOwnerState(reader);
                if (reader.HasRemaining)
                    throw new InvalidOperationException($"Extra owner state for entity {obj.EntityId}");
            }
        }

        private void ReceiveWorkerPrepare(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            uint snapshotTick = reader.ReadUInt();
            byte[] bytes = reader.ReadBytes();
            if (!spawned.TryGetValue(id, out var obj)) throw new InvalidOperationException($"Worker has no ghost for handoff entity {id}");
            if (epoch <= obj.AuthorityEpoch) throw new InvalidOperationException($"Stale handoff prepare for entity {id}");
            using (var state = new NetReader(bytes))
            {
                obj.ReadHandoff(state);
                if (state.HasRemaining) throw new InvalidOperationException($"Extra handoff state for entity {id}");
            }
            Debug.Log($"AtlasNet prepared entity {id} at source tick {snapshotTick}, epoch {epoch}");
            using (var ack = new NetWriter())
            {
                ack.Write((byte)Packet.WorkerPrepared);
                ack.Write(id);
                ack.Write(epoch);
                transport.SendToServer(ack.ToArray());
            }
        }

        private void ReceiveWorkerExportRequest(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint currentEpoch = reader.ReadUInt();
            uint nextEpoch = reader.ReadUInt();
            ulong destination = reader.ReadULong();
            if (!spawned.TryGetValue(id, out var obj) || obj.SimulationWorker != localWorkerId ||
                obj.AuthorityEpoch != currentEpoch || nextEpoch <= currentEpoch || pendingHandoffs.ContainsKey(id))
                throw new InvalidOperationException($"Invalid handoff export request for entity {id}");
            using (var state = new NetWriter())
            using (var packet = new NetWriter())
            {
                obj.WriteHandoff(state);
                pendingHandoffs.Add(id, new PendingHandoff
                {
                    Source = new SessionId(localWorkerId), Destination = new SessionId(destination),
                    Epoch = nextEpoch, StartedAtTick = Tick
                });
                obj.NotifySimulationAuthorityChanged();
                packet.Write((byte)Packet.WorkerExport);
                packet.Write(id);
                packet.Write(currentEpoch);
                packet.Write(nextEpoch);
                packet.Write(destination);
                packet.Write(Tick);
                packet.WriteBytes(state.ToArray());
                transport.SendToServer(packet.ToArray());
            }
        }

        private void ReceiveWorkerExport(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint currentEpoch = reader.ReadUInt();
            uint nextEpoch = reader.ReadUInt();
            ulong destination = reader.ReadULong();
            uint snapshotTick = reader.ReadUInt();
            byte[] state = reader.ReadBytes();
            if (!workerSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                !pendingHandoffs.TryGetValue(id, out var pending) || pending.Source != sender ||
                pending.Destination.Value != destination || pending.Epoch != nextEpoch ||
                entity.Worker != sender.Value || entity.Epoch != currentEpoch)
                throw new InvalidOperationException($"Invalid handoff export from worker {sender} for entity {id}");
            var obj = spawned[id]; // Held only while this handoff is pending.
            using (var snapshot = new NetReader(state))
            {
                obj.ReadHandoff(snapshot);
                if (snapshot.HasRemaining) throw new InvalidOperationException($"Extra handoff state for {id}");
            }
            // The export contains owner packets received before the request; replay
            // only packets accepted after handoff began, preserving later input.
            ReplayOwnerUpdates(obj, pending.OwnerUpdates);
            entity.Capture(obj);
            if (destination == 0)
            {
                CommitHandoff(entity, pending);
            }
            else
            {
                using (var packet = new NetWriter())
                {
                    packet.Write((byte)Packet.WorkerPrepare);
                    packet.Write(id);
                    packet.Write(nextEpoch);
                    packet.Write(snapshotTick);
                    packet.WriteBytes(state);
                    transport.SendTo(pending.Destination, packet.ToArray());
                }
            }
        }

        private void ReceiveWorkerPrepared(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            if (!workerSessions.Contains(sender) || !pendingHandoffs.TryGetValue(id, out var pending) ||
                pending.Destination != sender || pending.Epoch != epoch || !directory.TryGetValue(id, out var entity))
                throw new InvalidOperationException($"Invalid worker handoff acknowledgment for entity {id}");
            CommitHandoff(entity, pending);
        }

        private void CommitHandoff(CanonicalEntity entity, PendingHandoff pending)
        {
            EntityId id = entity.Id;
            uint epoch = pending.Epoch;
            pendingHandoffs.Remove(id);
            entity.Worker = pending.Destination.Value;
            entity.Epoch = epoch;
            spawned.TryGetValue(id, out var obj);
            if (obj != null)
            {
                obj.SetSimulationAuthority(entity.Worker, epoch);
                entity.Capture(obj);
            }
            lastHandoffTick[id] = Tick;
            byte[] authorityChange = MakeAuthorityChange(entity);
            foreach (var client in clientSessions)
                if (IsObserver(id, client)) transport.SendTo(client, authorityChange);
            if (clientDebugRegionSubscribers.TryGetValue(entity.Owner, out var requested) && requested == id)
                SendClientDebugRegion(entity.Owner, entity);
            using (var commit = new NetWriter())
            {
                commit.Write((byte)Packet.WorkerCommit);
                commit.Write(id);
                commit.Write(pending.Destination.Value);
                commit.Write(epoch);
                bool hasOwnerState = entity.Owner.Value != 0;
                commit.Write(hasOwnerState);
                if (hasOwnerState) commit.WriteBytes(CaptureOwnerState(obj));
                SendToWorkers(id, commit.ToArray());
            }
            FlushAuthorityRpcs(id, pending.Destination.Value);
            Debug.Log($"AtlasNet committed handoff of {id} to worker {pending.Destination}, epoch {epoch}");
        }

        private void AbortPendingHandoffs(SessionId destination)
        {
            var abort = new List<EntityId>();
            foreach (var pair in pendingHandoffs)
                if (pair.Value.Destination == destination || pair.Value.Source == destination) abort.Add(pair.Key);
            foreach (var id in abort) AbortPendingHandoff(id);
        }

        private void CheckHandoffTimeouts()
        {
            if (isWorker || pendingHandoffs.Count == 0) return;
            var expired = new List<EntityId>();
            foreach (var pair in pendingHandoffs)
                if (Tick - pair.Value.StartedAtTick > tickRate * 5) expired.Add(pair.Key);
            foreach (var id in expired) AbortPendingHandoff(id);
        }

        private void CheckAutomaticHandoffs()
        {
            if (!automaticLocalHandoffs || !IsServer || isWorker || workerSessions.Count == 0 || localWorld == null) return;
            int started = 0;
            foreach (var entity in directory.Values)
            {
                if (started >= 4) break;
                if (!entity.CanHandoff || pendingHandoffs.ContainsKey(entity.Id)) continue;
                if (lastHandoffTick.TryGetValue(entity.Id, out uint last) && Tick - last < tickRate * 2) continue;
                ulong destination = localWorld.OwnerAt(entity.Position.x, entity.Position.z);
                if (destination == entity.Worker ||
                    !localWorld.ShouldMove(entity.Worker, entity.Position.x, entity.Position.z, localBoundaryMargin)) continue;
                HandoffToWorker(entity, destination);
                started++;
            }
        }

        private void AbortPendingHandoff(EntityId id)
        {
            if (pendingHandoffs.TryGetValue(id, out var pending))
            {
                pendingHandoffs.Remove(id);
                if (spawned.TryGetValue(id, out var obj)) obj.NotifySimulationAuthorityChanged();
                if (pending.Source.Value != 0 && workerSessions.Contains(pending.Source))
                {
                    using (var packet = new NetWriter())
                    {
                        packet.Write((byte)Packet.WorkerAbort);
                        packet.Write(id);
                        packet.Write(pending.Epoch);
                        transport.SendTo(pending.Source, packet.ToArray());
                    }
                }
                Debug.LogWarning($"AtlasNet aborted uncommitted handoff of {id}; epoch remains unchanged");
                FlushAuthorityRpcs(id, pending.Source.Value);
            }
        }

        private void ReceiveWorkerCommit(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            ulong worker = reader.ReadULong();
            uint epoch = reader.ReadUInt();
            bool hasOwnerState = reader.ReadBool();
            byte[] ownerState = hasOwnerState ? reader.ReadBytes() : null;
            if (!spawned.TryGetValue(id, out var obj)) throw new InvalidOperationException($"Unknown committed entity {id}");
            if (reader.HasRemaining || epoch <= obj.AuthorityEpoch ||
                hasOwnerState != (obj.OwnerSession.Value != 0))
                throw new InvalidOperationException($"Invalid worker commit state for entity {id}");
            // Apply the coordinator's latest owner channels while this is still a ghost,
            // before the new simulation authority is exposed to gameplay callbacks.
            if (hasOwnerState) ApplyOwnerState(obj, ownerState);
            pendingHandoffs.Remove(id);
            obj.SetSimulationAuthority(worker, epoch);
        }

        private void ReceiveWorkerAbort(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            if (pendingHandoffs.TryGetValue(id, out var pending) && pending.Epoch == epoch)
            {
                pendingHandoffs.Remove(id);
                if (spawned.TryGetValue(id, out var obj)) obj.NotifySimulationAuthorityChanged();
            }
        }

        private void ReceiveAuthorityChange(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            ulong worker = reader.ReadULong();
            uint epoch = reader.ReadUInt();
            if (spawned.TryGetValue(id, out var obj)) obj.SetSimulationAuthority(worker, epoch);
        }

        private static byte[] MakeAuthorityChange(CanonicalEntity entity)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.AuthorityChange);
                writer.Write(entity.Id);
                writer.Write(entity.Worker);
                writer.Write(entity.Epoch);
                return writer.ToArray();
            }
        }

        private void ReceiveWorkerTransform(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte index = reader.ReadByte();
            byte flags = reader.ReadByte();
            if (flags == 0 || (flags & ~3) != 0) throw new InvalidOperationException("Invalid worker transform flags");
            Vector3 position = (flags & 1) != 0 ? reader.ReadVector3() : default;
            Quaternion rotation = (flags & 2) != 0 ? reader.ReadQuaternion() : default;
            if (!workerSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                entity.Worker != sender.Value || entity.Epoch != epoch)
                throw new InvalidOperationException($"Stale or unauthorized worker transform for entity {id}, epoch {epoch}");
            var schema = Schema(id, index);
            if (!schema.IsTransform)
                throw new InvalidOperationException($"Missing NetworkTransform {index} for entity {id}");
            if (((flags & 1) != 0 && (!schema.SyncPosition || schema.PositionWriter != TransformWriter.Server)) ||
                ((flags & 2) != 0 && (!schema.SyncRotation || schema.RotationWriter != TransformWriter.Server)))
                throw new InvalidOperationException($"Worker attempted to write a client-owned transform channel for entity {id}");
            if (FindBehaviour(id, index) is NetworkTransform transform)
                transform.AcceptServerState(flags, position, rotation);
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.Transform);
                packet.Write(id);
                packet.Write(index);
                packet.Write(flags);
                if ((flags & 1) != 0) packet.Write(position);
                if ((flags & 2) != 0) packet.Write(rotation);
                byte[] bytes = packet.ToArray();
                SendToObservers(id, bytes);
                SendToWorkers(id, bytes, sender);
            }
        }

        private void SendWorkerAnimator(NetworkAnimator component, byte[] payload)
        {
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerAnimator);
                packet.Write(component.NetworkObject.EntityId);
                packet.Write(component.NetworkObject.AuthorityEpoch);
                packet.Write(component.BehaviourIndex);
                packet.WriteBytes(payload);
                transport.SendToServer(packet.ToArray());
            }
        }

        private void ReceiveWorkerAnimator(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte index = reader.ReadByte();
            byte[] payload = reader.ReadBytes();
            if (!workerSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                entity.Worker != sender.Value || entity.Epoch != epoch ||
                !Schema(id, index).IsAnimator || Schema(id, index).AnimatorWriter != AnimatorWriter.Server)
                throw new InvalidOperationException($"Stale or unauthorized worker animator update for entity {id}");
            if (FindBehaviour(id, index) is NetworkAnimator animator) animator.AcceptServerState(payload);
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.Animator);
                packet.Write(id);
                packet.Write(index);
                packet.WriteBytes(payload);
                byte[] data = packet.ToArray();
                SendToObservers(id, data);
                SendToWorkers(id, data, sender);
            }
        }

        private void ForwardAuthorityRpc(EntityId id, byte index, uint method, byte[] payload, SessionId sender)
        {
            if (pendingHandoffs.ContainsKey(id))
            {
                if (!queuedAuthorityRpcs.TryGetValue(id, out var queue))
                    queuedAuthorityRpcs[id] = queue = new List<QueuedAuthorityRpc>();
                if (queue.Count >= 512) throw new InvalidOperationException($"Authority RPC queue is full for entity {id}");
                queue.Add(new QueuedAuthorityRpc
                {
                    Behaviour = index, Method = method, Payload = payload, Sender = sender
                });
                return;
            }
            SendAuthorityRpcToWorker(id, index, method, payload, sender, directory[id].Worker);
        }

        private void FlushAuthorityRpcs(EntityId id, ulong worker)
        {
            if (!queuedAuthorityRpcs.TryGetValue(id, out var queue)) return;
            queuedAuthorityRpcs.Remove(id);
            foreach (var rpc in queue)
            {
                if (worker == 0 && FindBehaviour(id, rpc.Behaviour) is NetworkBehaviour behaviour)
                    behaviour.ReceiveRpc(rpc.Method, rpc.Payload, rpc.Sender, RpcDestination.Authority, default);
                else if (workerSessions.Contains(new SessionId(worker)))
                    SendAuthorityRpcToWorker(id, rpc.Behaviour, rpc.Method, rpc.Payload, rpc.Sender, worker);
            }
        }

        private void SendAuthorityRpcToWorker(EntityId id, byte index, uint method, byte[] payload,
            SessionId sender, ulong workerId)
        {
            var worker = new SessionId(workerId);
            if (!workerSessions.Contains(worker))
                throw new InvalidOperationException($"Authority worker {worker} is unavailable for entity {id}");
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerAuthorityRpc);
                packet.Write(id);
                packet.Write(directory[id].Epoch);
                packet.Write(index);
                packet.Write(method);
                packet.Write(sender);
                packet.WriteBytes(payload);
                transport.SendTo(worker, packet.ToArray());
            }
        }

        private void ReceiveWorkerAuthorityRpc(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte index = reader.ReadByte();
            uint method = reader.ReadUInt();
            SessionId sender = reader.ReadSessionId();
            byte[] payload = reader.ReadBytes();
            if (!spawned.TryGetValue(id, out var obj) || obj.SimulationWorker != localWorkerId ||
                obj.AuthorityEpoch != epoch || FindBehaviour(id, index) is not NetworkBehaviour behaviour)
                throw new InvalidOperationException($"Stale authority RPC for entity {id}, epoch {epoch}");
            behaviour.ReceiveRpc(method, payload, sender, RpcDestination.Authority, default);
        }

        private void SendWorkerVariable(NetworkBehaviour behaviour, ushort variable, byte[] payload)
        {
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerVariable);
                packet.Write(behaviour.NetworkObject.EntityId);
                packet.Write(behaviour.NetworkObject.AuthorityEpoch);
                packet.Write(behaviour.BehaviourIndex);
                packet.Write(variable);
                packet.WriteBytes(payload);
                transport.SendToServer(packet.ToArray());
            }
        }

        private void ReceiveWorkerVariable(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte index = reader.ReadByte();
            ushort variable = reader.ReadUShort();
            byte[] payload = reader.ReadBytes();
            if (!workerSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                entity.Worker != sender.Value || entity.Epoch != epoch)
                throw new InvalidOperationException($"Stale or unauthorized worker variable for entity {id}");
            if (!Schema(id, index).Variables.TryGetValue(variable, out var state) ||
                state.Write != NetworkVariableWritePermission.Server)
                throw new InvalidOperationException($"Worker cannot write owner variable {variable} on entity {id}");
            FindBehaviour(id, index)?.ReadVariable(variable, payload);
            byte[] update = MakeVariablePacket(id, index, variable, payload);
            foreach (var session in clientSessions)
                if (IsObserver(id, session) &&
                    (state.Read == NetworkVariableReadPermission.Everyone || session == entity.Owner))
                    transport.SendTo(session, update);
            SendToWorkers(id, update, sender);
        }

        private void SendWorkerOutboundRpc(NetworkObject obj, byte[] rpcPacket)
        {
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerOutboundRpc);
                packet.Write(obj.EntityId);
                packet.Write(obj.AuthorityEpoch);
                packet.WriteBytes(rpcPacket);
                transport.SendToServer(packet.ToArray());
            }
        }

        private void ReceiveWorkerOutboundRpc(SessionId sender, NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte[] bytes = reader.ReadBytes();
            if (!workerSessions.Contains(sender) || !directory.TryGetValue(id, out var entity) ||
                entity.Worker != sender.Value || entity.Epoch != epoch)
                throw new InvalidOperationException($"Stale or unauthorized worker RPC for entity {id}");
            using (var rpc = new NetReader(bytes))
            {
                if ((Packet)rpc.ReadByte() != Packet.Rpc) throw new InvalidOperationException("Invalid worker RPC envelope");
                RpcDestination destination = (RpcDestination)rpc.ReadByte();
                if (rpc.ReadEntityId() != id) throw new InvalidOperationException("Worker RPC entity mismatch");
                byte index = rpc.ReadByte();
                SessionId target = rpc.ReadSessionId();
                uint method = rpc.ReadUInt();
                byte[] payload = rpc.ReadBytes();
                if (rpc.HasRemaining)
                    throw new InvalidOperationException($"Invalid worker RPC for entity {id}");
                if (destination != RpcDestination.Observers && destination != RpcDestination.Everyone &&
                    destination != RpcDestination.Target)
                    throw new InvalidOperationException("Worker cannot send an authority RPC outward");
                if (RpcSchemaFor(Schema(id, index), method).Destination != destination)
                    throw new InvalidOperationException($"Worker RPC {method} has wrong destination");
                if (destination == RpcDestination.Target && !clientSessions.Contains(target) &&
                    !(IsClient && target == LocalSession))
                    throw new InvalidOperationException($"Unknown target session {target}");
                if ((destination != RpcDestination.Target || (IsClient && target == LocalSession)) &&
                    FindBehaviour(id, index) is NetworkBehaviour behaviour)
                    behaviour.ReceiveRpc(method, payload, default, destination, target);
                if (destination == RpcDestination.Target)
                {
                    if (clientSessions.Contains(target) && IsObserver(id, target)) transport.SendTo(target, bytes);
                }
                else
                {
                    SendToObservers(id, bytes);
                    SendToWorkers(id, bytes, sender);
                }
            }
        }

        private void SendToWorkers(EntityId id, byte[] data, SessionId except = default)
        {
            foreach (var pair in workerResidents)
                if (pair.Key != except && pair.Value.Contains(id))
                    transport.SendTo(pair.Key, data);
        }

        private static byte[] MakeEntityPacket(Packet type, EntityId id)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)type);
                writer.Write(id);
                return writer.ToArray();
            }
        }
    }
}
