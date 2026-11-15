using System;
using System.Collections.Generic;
using AtlasNet;
using Newtonsoft.Json.Linq;
using UnityEngine;
using EntityId = AtlasNet.EntityId;

internal sealed partial class NativeShardBackend : IWorldBackend, IWorldBackendTelemetry
{
    internal static readonly byte[] SpectatorAdmission = { 0x41, 0x54, 0x4C, 0x53, 1 };
    private readonly NetworkManager manager;
    private readonly NetworkObject observerPrefab;
    private readonly HashSet<EntityId> frozen = new();
    private readonly Dictionary<EntityId, Vector3> positions = new();
    private readonly UnityShardMigration migration;
    private UnityShardConnection connection;
    private float accumulated;
    private bool quitting;
    private bool ownsOrigin;
    private JToken regionShape;
    private ulong nextRegistration;
    private uint nextSnapshotTick;

    public NativeShardBackend(NetworkManager manager, NetworkObject observerPrefab)
    {
        this.manager = manager;
        this.observerPrefab = observerPrefab;
        migration = new UnityShardMigration(manager, Capture, Freeze, Restore);
    }

    public bool IsServer => connection != null;
    public bool IsClient => client != null;
    public bool IsHost => false;
    public bool IsRunning => IsServer || IsClient;
    public bool IsWorker => !ownsOrigin;
    public SessionId LocalSession { get; private set; }
    public uint Tick { get; private set; }
    public int TrackedEntityCount => manager.SpawnedCount;
    public int RemoteClientCount => manager.ConnectedClientsIds.Count;
    public int ObserverCopies => 0;
    public int GhostCount => 0;
    public long BytesSent => client == null ? 0 : System.Threading.Interlocked.Read(ref client.BytesSent);
    public long BytesSentLastTick => 0;
    public long AllocatedBytesLastTick => -1;
    public double LastTickMilliseconds => 0;
    public ulong LocalWorkerId { get; private set; }
    public int WorkerCount => 0;
    public int PendingHandoffCount => frozen.Count;
    public IReadOnlyList<Vector2> LocalRegion => Array.Empty<Vector2>();
    public int LocalRegionVersion => 0;
    private NetworkObject debugPlayer;
    private JObject displayedRegion;
    private readonly List<Vector2> debugOutline = new();
    private int debugRegionVersion;
    public IReadOnlyList<Vector2> ClientDebugRegion => debugOutline;
    public EntityId ClientDebugRegionEntity => debugPlayer ? debugPlayer.EntityId : default;
    public ulong ClientDebugRegionWorker => debugPlayer ? debugPlayer.SimulationWorker : 0;
    public uint ClientDebugRegionEpoch => debugPlayer ? debugPlayer.AuthorityEpoch : 0;
    public int ClientDebugRegionVersion
    {
        get
        {
            DebugRegions.TryGetValue(ClientDebugRegionWorker, out var shape);
            if (JToken.DeepEquals(displayedRegion, shape)) return debugRegionVersion;
            displayedRegion = shape;
            debugRegionVersion++;
            BuildRegionOutline(shape, debugOutline);
            return debugRegionVersion;
        }
    }

    internal static void BuildRegionOutline(JObject shape, List<Vector2> outline)
    {
        outline.Clear();
        if (shape?["min"] is not JArray min || shape["max"] is not JArray max) return;
        int z = shape.Value<int>("dimension") == 2 ? 1 : 2;
        if (min.Count <= z || max.Count <= z) return;
        float left = Mathf.Max(-65, (float?)min[0] ?? -65);
        float right = Mathf.Min(65, (float?)max[0] ?? 65);
        float bottom = Mathf.Max(-40, (float?)min[z] ?? -40);
        float top = Mathf.Min(40, (float?)max[z] ?? 40);
        if (left >= right || bottom >= top) return;
        outline.Add(new Vector2(left, bottom));
        outline.Add(new Vector2(right, bottom));
        outline.Add(new Vector2(right, top));
        outline.Add(new Vector2(left, top));
        if (shape["planes"] is not JArray planes) return;
        foreach (var plane in planes)
        {
            if (plane["normal"] is not JArray normal || normal.Count <= z) continue;
            BackendSmokeLauncher.Clip(outline, new Vector2((float)normal[0], (float)normal[z]),
                plane.Value<float>("offset"));
        }
    }

