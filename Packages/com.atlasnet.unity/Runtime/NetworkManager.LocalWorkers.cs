using System;
using System.Collections.Generic;
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
        }

        private struct QueuedAuthorityRpc
        {
            public byte Behaviour;
            public uint Method;
            public byte[] Payload;
            public SessionId Sender;
        }

        private readonly HashSet<SessionId> workerSessions = new HashSet<SessionId>();
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

        public bool IsWorker => isWorker;
        public ulong LocalWorkerId => localWorkerId;
        public int WorkerCount => workerSessions.Count + (IsServer ? 1 : 0);
        public int PendingHandoffCount => pendingHandoffs.Count;
        /// <summary>Optional debug-only X/Z outline of this server's shard. Empty until requested or if unavailable.</summary>
        public IReadOnlyList<Vector2> LocalRegion => localRegion;
        public int LocalRegionVersion => localRegionVersion;
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

        /// <summary>Sample diagnostics only; canonical coordinator state can exist outside local interest.</summary>
        public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj)
        {
            if (!IsServer || obj == null || obj.Manager != this) return false;
            if (obj.HasAuthority) return true;
            if (isWorker) return true; // Workers only receive subscribed copies.
            return ShouldWorkerHold(new SessionId(0), obj, false);
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
                foreach (var obj in spawned.Values)
                    if (IsObserver(obj.EntityId, session)) transport.SendTo(session, MakeSpawn(obj, session));
                SpawnPlayer(session);
            }
            SessionJoined?.Invoke(session);
        }

        private bool ShouldClientHold(SessionId session, NetworkObject obj, bool alreadyResident)
        {
            if (obj.OwnerSession == session) return true;
            foreach (var source in spawned.Values)
            {
                if (source.OwnerSession != session) continue;
                var interest = source.GetComponent<NetworkInterestSource>();
                if (interest == null) continue;
                // A client receives its authoring worker's full entity stream. Radius
                // interest only determines which other workers' entities are observed.
                if (obj.SimulationWorker == source.SimulationWorker) return true;
                if (!interest.isActiveAndEnabled) continue;
                float radius = interest.Radius + (alreadyResident ? interest.ExitPadding : 0f);
                Vector3 center = source.transform.position;
                Vector3 target = obj.transform.position;
                float dx = target.x - center.x, dz = target.z - center.z;
                if (dx * dx + dz * dz <= radius * radius) return true;
            }
            return false;
        }

        private void RefreshClientInterest(SessionId session)
        {
            if (!clientResidents.TryGetValue(session, out var residents)) return;
            foreach (var obj in spawned.Values)
            {
                EntityId id = obj.EntityId;
                bool excluded = hidden.TryGetValue(id, out var sessions) && sessions.Contains(session);
                bool wanted = !excluded && ShouldClientHold(session, obj, residents.Contains(id));
                if (wanted && residents.Add(id)) transport.SendTo(session, MakeSpawn(obj, session));
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
            foreach (var session in clientSessions)
            {
                if (!clientResidents.TryGetValue(session, out var residents))
                {
                    if (IsObserver(obj.EntityId, session)) transport.SendTo(session, MakeSpawn(obj, session));
                }
                else if (ShouldClientHold(session, obj, false) && IsExplicitlyVisible(obj.EntityId, session) &&
                         residents.Add(obj.EntityId))
                    transport.SendTo(session, MakeSpawn(obj, session));
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

        private bool ShouldWorkerHold(SessionId worker, NetworkObject obj, bool alreadyResident)
        {
            if (obj.SimulationWorker == worker.Value) return true;
            if (pendingHandoffs.TryGetValue(obj.EntityId, out var pending) &&
                (pending.Source == worker || pending.Destination == worker)) return true;
            if (localWorld == null) return false;
            foreach (var source in spawned.Values)
            {
                if (source == obj || source.OwnerSession.Value == 0 ||
                    source.SimulationWorker != worker.Value) continue;
                var interest = source.GetComponent<NetworkInterestSource>();
                if (interest == null || !interest.isActiveAndEnabled) continue;
                float radius = interest.Radius + (alreadyResident ? interest.ExitPadding : 0f);
                Vector3 center = source.transform.position;
                // The region check finds candidate owners. A pending transfer can briefly
                // leave an entity outside its recorded owner's region, so keep that case.
                if (!localWorld.IsWithinInterest(obj.SimulationWorker, center.x, center.z, radius) &&
                    !pendingHandoffs.ContainsKey(obj.EntityId) &&
                    localWorld.OwnerAt(obj.transform.position.x, obj.transform.position.z) == obj.SimulationWorker)
                    continue;
                Vector3 target = obj.transform.position;
                float dx = target.x - center.x, dz = target.z - center.z;
                if (dx * dx + dz * dz <= radius * radius) return true;
            }
            return false;
        }

        private void EnsureWorkerResident(SessionId worker, NetworkObject obj)
        {
            if (workerResidents.TryGetValue(worker, out var residents) && residents.Add(obj.EntityId))
                transport.SendTo(worker, MakeWorkerSpawn(obj));
        }

        private void RefreshWorkerInterest()
        {
            if (!IsServer || isWorker || workerResidents.Count == 0) return;
            foreach (var pair in workerResidents)
            {
                SessionId worker = pair.Key;
                var residents = pair.Value;
                foreach (var obj in spawned.Values)
                {
                    bool wanted = ShouldWorkerHold(worker, obj, residents.Contains(obj.EntityId));
                    if (wanted) EnsureWorkerResident(worker, obj);
                    else if (residents.Remove(obj.EntityId))
                        transport.SendTo(worker, MakeEntityPacket(Packet.Despawn, obj.EntityId));
                }
            }
        }

        private void AddSpawnToWorkerInterest(NetworkObject obj)
        {
            foreach (var worker in workerSessions)
                if (ShouldWorkerHold(worker, obj, false)) EnsureWorkerResident(worker, obj);
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

        private byte[] MakeWorkerSpawn(NetworkObject obj)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.WorkerSpawn);
                writer.Write(obj.EntityId);
                writer.Write(obj.PrefabId);
                writer.Write(obj.OwnerSession);
                writer.Write(obj.transform.position);
                writer.Write(obj.transform.rotation);
                writer.Write(obj.SimulationWorker);
                writer.Write(obj.AuthorityEpoch);
                obj.WriteSnapshot(writer, obj.OwnerSession);
                return writer.ToArray();
            }
        }

        private void ReceiveWorkerSpawn(NetReader reader)
        {
            EntityId id = reader.ReadEntityId();
            string prefabId = reader.ReadString();
            SessionId owner = reader.ReadSessionId();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            ulong authority = reader.ReadULong();
            uint epoch = reader.ReadUInt();
            if (spawned.ContainsKey(id)) throw new InvalidOperationException($"Duplicate worker entity {id}");
            if (!registry.TryGetValue(prefabId, out var prefab))
                throw new InvalidOperationException($"Worker lacks registered prefab '{prefabId}' for entity {id}");
            var obj = Instantiate(prefab, position, rotation);
            try
            {
                obj.Initialize(this, id, owner, deferSpawnCallbacks: true);
                obj.SetSimulationAuthority(authority, epoch);
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

        /// <summary>Called by local spatial orchestration; gameplay does not choose workers.</summary>
        internal void HandoffToWorker(NetworkObject obj, ulong destinationWorker)
        {
            if (!IsServer || isWorker) throw new InvalidOperationException("Only the first local server coordinates handoffs");
            if (obj == null || obj.Manager != this || !spawned.ContainsKey(obj.EntityId))
                throw new InvalidOperationException("Object is not spawned on this coordinator");
            var destination = new SessionId(destinationWorker);
            if (destinationWorker != 0 && !workerSessions.Contains(destination))
                throw new InvalidOperationException($"Worker {destinationWorker} is not connected");
            if (obj.SimulationWorker == destinationWorker)
                throw new InvalidOperationException($"Entity {obj.EntityId} is already simulated by worker {destinationWorker}");
            if (pendingHandoffs.ContainsKey(obj.EntityId)) throw new InvalidOperationException("Entity handoff is already pending");
            if (destinationWorker != 0) EnsureWorkerResident(destination, obj);
            uint nextEpoch = checked(obj.AuthorityEpoch + 1);
            var pending = new PendingHandoff
            {
                Source = new SessionId(obj.SimulationWorker), Destination = destination,
                Epoch = nextEpoch, StartedAtTick = Tick
            };
            byte[] localState = null;
            if (obj.SimulationWorker == 0)
            {
                using (var state = new NetWriter())
                {
                    obj.WriteHandoff(state);
                    localState = state.ToArray();
                }
            }
            pendingHandoffs.Add(obj.EntityId, pending);
            if (obj.SimulationWorker == 0) obj.NotifySimulationAuthorityChanged();
            if (obj.SimulationWorker != 0)
            {
                using (var request = new NetWriter())
                {
                    request.Write((byte)Packet.WorkerExportRequest);
                    request.Write(obj.EntityId);
                    request.Write(obj.AuthorityEpoch);
                    request.Write(nextEpoch);
                    request.Write(destinationWorker);
                    transport.SendTo(pending.Source, request.ToArray());
                }
                Debug.Log($"AtlasNet requested handoff state for {obj.EntityId} from worker {pending.Source}");
                return;
            }
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerPrepare);
                packet.Write(obj.EntityId);
                packet.Write(nextEpoch);
                packet.Write(Tick);
                packet.WriteBytes(localState);
                transport.SendTo(destination, packet.ToArray());
            }
            Debug.Log($"AtlasNet preparing handoff of {obj.EntityId} to worker {destinationWorker}, epoch {nextEpoch}");
        }

        private static byte[] CaptureOwnerState(NetworkObject obj)
        {
            using (var state = new NetWriter())
            {
                obj.WriteOwnerState(state);
                return state.ToArray();
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
            if (!workerSessions.Contains(sender) || !spawned.TryGetValue(id, out var obj) ||
                !pendingHandoffs.TryGetValue(id, out var pending) || pending.Source != sender ||
                pending.Destination.Value != destination || pending.Epoch != nextEpoch ||
                obj.SimulationWorker != sender.Value || obj.AuthorityEpoch != currentEpoch)
                throw new InvalidOperationException($"Invalid handoff export from worker {sender} for entity {id}");
            if (destination == 0)
            {
                // The worker's snapshot may predate owner updates already accepted by
                // the coordinator. Transfer simulation state without rewinding those channels.
                byte[] latestOwnerState = CaptureOwnerState(obj);
                using (var snapshot = new NetReader(state))
                {
                    obj.ReadHandoff(snapshot);
                    if (snapshot.HasRemaining) throw new InvalidOperationException($"Extra handoff state for {id}");
                }
                ApplyOwnerState(obj, latestOwnerState);
                CommitHandoff(obj, pending);
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
                pending.Destination != sender || pending.Epoch != epoch || !spawned.TryGetValue(id, out var obj))
                throw new InvalidOperationException($"Invalid worker handoff acknowledgment for entity {id}");
            CommitHandoff(obj, pending);
        }

        private void CommitHandoff(NetworkObject obj, PendingHandoff pending)
        {
            EntityId id = obj.EntityId;
            uint epoch = pending.Epoch;
            pendingHandoffs.Remove(id);
            obj.SetSimulationAuthority(pending.Destination.Value, epoch);
            lastHandoffTick[id] = Tick;
            byte[] authorityChange = MakeAuthorityChange(obj);
            foreach (var client in clientSessions)
                if (IsObserver(id, client)) transport.SendTo(client, authorityChange);
            using (var commit = new NetWriter())
            {
                commit.Write((byte)Packet.WorkerCommit);
                commit.Write(id);
                commit.Write(pending.Destination.Value);
                commit.Write(epoch);
                bool hasOwnerState = obj.OwnerSession.Value != 0;
                commit.Write(hasOwnerState);
                if (hasOwnerState) commit.WriteBytes(CaptureOwnerState(obj));
                SendToWorkers(id, commit.ToArray());
            }
            FlushAuthorityRpcs(obj, pending.Destination.Value);
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
            foreach (var obj in spawned.Values)
            {
                if (started >= 4) break;
                if (obj == null || pendingHandoffs.ContainsKey(obj.EntityId) ||
                    obj.GetComponent<NetworkTransform>() is not NetworkTransform movement ||
                    !movement.SyncPosition || movement.Target != obj.transform ||
                    (movement.PositionWriter == TransformWriter.Owner && obj.OwnerSession.Value == 0)) continue;
                if (lastHandoffTick.TryGetValue(obj.EntityId, out uint last) && Tick - last < tickRate * 2) continue;
                ulong destination = localWorld.OwnerAt(obj.transform.position.x, obj.transform.position.z);
                if (destination == obj.SimulationWorker ||
                    !localWorld.ShouldMove(obj.SimulationWorker, obj.transform.position.x, obj.transform.position.z, localBoundaryMargin))
                    continue;
                HandoffToWorker(obj, destination);
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
                if (spawned.TryGetValue(id, out var source)) FlushAuthorityRpcs(source, pending.Source.Value);
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

        private static byte[] MakeAuthorityChange(NetworkObject obj)
        {
            using (var writer = new NetWriter())
            {
                writer.Write((byte)Packet.AuthorityChange);
                writer.Write(obj.EntityId);
                writer.Write(obj.SimulationWorker);
                writer.Write(obj.AuthorityEpoch);
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
            if (!workerSessions.Contains(sender) || !spawned.TryGetValue(id, out var obj) ||
                obj.SimulationWorker != sender.Value || obj.AuthorityEpoch != epoch)
                throw new InvalidOperationException($"Stale or unauthorized worker transform for entity {id}, epoch {epoch}");
            if (FindBehaviour(id, index) is not NetworkTransform transform)
                throw new InvalidOperationException($"Missing NetworkTransform {index} for entity {id}");
            if (((flags & 1) != 0 && (!transform.SyncPosition || transform.PositionWriter != TransformWriter.Server)) ||
                ((flags & 2) != 0 && (!transform.SyncRotation || transform.RotationWriter != TransformWriter.Server)))
                throw new InvalidOperationException($"Worker attempted to write a client-owned transform channel for entity {id}");
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
            if (!workerSessions.Contains(sender) || !spawned.TryGetValue(id, out var obj) ||
                obj.SimulationWorker != sender.Value || obj.AuthorityEpoch != epoch ||
                FindBehaviour(id, index) is not NetworkAnimator animator || animator.Writer != AnimatorWriter.Server)
                throw new InvalidOperationException($"Stale or unauthorized worker animator update for entity {id}");
            animator.AcceptServerState(payload);
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

        private void ForwardAuthorityRpc(NetworkBehaviour behaviour, uint method, byte[] payload, SessionId sender)
        {
            var obj = behaviour.NetworkObject;
            if (pendingHandoffs.ContainsKey(obj.EntityId))
            {
                if (!queuedAuthorityRpcs.TryGetValue(obj.EntityId, out var queue))
                    queuedAuthorityRpcs[obj.EntityId] = queue = new List<QueuedAuthorityRpc>();
                if (queue.Count >= 512) throw new InvalidOperationException($"Authority RPC queue is full for entity {obj.EntityId}");
                queue.Add(new QueuedAuthorityRpc
                {
                    Behaviour = behaviour.BehaviourIndex, Method = method, Payload = payload, Sender = sender
                });
                return;
            }
            SendAuthorityRpcToWorker(behaviour, method, payload, sender, obj.SimulationWorker);
        }

        private void FlushAuthorityRpcs(NetworkObject obj, ulong worker)
        {
            if (!queuedAuthorityRpcs.TryGetValue(obj.EntityId, out var queue)) return;
            queuedAuthorityRpcs.Remove(obj.EntityId);
            foreach (var rpc in queue)
            {
                var behaviour = FindBehaviour(obj.EntityId, rpc.Behaviour);
                if (worker == 0) behaviour.ReceiveRpc(rpc.Method, rpc.Payload, rpc.Sender, RpcDestination.Authority, default);
                else if (workerSessions.Contains(new SessionId(worker)))
                    SendAuthorityRpcToWorker(behaviour, rpc.Method, rpc.Payload, rpc.Sender, worker);
            }
        }

        private void SendAuthorityRpcToWorker(NetworkBehaviour behaviour, uint method, byte[] payload,
            SessionId sender, ulong workerId)
        {
            var obj = behaviour.NetworkObject;
            var worker = new SessionId(workerId);
            if (!workerSessions.Contains(worker))
                throw new InvalidOperationException($"Authority worker {worker} is unavailable for entity {obj.EntityId}");
            using (var packet = new NetWriter())
            {
                packet.Write((byte)Packet.WorkerAuthorityRpc);
                packet.Write(obj.EntityId);
                packet.Write(obj.AuthorityEpoch);
                packet.Write(behaviour.BehaviourIndex);
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
            if (!workerSessions.Contains(sender) || !spawned.TryGetValue(id, out var obj) ||
                obj.SimulationWorker != sender.Value || obj.AuthorityEpoch != epoch ||
                FindBehaviour(id, index) is not NetworkBehaviour behaviour)
                throw new InvalidOperationException($"Stale or unauthorized worker variable for entity {id}");
            var state = behaviour.GetVariable(variable);
            if (state.WritePermission != NetworkVariableWritePermission.Server)
                throw new InvalidOperationException($"Worker cannot write owner variable {variable} on entity {id}");
            behaviour.ReadVariable(variable, payload);
            byte[] update = MakeVariablePacket(id, index, variable, payload);
            foreach (var session in clientSessions)
                if (IsObserver(id, session) &&
                    (state.ReadPermission == NetworkVariableReadPermission.Everyone || session == obj.OwnerSession))
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
            if (!workerSessions.Contains(sender) || !spawned.TryGetValue(id, out var obj) ||
                obj.SimulationWorker != sender.Value || obj.AuthorityEpoch != epoch)
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
                if (rpc.HasRemaining || FindBehaviour(id, index) is not NetworkBehaviour behaviour)
                    throw new InvalidOperationException($"Invalid worker RPC for entity {id}");
                if (destination != RpcDestination.Observers && destination != RpcDestination.Everyone &&
                    destination != RpcDestination.Target)
                    throw new InvalidOperationException("Worker cannot send an authority RPC outward");
                if (behaviour.GetRpcDestination(method) != destination)
                    throw new InvalidOperationException($"Worker RPC {method} has wrong destination");
                if (destination == RpcDestination.Target && !clientSessions.Contains(target) &&
                    !(IsClient && target == LocalSession))
                    throw new InvalidOperationException($"Unknown target session {target}");
                if (destination != RpcDestination.Target || (IsClient && target == LocalSession))
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
