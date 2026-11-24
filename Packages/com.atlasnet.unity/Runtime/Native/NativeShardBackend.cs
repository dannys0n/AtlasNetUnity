using System;
using System.Collections.Generic;
using AtlasNet;
using Newtonsoft.Json.Linq;
using UnityEngine;
using EntityId = AtlasNet.EntityId;

public partial class NativeShardBackend : IWorldBackend, IWorldBackendTelemetry
{
    public static readonly byte[] SpectatorAdmission = { 0x41, 0x54, 0x4C, 0x53, 1 };
    protected readonly NetworkManager manager;
    private readonly NetworkObject observerPrefab;
    private readonly HashSet<EntityId> frozen = new();
    private readonly Dictionary<EntityId, Vector3> positions = new();
    private readonly HashSet<EntityId> awaitingFirstClientCommand = new();
    private readonly UnityShardMigration migration;
    private UnityShardConnection connection;
    private float accumulated;
    private bool quitting;
    private bool ownsOrigin;
    private JToken regionShape;
    private bool regionChanged;
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
    public bool IsWorker => IsServer && !ownsOrigin;
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

    public static void BuildRegionOutline(JObject shape, List<Vector2> outline)
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
            Clip(outline, new Vector2((float)normal[0], (float)normal[z]),
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
                PublishScalePoseBatches();
                bool publishSnapshots = Tick >= nextSnapshotTick;
                if (publishSnapshots) nextSnapshotTick = Tick + (uint)manager.TickRate;
                if (regionChanged || (publishSnapshots && regionShape is JObject))
                {
                    using var region = new NetWriter();
                    region.Write(LocalWorkerId);
                    region.Write(regionShape.ToString(Newtonsoft.Json.Formatting.None));
                    connection.Send(new JObject
                    {
                        ["type"] = "region_signal",
                        ["payload"] = JArray.FromObject(BytesAsIntegers(region.ToArray()))
                    });
                    regionChanged = false;
                }

                foreach (var obj in manager.SpawnedObjects)
                {
                    if (!CanSimulate(obj)) continue;
                    uint tickRate = (uint)manager.TickRate;
                    bool snapshotDue = scaleInterestEnabled
                        ? Tick % tickRate == ScaleSendPhase(obj.EntityId, tickRate)
                        : publishSnapshots;
                    if (snapshotDue || !positions.ContainsKey(obj.EntityId)) PublishSnapshot(obj);
                    Vector3 position = obj.transform.position;
                    if (awaitingFirstClientCommand.Contains(obj.EntityId))
                    {
                        // Keep snapshots flowing while AtlasNet finishes registering the client actor.
                        positions[obj.EntityId] = position;
                        continue;
                    }
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
            regionShape = request["shape"] ?? JValue.CreateNull();
            regionChanged = true;
            return;
        }
        if (type == "authority_interaction")
        {
            var entity = new EntityId(request.Value<ulong>("entity"));
            if (manager.TryGet(entity, out var obj) && CanSimulate(obj))
            {
                byte behaviour = request.Value<byte>("behaviour");
                uint method = request.Value<uint>("method");
                byte[] arguments = request["payload"].ToObject<byte[]>();
                if (behaviour == AdapterBehaviour)
                    ReceiveSampleAuthorityCommand(obj, method, arguments);
                else
                    manager.DispatchBackendRpc(entity, behaviour, method, arguments, default, RpcDestination.Authority);
            }
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
                // AtlasNet forwards client commands only after registration and handshake completion.
                if (awaitingFirstClientCommand.Remove(entity))
                    positions.Remove(entity);
                if (behaviour == AdapterBehaviour)
                {
                    if (method == PingProbeMethod && arguments.Length == sizeof(uint) && obj.OwnerSession.Value != 0)
                    {
                        using var pong = new NetWriter();
                        pong.WriteRaw(arguments);
                        Publish(obj, PingReplySignal, pong, obj.OwnerSession);
                    }
                    ReceiveSampleClientCommand(obj, method, arguments);
                    continue;
                }
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
        awaitingFirstClientCommand.Add(id);
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
        OnReplicaRemoved(id);
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
        awaitingFirstClientCommand.Clear();
        OnStopped();
        frozen.Clear();
        Stopped?.Invoke(wasServer, !wasServer);
    }

    public bool TryDespawn(EntityId id)
    {
        if (!manager.TryGet(id, out var replica)) return true;
        var ownerSession = replica.OwnerSession.Value;
        var authorityEpoch = replica.AuthorityEpoch;
        try
        {
            manager.RemoveReplica(id);
        }
        catch (Exception error)
        {
            Debug.LogError($"[AtlasNet] Unity despawn failed for entity {id.Value}, owner session {ownerSession}, " +
                $"worker {LocalWorkerId}, authority epoch {authorityEpoch}: {error.GetType().Name}: {error.Message}");
            throw;
        }
        positions.Remove(id);
        awaitingFirstClientCommand.Remove(id);
        OnReplicaRemoved(id);
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

    public void StartHost() => throw new NotSupportedException("AtlasNet shards and clients start separately. Enable Editor Loopback to start a local host in Play Mode.");
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
    public const uint SnapshotSignal = 0x415501;
    public const uint VariableSignal = 0x415502;
    public const uint TransformSignal = 0x415503;
    public const uint AnimatorSignal = 0x415504;
    public const uint RpcSignal = 0x415505;
    public const uint SequencedRpcSignal = 0x41550B;
    private uint nextRpcSequence;

    private readonly bool scaleInterestEnabled = Environment.GetEnvironmentVariable("ATLASNET_SCALE_INTEREST") == "1";
    private const double ScaleInterestRadius = 24;
    public const uint ScalePoseBatchSignal = 0x41550A;
    private const float ScalePoseCellSize = 8f;
    private const int ScalePosesPerPacket = 16;
    private readonly Dictionary<Vector2Int, List<ScalePose>> scalePoseCells = new();

    private readonly struct ScalePose
    {
        public readonly NetworkObject Object;
        public readonly byte Behaviour;
        public readonly byte Flags;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public readonly Vector3 Scale;

        public ScalePose(NetworkObject obj, byte behaviour, byte flags, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            Object = obj;
            Behaviour = behaviour;
            Flags = flags;
            Position = position;
            Rotation = rotation;
            Scale = scale;
        }
    }

    private void QueueScalePose(ScalePose pose)
    {
        var cell = new Vector2Int(Mathf.FloorToInt(pose.Position.x / ScalePoseCellSize),
            Mathf.FloorToInt(pose.Position.z / ScalePoseCellSize));
        if (!scalePoseCells.TryGetValue(cell, out var poses))
            scalePoseCells.Add(cell, poses = new List<ScalePose>(ScalePosesPerPacket));
        poses.Add(pose);
    }

    private void PublishScalePoseBatches()
    {
        foreach (var entry in scalePoseCells)
        {
            var poses = entry.Value;
            if (poses.Count == 0) continue;
            var center = new Vector3((entry.Key.x + .5f) * ScalePoseCellSize, 0,
                (entry.Key.y + .5f) * ScalePoseCellSize);
            // Cover the original interest circle of every pose in this cell.
            double radius = ScaleInterestRadius + ScalePoseCellSize / Mathf.Sqrt(2);
            for (int start = 0; start < poses.Count; start += ScalePosesPerPacket)
            {
                int count = Mathf.Min(ScalePosesPerPacket, poses.Count - start);
                using var writer = new NetWriter();
                writer.Write(LocalWorkerId);
                writer.Write(Tick);
                writer.Write((byte)count);
                for (int i = start; i < start + count; ++i)
                {
                    var pose = poses[i];
                    writer.Write(pose.Object.EntityId);
                    writer.Write(pose.Object.AuthorityEpoch);
                    writer.Write(pose.Behaviour);
                    writer.Write(pose.Flags);
                    writer.Write(pose.Position);
                    writer.Write(pose.Rotation);
                    if ((pose.Flags & 4) != 0) writer.Write(pose.Scale);
                }
                connection.Send(new JObject
                {
                    ["type"] = "scale_pose_batch",
                    ["payload"] = JArray.FromObject(BytesAsIntegers(writer.ToArray())),
                    ["spatial_radius"] = radius,
                    ["spatial_center"] = ToAtlasNetPosition(center)
                });
            }
            poses.Clear();
        }
    }

    private static uint ScaleSendPhase(EntityId id, uint tickRate)
    {
        // Mix the time and counter bits so clients admitted together spread across ticks.
        ulong mixed = id.Value ^ (id.Value >> 13) ^ (id.Value >> 27);
        return (uint)(mixed % tickRate);
    }

    private bool IsObserver(NetworkObject obj) => observerPrefab && obj.PrefabId == observerPrefab.PrefabId;

    private void PublishSnapshot(NetworkObject obj)
    {
        if (obj.OwnerSession.Value != 0)
        {
            using var admission = new NetWriter();
            admission.Write(obj.OwnerSession);
            admission.Write((byte)(SessionSupportsPing | SessionSupportsRemoteContact |
                (scaleInterestEnabled && !IsObserver(obj) ? SessionSupportsScaleInterest : 0)));
            admission.Write(obj.EntityId);
            admission.Write(GetAdmissionTeam(obj));
            Publish(obj, SessionSignal, admission, obj.OwnerSession);
        }
        if (IsObserver(obj)) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(obj.PrefabId);
        writer.Write(obj.OwnerSession);
        writer.Write(obj.transform.position);
        writer.Write(obj.transform.rotation);
        writer.WriteBytes(manager.CaptureReplicaSnapshot(obj.EntityId, default));
        Publish(obj, SnapshotSignal, writer, spatialRadius: scaleInterestEnabled ? ScaleInterestRadius : 0);
    }

    private static void WriteEntityHeader(NetWriter writer, NetworkObject obj)
    {
        writer.Write(obj.EntityId);
        writer.Write(obj.SimulationWorker);
        writer.Write(obj.AuthorityEpoch);
    }

    private void Publish(NetworkObject obj, uint signal, NetWriter writer, SessionId recipient = default, bool stateStream = false, double spatialRadius = 0, bool transientRpc = false)
    {
        byte[] bytes = writer.ToArray();
        if (bytes.Length > 4096)
            throw new InvalidOperationException($"AtlasNet signal {signal} exceeds the shard payload limit.");
        connection.Send(new JObject
        {
            ["type"] = "signal", ["entity"] = obj.EntityId.Value, ["signal"] = signal,
            ["recipient"] = recipient.Value, ["payload"] = JArray.FromObject(BytesAsIntegers(bytes)),
            ["sequence"] = stateStream ? ((ulong)obj.AuthorityEpoch << 32) | Tick : 0,
            ["transient_rpc"] = transientRpc,
            ["spatial_radius"] = spatialRadius,
            ["spatial_center"] = spatialRadius > 0 ? ToAtlasNetPosition(obj.transform.position) : null
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
                SendClientCommand(behaviour.BehaviourIndex, method, write, behaviour.GetRpcDelivery(method));
            return;
        }
        if (!CanSimulate(obj) || destination == RpcDestination.Authority) return;
        if (destination == RpcDestination.Target && target.Value == 0) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(behaviour.BehaviourIndex);
        writer.Write(method);
        writer.Write((byte)destination);
        var delivery = behaviour.GetRpcDelivery(method);
        if (delivery == NetworkDelivery.UnreliableSequenced)
            writer.Write(++nextRpcSequence);
        using var content = new NetWriter();
        write?.Invoke(content);
        writer.WriteBytes(content.ToArray());
        Publish(obj, delivery == NetworkDelivery.UnreliableSequenced ? SequencedRpcSignal : RpcSignal,
            writer, destination == RpcDestination.Target ? target : default,
            transientRpc: delivery != NetworkDelivery.ReliableOrdered);
    }

    public void SendAuthorityInteraction(NetworkObject source, NetworkBehaviour target, uint method, Action<NetWriter> write)
    {
        if (!source || !target || source.Manager != manager || target.NetworkManager != manager ||
            !CanSimulate(source) || !target.IsSpawned)
            throw new InvalidOperationException("The interaction requires a local authoritative source and a spawned target.");

        var targetObject = target.NetworkObject;
        using var payload = new NetWriter();
        write(payload);
        var arguments = payload.ToArray();
        if (CanSimulate(targetObject))
        {
            manager.DispatchBackendRpc(targetObject.EntityId, target.BehaviourIndex, method,
                arguments, default, RpcDestination.Authority);
            return;
        }

        SendRemoteAuthorityInteraction(source, targetObject.EntityId, target.BehaviourIndex, method, arguments);
    }

    protected void SendRemoteAuthorityInteraction(NetworkObject source, EntityId target, byte behaviour, uint method, byte[] arguments)
    {
        if (arguments.Length > 4096)
            throw new InvalidOperationException("Native authority interaction arguments exceed 4096 bytes.");
        connection.Send(new JObject
        {
            ["type"] = "authority_interaction",
            ["entity"] = source.EntityId.Value,
            ["target"] = target.Value,
            ["behaviour"] = behaviour,
            ["method"] = method,
            ["payload"] = new JArray(Array.ConvertAll(arguments, value => (int)value))
        });
    }

    public void SendTransform(NetworkTransform component, byte flags, Vector3 position, Quaternion rotation, Vector3 scale)
    {
        var obj = component.NetworkObject;
        if (!CanSimulate(obj)) return;
        bool scaleMotion = UsesSpatialPoseBatch(obj);
        const uint observerRate = 20;
        uint tickRate = (uint)manager.TickRate;
        ulong phasedTick = (ulong)Tick + ScaleSendPhase(obj.EntityId, tickRate);
        if (scaleMotion && phasedTick * observerRate / tickRate ==
            (phasedTick - 1) * observerRate / tickRate) return;
        if (scaleMotion && scaleInterestEnabled)
        {
            QueueScalePose(new ScalePose(obj, component.BehaviourIndex, flags, position, rotation, scale));
            return;
        }
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(component.BehaviourIndex);
        writer.Write(flags);
        writer.Write(position);
        writer.Write(rotation);
        if ((flags & 4) != 0) writer.Write(scale);
        Publish(obj, TransformSignal, writer, stateStream: true, spatialRadius: scaleMotion ? ScaleInterestRadius : 0);
    }

    public void SendAnimator(NetworkAnimator component, byte[] payload)
    {
        var obj = component.NetworkObject;
        if (!CanSimulate(obj)) return;
        using var writer = new NetWriter();
        WriteEntityHeader(writer, obj);
        writer.Write(component.BehaviourIndex);
        // Trigger payloads are discrete events. Replicate continuous state as a
        // complete sample so a newer packet can replace an older one safely.
        bool trigger = payload.Length == 8 && payload[6] == (byte)AnimatorControllerParameterType.Trigger;
        writer.WriteBytes(trigger ? payload : component.CaptureState());
        Publish(obj, AnimatorSignal, writer, stateStream: !trigger);
    }

    protected virtual bool UsesSpatialPoseBatch(NetworkObject obj) => false;
    protected virtual byte GetAdmissionTeam(NetworkObject obj) => 0;
    protected virtual void ReceiveSampleAuthorityCommand(NetworkObject obj, uint method, byte[] arguments) { }
    protected virtual void ReceiveSampleClientCommand(NetworkObject obj, uint method, byte[] arguments) { }
    protected virtual void ProbeSampleContact() { }
    protected virtual void OnReplicaRemoved(EntityId id) { }
    protected virtual void OnStopped() { }

    private static void Clip(System.Collections.Generic.List<Vector2> polygon, Vector2 normal, float offset)
    {
        if (polygon.Count == 0) return;
        var input = polygon.ToArray();
        polygon.Clear();
        var previous = input[input.Length - 1];
        float distance = Vector2.Dot(normal, previous) - offset;
        foreach (var current in input)
        {
            float next = Vector2.Dot(normal, current) - offset;
            if ((distance <= 0) != (next <= 0))
                polygon.Add(Vector2.Lerp(previous, current, distance / (distance - next)));
            if (next <= 0) polygon.Add(current);
            previous = current;
            distance = next;
        }
    }
}