    public int LocalAuthorityCount => manager.SpawnedCount - frozen.Count;
    public Func<SessionId, Vector3> PlayerSpawnPosition { get; set; }

    public event Action<SessionId> SessionJoined;
    public event Action<SessionId> SessionLeft { add { } remove { } }
    public event Action ServerStarted;
    public event Action<bool, bool> Stopped;

    public void ValidateRegistry() => manager.ValidatePrefabRegistry();

    public void StartServer()
    {
        if (IsRunning) throw new InvalidOperationException("Shard already running.");
        ValidateRegistry();
        string[] args = Environment.GetCommandLineArgs();
        int socketIndex = Array.IndexOf(args, "-atlasnet-socket");
        int shardIndex = Array.IndexOf(args, "-atlasnet-shard");
        if (socketIndex < 0 || socketIndex + 1 >= args.Length ||
            shardIndex < 0 || shardIndex + 1 >= args.Length ||
            !Guid.TryParse(args[shardIndex + 1], out var shard))
            throw new InvalidOperationException("Start this server through an AtlasNet shard worker.");
        byte[] id = shard.ToByteArray();
        LocalWorkerId = BitConverter.ToUInt64(id, 0) ^ BitConverter.ToUInt64(id, 8);
        connection = new UnityShardConnection(args[socketIndex + 1]);
        try { connection.Send(new JObject { ["type"] = "ready", ["version"] = 1 }); }
        catch { Stop(); throw; }
        Tick = 0;
        accumulated = 0;
        nextSnapshotTick = 0;
        ServerStarted?.Invoke();
    }

    public void PollAndTick(float deltaTime)
    {
        if (!IsRunning) return;
        try
        {
            if (IsClient) { PollClient(deltaTime); return; }
            for (int i = 0; i < 64 && IsRunning && connection.TryReceive(out var request); i++)
                Handle(request);
            if (!IsRunning) return;

            float interval = 1f / manager.TickRate;
            accumulated = Mathf.Min(accumulated + deltaTime, interval * 4);
            while (accumulated >= interval)
            {
                accumulated -= interval;
                Tick++;
                manager.TickBackendReplicas();
                bool publishSnapshots = Tick >= nextSnapshotTick;
                if (publishSnapshots) nextSnapshotTick = Tick + (uint)manager.TickRate;
                foreach (var obj in manager.SpawnedObjects)
                {
                    if (!CanSimulate(obj)) continue;
                    if (publishSnapshots && regionShape != null)
                    {
                        using var region = new NetWriter();
                        region.Write(LocalWorkerId);
                        region.Write(regionShape.ToString(Newtonsoft.Json.Formatting.None));
                        Publish(obj, 0x415508, region);
                    }
                    if (publishSnapshots || !positions.ContainsKey(obj.EntityId)) PublishSnapshot(obj);
                    Vector3 position = obj.transform.position;
                    if (positions.TryGetValue(obj.EntityId, out var previous) && previous == position) continue;
                    connection.Send(new JObject
                    {
                        ["type"] = "transform", ["entity"] = obj.EntityId.Value,
                        ["position"] = ToAtlasNetPosition(position)
                    });
                    positions[obj.EntityId] = position;
                }
            }
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            bool wasServer = IsServer;
            manager.ReportTransportFailure(error.Message);
            if (IsRunning) Stop();
            if (wasServer && !Application.isEditor) Application.Quit(1);
        }
    }

    // AtlasNet 2D uses XY; Unity Soccer plays on XZ.
    private static JArray ToAtlasNetPosition(Vector3 position) => new(position.x, position.z, position.y);

