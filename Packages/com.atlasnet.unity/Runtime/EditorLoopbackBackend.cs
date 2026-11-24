#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    internal sealed class EditorLoopbackBackend : IWorldBackend
    {
        readonly NetworkManager runtime;
        readonly Queue<Action> pending = new();
        float elapsed;
        ulong nextEntity = 1;

        internal EditorLoopbackBackend(NetworkManager runtime) => this.runtime = runtime;

        public bool IsRunning { get; private set; }
        public bool IsServer => IsRunning;
        public bool IsClient => IsRunning;
        public bool IsHost => IsRunning;
        public SessionId LocalSession => IsRunning ? new SessionId(1) : default;
        public uint Tick { get; private set; }
        public int TrackedEntityCount => runtime.SpawnedCount;
        public int RemoteClientCount => 0;
        public int ObserverCopies => 0;
        public long BytesSent => 0;
        public long BytesSentLastTick => 0;
        public long AllocatedBytesLastTick => 0;
        public double LastTickMilliseconds => 0;
        public bool IsWorker => false;
        public ulong LocalWorkerId => 0;
        public int WorkerCount => 0;
        public int PendingHandoffCount => 0;
        public IReadOnlyList<Vector2> LocalRegion => Array.Empty<Vector2>();
        public int LocalRegionVersion => 0;
        public IReadOnlyList<Vector2> ClientDebugRegion => Array.Empty<Vector2>();
        public EntityId ClientDebugRegionEntity => default;
        public ulong ClientDebugRegionWorker => 0;
        public uint ClientDebugRegionEpoch => 0;
        public int ClientDebugRegionVersion => 0;
        public int LocalAuthorityCount => runtime.SpawnedCount;
        public int GhostCount => 0;
        public Func<SessionId, Vector3> PlayerSpawnPosition { get; set; }
        public event Action<SessionId> SessionJoined;
        public event Action<SessionId> SessionLeft;
        public event Action ServerStarted;
        public event Action<bool, bool> Stopped;

        public TransportCapabilities GetTransportCapabilities(SessionId peer)
            => peer == LocalSession && IsRunning ? TransportCapabilities.ReliableOrdered : 0;
        public void ValidateRegistry() => runtime.ValidatePrefabRegistry();
        public void StartServer() => throw HostOnly();
        public void StartClient() => throw HostOnly();
        public void StartWorker() => throw HostOnly();
        static Exception HostOnly() => new NotSupportedException("Editor Loopback runs one host. Call StartHost.");

        public void StartHost()
        {
            if (IsRunning) throw new InvalidOperationException("NetworkManager is already running.");
            ValidateRegistry();
            runtime.ResetConnectionDiagnostics();
            elapsed = 0;
            Tick = 0;
            IsRunning = true;
            try
            {
                ServerStarted?.Invoke();
                if (!IsRunning) return;
                var prefab = runtime.PlayerPrefab;
                if (prefab != null)
                    Spawn(prefab, PlayerSpawnPosition?.Invoke(LocalSession) ?? prefab.transform.position,
                        prefab.transform.rotation, LocalSession);
                if (IsRunning) SessionJoined?.Invoke(LocalSession);
            }
            catch
            {
                Stop();
                throw;
            }
        }

        public void Stop()
        {
            if (!IsRunning) return;
            var session = LocalSession;
            IsRunning = false;
            pending.Clear();
            var ids = new List<EntityId>();
            foreach (var obj in runtime.SpawnedObjects) ids.Add(obj.EntityId);
            foreach (var id in ids) runtime.RemoveReplica(id);
            SessionLeft?.Invoke(session);
            Stopped?.Invoke(true, true);
        }

        public void PollAndTick(float deltaTime)
        {
            if (!IsRunning) return;
            elapsed += deltaTime;
            float interval = 1f / runtime.TickRate;
            while (IsRunning && elapsed >= interval)
            {
                elapsed -= interval;
                Tick++;
                int count = pending.Count;
                while (count-- > 0 && IsRunning && pending.Count > 0) pending.Dequeue()();
                if (IsRunning) runtime.TickBackendReplicas();
            }
        }

        public NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!runtime.TryGetRegisteredPrefab(prefabId, out var prefab))
                throw new InvalidOperationException($"Prefab '{prefabId}' is not registered.");
            return Spawn(prefab, position, rotation, owner);
        }

        public NetworkObject Spawn(NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!IsRunning || (owner.Value != 0 && owner != LocalSession))
                throw new InvalidOperationException("Spawn requires the active loopback host and a local owner.");
            if (!prefab || !runtime.TryGetRegisteredPrefab(prefab.PrefabId, out var registered) || registered != prefab)
                throw new InvalidOperationException("The network prefab must be registered.");
            return runtime.CreateReplica(prefab, new EntityId(nextEntity++), owner, position, rotation, 0, 0);
        }

        public void RequestSpawn(NetworkObject source, NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default)
        {
            if (!CanSimulate(source)) throw new InvalidOperationException("Spawn requests require an authoritative source.");
            pending.Enqueue(() => { if (CanSimulate(source)) Spawn(prefab, position, rotation, owner); });
        }

        public void Despawn(NetworkObject obj)
        {
            if (!CanSimulate(obj)) throw new InvalidOperationException("Despawn requires a local authoritative object.");
            TryDespawn(obj.EntityId);
        }

        public bool TryDespawn(EntityId id)
        {
            if (!IsRunning || !runtime.TryGet(id, out _)) return false;
            runtime.RemoveReplica(id);
            return true;
        }

        public bool CanSimulate(NetworkObject obj) => IsRunning && obj && obj.Manager == runtime && obj.IsSpawned;
        public void HideFrom(NetworkObject obj, SessionId session)
            => throw new NotSupportedException("Editor Loopback has no separate observer copy.");
        public void ShowTo(NetworkObject obj, SessionId session)
            => throw new NotSupportedException("Editor Loopback has no separate observer copy.");

        public void ValidateNamedMessageRecipient(SessionId recipient)
        {
            if (!IsRunning || (recipient != LocalSession && recipient.Value != 0))
                throw new InvalidOperationException("The recipient is not in this editor loopback session.");
        }

        public void SendNamedMessage(SessionId recipient, ulong messageId, byte[] payload, NetworkDelivery delivery)
        {
            ValidateNamedMessageRecipient(recipient);
            pending.Enqueue(() => runtime.DispatchNamedMessage(LocalSession, messageId, payload));
        }

        public void DisconnectClient(SessionId session, string reason = null)
        {
            ValidateNamedMessageRecipient(session);
            Stop();
        }

        public void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write)
        {
            var obj = behaviour.NetworkObject;
            if (!CanSimulate(obj)) throw new InvalidOperationException("RPC requires a spawned loopback object.");
            if (destination == RpcDestination.Target && target != LocalSession)
                throw new InvalidOperationException("No such target in this editor loopback session.");
            if (behaviour.GetRpcPermission(method) == RpcInvokePermission.Owner && !obj.IsOwner)
                throw new InvalidOperationException("Only the owner may invoke this RPC.");

            var id = obj.EntityId;
            var index = behaviour.BehaviourIndex;
            using var writer = new NetWriter();
            write?.Invoke(writer);
            var payload = writer.ToArray();
            pending.Enqueue(() => runtime.DispatchBackendRpc(id, index, method, payload, LocalSession, destination, target));
        }

        public void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write)
        {
            if (!CanSimulate(source)) throw new InvalidOperationException("An authoritative source is required.");
            SendRpc(target, RpcDestination.Authority, default, method, write);
        }

        // The host and authority share the same representation.
        public void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value) { }
        public void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation, Vector3 scale) { }
        public void SendAnimator(NetworkAnimator component, byte[] payload) { }
        public bool SetClientDebugRegionEnabled(NetworkObject ownedPlayer, bool enabled) => false;
        public bool RequestLocalRegionForDebug() => false;
        public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj) => false;
    }
}
#endif