    private void Handle(JObject request)
    {
        string type = request.Value<string>("type");
        if (type == "region")
        {
            ownsOrigin = request.Value<bool>("ownsOrigin");
            regionShape = request["shape"];
            return;
        }
        if (type == "commands")
        {
            foreach (JObject item in (JArray)request["items"])
            {
                var entity = new EntityId(item.Value<ulong>("entity"));
                if (!manager.TryGet(entity, out var obj) || !CanSimulate(obj)) continue;
                using var payload = new NetReader(((JArray)item["payload"]).ToObject<byte[]>());
                byte behaviour = payload.ReadByte();
                uint method = payload.ReadUInt();
                byte[] arguments = payload.ReadBytes();
                if (payload.HasRemaining) throw new InvalidOperationException("Trailing shard command data.");
                manager.DispatchBackendRpc(entity, behaviour, method, arguments, obj.OwnerSession, RpcDestination.Authority);
            }
            return;
        }

        var reply = new JObject { ["type"] = "reply", ["request"] = request["request"].DeepClone() };
        reply["ok"] = type switch
        {
            "spawn" => SpawnPlayer(request),
            "despawn" => TryDespawn(new EntityId(request.Value<ulong>("entity"))),
            "shutdown" => quitting = true,
            _ => migration.Handle(request, reply)
        };
        connection.Send(reply);
        if (!quitting) return;
        Stop();
        if (!Application.isEditor) Application.Quit();
    }

    private bool SpawnPlayer(JObject request)
    {
        var id = new EntityId(request.Value<ulong>("entity"));
        var owner = new SessionId(request.Value<ulong>("client"));
        byte[] admission = request["admission"]?.ToObject<byte[]>() ?? Array.Empty<byte>();
        bool spectator = admission.AsSpan().SequenceEqual(SpectatorAdmission);
        var prefab = spectator ? observerPrefab : manager.PlayerPrefab;
        if (!prefab || id.Value == 0 || owner.Value == 0) return false;
        if (manager.TryGet(id, out var existing)) return existing.OwnerSession == owner;
        foreach (var player in manager.SpawnedObjects)
            if (player.OwnerSession == owner) return false;
        var position = (JArray)request["position"];
        var placement = new Vector3(position[0].Value<float>(), position[2].Value<float>(), position[1].Value<float>());
        var spawn = spectator ? placement : PlayerSpawnPosition?.Invoke(owner) ?? placement;
        manager.CreateReplica(prefab, id, owner, spawn, Quaternion.identity, LocalWorkerId, 1);
        if (!spectator) SessionJoined?.Invoke(owner);
        return true;
    }

    private byte[] Capture(EntityId entity)
    {
        if (!manager.TryGet(entity, out var obj)) throw new InvalidOperationException("Missing source replica.");
        using var writer = new NetWriter();
        writer.Write(obj.PrefabId);
        writer.Write(obj.OwnerSession);
        writer.Write(checked(obj.AuthorityEpoch + 1));
        writer.WriteBytes(manager.CaptureMigrationState(entity));
        return writer.ToArray();
    }

    private void Restore(EntityId entity, byte[] snapshot)
    {
        using var reader = new NetReader(snapshot);
        string key = reader.ReadString();
        var owner = reader.ReadSessionId();
        uint epoch = reader.ReadUInt();
        byte[] state = reader.ReadBytes();
        if (reader.HasRemaining || !manager.TryGetRegisteredPrefab(key, out var prefab))
            throw new InvalidOperationException("Invalid migrated prefab or state.");
        manager.RestoreMigratedReplica(prefab, entity, owner, LocalWorkerId, epoch, state);
    }

    private void Freeze(EntityId id, bool value)
    {
        if (value) frozen.Add(id);
        else frozen.Remove(id);
        positions.Remove(id);
        if (manager.TryGet(id, out var obj)) obj.gameObject.SetActive(!value);
    }

    public bool CanSimulate(NetworkObject obj) => IsServer && obj.SimulationWorker == LocalWorkerId &&
        !frozen.Contains(obj.EntityId);

    public void Stop()
    {
        if (!IsRunning) return;
        bool wasServer = IsServer;
        connection?.Dispose();
        StopClient();
        connection = null;
        var objects = new List<NetworkObject>(manager.SpawnedObjects);
        foreach (var obj in objects) manager.RemoveReplica(obj.EntityId);
        positions.Clear();
        frozen.Clear();
        Stopped?.Invoke(wasServer, !wasServer);
    }

    public bool TryDespawn(EntityId id)
    {
        if (!manager.TryGet(id, out _)) return true;
        manager.RemoveReplica(id);
        positions.Remove(id);
        frozen.Remove(id);
        return true;
    }

    public void Despawn(NetworkObject obj) => TryDespawn(obj.EntityId);
    public TransportCapabilities GetTransportCapabilities(SessionId peer) => 0;
    public bool SetClientDebugRegionEnabled(NetworkObject ownedPlayer, bool enabled)
    {
        debugPlayer = enabled ? ownedPlayer : null;
        return !enabled || (IsClient && ownedPlayer && ownedPlayer.IsOwner);
    }
    public bool RequestLocalRegionForDebug() => false;
    public bool ShouldRenderWorkerCopyForDebug(NetworkObject obj) => false;

    // These require the native gameplay signal codec and coordinator entity registration.
    private static NotSupportedException Missing() => new("Native shard replication is not wired yet.");

    public void StartHost() => throw Missing();
    public void StartWorker() => StartServer();
    public NetworkObject Spawn(string prefabId, Vector3 position, Quaternion rotation, SessionId owner = default)
    {
        if (!manager.TryGetRegisteredPrefab(prefabId, out var prefab))
            throw new ArgumentException($"Unknown network prefab {prefabId}.", nameof(prefabId));
        return Spawn(prefab, position, rotation, owner);
    }

    public NetworkObject Spawn(NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default)
    {
        if (!IsServer || !prefab) throw new InvalidOperationException("An active shard and registered prefab are required.");
        if (!manager.TryGetRegisteredPrefab(prefab.PrefabId, out var registered) || registered != prefab)
            throw new ArgumentException("The prefab is not registered on this shard.", nameof(prefab));

        ulong registration = ++nextRegistration;
        connection.Send(new JObject
        {
            ["type"] = "register", ["registration"] = registration,
            ["position"] = ToAtlasNetPosition(position)
        });

        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (IsRunning && timer.ElapsedMilliseconds < 5500)
        {
            if (!connection.TryReceive(out var response))
            {
                System.Threading.Thread.Sleep(1);
                continue;
            }

            if (response.Value<string>("type") != "registration_reply")
            {
                Handle(response);
                continue;
            }

            if (response.Value<ulong>("registration") != registration)
                throw new InvalidOperationException("Unexpected AtlasNet registration reply.");
            var id = new EntityId(response.Value<ulong>("entity"));
            if (id.Value == 0)
                throw new InvalidOperationException(response.Value<string>("error") ?? "AtlasNet rejected entity registration.");
            return manager.CreateReplica(prefab, id, owner, position, rotation, LocalWorkerId, 1);
        }
        throw new TimeoutException("AtlasNet entity registration did not complete.");
    }
    public void RequestSpawn(NetworkObject source, NetworkObject prefab, Vector3 position, Quaternion rotation, SessionId owner = default) => throw Missing();
    public void ValidateNamedMessageRecipient(SessionId recipient) => throw Missing();
    public void SendNamedMessage(SessionId recipient, ulong messageId, byte[] payload, NetworkDelivery delivery) => throw Missing();
    public void DisconnectClient(SessionId session, string reason = null) => throw Missing();
    public void HideFrom(NetworkObject obj, SessionId session) => throw Missing();
    public void ShowTo(NetworkObject obj, SessionId session) => throw Missing();
    // Private adapter protocol, carried by AtlasNet client signals.
    internal const uint SnapshotSignal = 0x415501;
    internal const uint VariableSignal = 0x415502;
    internal const uint TransformSignal = 0x415503;
    internal const uint AnimatorSignal = 0x415504;
    internal const uint RpcSignal = 0x415505;

    private void PublishSnapshot(NetworkObject obj)
    {
        if (obj.OwnerSession.Value != 0)
        {
            using var admission = new NetWriter();
            admission.Write(obj.OwnerSession);
            Publish(obj, SessionSignal, admission, obj.OwnerSession);
        }
        if (observerPrefab && obj.PrefabId == observerPrefab.PrefabId) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(obj.PrefabId);
        writer.Write(obj.OwnerSession);
        writer.Write(obj.transform.position);
        writer.Write(obj.transform.rotation);
        writer.WriteBytes(manager.CaptureReplicaSnapshot(obj.EntityId, default));
        Publish(obj, SnapshotSignal, writer);
    }

    private static void WriteEntityHeader(NetWriter writer, NetworkObject obj)
    {
        writer.Write(obj.EntityId);
        writer.Write(obj.SimulationWorker);
        writer.Write(obj.AuthorityEpoch);
    }

    private void Publish(NetworkObject obj, uint signal, NetWriter writer, SessionId recipient = default)
    {
        byte[] bytes = writer.ToArray();
        if (bytes.Length > 4096)
            throw new InvalidOperationException($"AtlasNet signal {signal} exceeds the shard payload limit.");
        connection.Send(new JObject
        {
            ["type"] = "signal", ["entity"] = obj.EntityId.Value, ["signal"] = signal,
            ["recipient"] = recipient.Value, ["payload"] = JArray.FromObject(BytesAsIntegers(bytes))
        });
    }

    private static int[] BytesAsIntegers(byte[] bytes)
    {
        // JSON.NET normally encodes a byte array as a base64 string.
        return Array.ConvertAll(bytes, value => (int)value);
    }

    public void SendVariable(NetworkBehaviour behaviour, ushort id, INetworkVariable value)
    {
        var obj = behaviour.NetworkObject;
        if (!CanSimulate(obj)) return;
        var recipient = value.ReadPermission == NetworkVariableReadPermission.Owner ? obj.OwnerSession : default;
        if (value.ReadPermission == NetworkVariableReadPermission.Owner && recipient.Value == 0) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(behaviour.BehaviourIndex);
        writer.Write(id);
        using var content = new NetWriter();
        value.Write(content);
        writer.WriteBytes(content.ToArray());
        Publish(obj, VariableSignal, writer, recipient);
    }

    public void SendRpc(NetworkBehaviour behaviour, RpcDestination destination, SessionId target, uint method, Action<NetWriter> write)
    {
        var obj = behaviour.NetworkObject;
        if (IsClient)
        {
            if (destination == RpcDestination.Authority && obj.IsOwner)
                SendClientCommand(behaviour.BehaviourIndex, method, write);
            return;
        }
        if (!CanSimulate(obj) || destination == RpcDestination.Authority) return;
        if (destination == RpcDestination.Target && target.Value == 0) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(behaviour.BehaviourIndex);
        writer.Write(method);
        writer.Write((byte)destination);
        using var content = new NetWriter();
        write?.Invoke(content);
        writer.WriteBytes(content.ToArray());
        Publish(obj, RpcSignal, writer, destination == RpcDestination.Target ? target : default);
    }

    public void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write)
    {
        if (!source || !target || source.Manager != manager || target.NetworkManager != manager ||
            !CanSimulate(source) || !CanSimulate(target.NetworkObject))
            throw new InvalidOperationException("The interaction requires an authoritative source and target on this shard.");

        using var payload = new NetWriter();
        write(payload);
        manager.DispatchBackendRpc(target.NetworkObject.EntityId, target.BehaviourIndex, method,
            payload.ToArray(), default, RpcDestination.Authority);
    }

    public void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation)
    {
        var obj = component.NetworkObject;
        if (!CanSimulate(obj)) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(component.BehaviourIndex);
        writer.Write(flags);
        writer.Write(position);
        writer.Write(rotation);
        Publish(obj, TransformSignal, writer);
    }

    public void SendAnimator(NetworkAnimator component, byte[] payload)
    {
        var obj = component.NetworkObject;
        if (!CanSimulate(obj)) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(component.BehaviourIndex);
        writer.WriteBytes(payload);
        Publish(obj, AnimatorSignal, writer);
    }
}
