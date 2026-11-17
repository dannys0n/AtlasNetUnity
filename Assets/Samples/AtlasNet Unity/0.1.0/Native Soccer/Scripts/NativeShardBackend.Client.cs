using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using AtlasNet;
using UnityEngine;
using NativeClient = AtlasNet.Native.AtlasNetNativeClient;
using EntityId = AtlasNet.EntityId;

internal sealed partial class NativeShardBackend
{
    private const uint PacketMagic = 0x41544C43;
    private const int HeaderSize = 21;
    internal const uint SessionSignal = 0x415506;
    private const uint PingReplySignal = 0x415509;
    internal const byte AdapterBehaviour = byte.MaxValue;
    private const uint PingProbeMethod = 1;
    private const byte SessionSupportsPing = 1;
    private const byte SessionSupportsRemoteContact = 2;
    private const byte SessionSupportsScaleInterest = 4;
    private bool scaleInterestSupported;
    private readonly System.Collections.Generic.HashSet<EntityId> visibleScaleEntities = new();
    internal const uint RemoteBallContactMethod = 2;
    internal const uint LoadMoveMethod = 3;
    internal const uint ScalePoseMethod = 4;
    internal const uint ReadyMethod = 5;
    private bool remoteContactSupported;
    private double nextBallContactProbe;
    private const double PingInterval = 1;
    private const double PingTimeout = 4;
    private bool pingSupported;
    private uint nextPingId;
    private uint pendingPingId;
    private double pingSentAt;
    private double nextPingAt;
    private float roundTripMilliseconds = -1;

    private sealed partial class ClientConnection
    {
        public readonly ConcurrentQueue<byte[]> Incoming = new();
        public readonly ConcurrentQueue<byte[]> Outgoing = new();
        public volatile bool Stopping;
        public volatile string Error;
        public long BytesSent;

#if !UNITY_WEBGL || UNITY_EDITOR
        public void Run(string endpoint, byte[] admission)
        {
            try
            {
                if (NativeClient.anu_abi_version() != 1) throw new IOException("Unexpected AtlasNet client ABI.");
                using var handle = NativeClient.anu_create();
                if (handle.IsInvalid || NativeClient.anu_connect(handle, endpoint, admission, (uint)admission.Length) != NativeClient.Result.Ok)
                    throw new IOException("AtlasNet ingress connection failed.");

                var buffer = new byte[65536];
                while (!Stopping)
                {
                    while (!Stopping && Outgoing.TryDequeue(out byte[] packet))
                    {
                        if (NativeClient.anu_send(handle, packet, (uint)packet.Length) == NativeClient.Result.Error)
                            throw new IOException("AtlasNet command send failed.");
                        Interlocked.Add(ref BytesSent, packet.Length);
                    }
                    var result = NativeClient.anu_receive(handle, buffer, (uint)buffer.Length, out uint size);
                    if (result == NativeClient.Result.Empty) { Thread.Sleep(2); continue; }
                    if (result != NativeClient.Result.Ok) throw new IOException("AtlasNet ingress receive failed.");
                    if (Incoming.Count >= 512) throw new IOException("AtlasNet client receive queue is full.");
                    Incoming.Enqueue(new ReadOnlySpan<byte>(buffer, 0, (int)size).ToArray());
                }
            }
            catch (Exception error) { Error = error.Message; }
        }
#endif
    }

    private ClientConnection client;
    internal static readonly System.Collections.Generic.Dictionary<ulong, Newtonsoft.Json.Linq.JObject> DebugRegions = new();
    private readonly System.Collections.Generic.Dictionary<ulong, Newtonsoft.Json.Linq.JObject> pendingRegions = new();
    private readonly System.Collections.Generic.Dictionary<ulong, double> regionLastSeen = new();
    private readonly System.Collections.Generic.List<ulong> expiredRegions = new();
    private ulong regionGeneration;
    private double regionApplyAt;

    private readonly System.Collections.Generic.HashSet<EntityId> removedEntities = new();
    private readonly System.Collections.Generic.Dictionary<(EntityId, uint), uint> streamInstances = new();
    private readonly System.Collections.Generic.Dictionary<EntityId,
        System.Collections.Generic.Dictionary<(byte, uint), (uint Epoch, uint Sequence)>> rpcStreams = new();
    private readonly System.Collections.Generic.Dictionary<EntityId, double> lastTransformAt = new();

    public float TickInterpolation => 1f;

    public bool TryGetPeerMetrics(SessionId session, out long sent, out float ping)
    {
        sent = BytesSent;
        ping = IsClient && session.Value == 0 ? roundTripMilliseconds : -1;
        return IsClient && LocalSession.Value != 0 && session.Value == 0;
    }

    public void StartClient()
    {
        if (IsRunning) throw new InvalidOperationException("An AtlasNet session is already running.");
        ValidateRegistry();
        var transport = new ClientConnection();
        byte[] admission = manager.ConnectionData ?? Array.Empty<byte>();

        Tick = 0;
        accumulated = 0;
        nextCommand = 0;
#if UNITY_WEBGL && !UNITY_EDITOR
        transport.OpenWebSocket(manager.Address, manager.Port, admission);
#else
        string endpoint = $"{manager.Address}:{manager.Port}";
        new Thread(() => transport.Run(endpoint, admission)) { IsBackground = true, Name = "AtlasNet native client" }.Start();
#endif
        client = transport;
    }

    private void StopClient()
    {
        if (client != null)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            client.CloseWebSocket();
#else
            client.Stopping = true;
#endif
        }
        client = null;
        removedEntities.Clear();
        visibleScaleEntities.Clear();
        scaleInterestSupported = false;
        streamInstances.Clear();
        rpcStreams.Clear();
        lastTransformAt.Clear();
        DebugRegions.Clear();
        pendingRegions.Clear();
        regionLastSeen.Clear();
        regionGeneration = 0;
        regionApplyAt = 0;
        LocalSession = default;
        pingSupported = false;
        pendingPingId = 0;
        nextPingAt = 0;
        nextBallContactProbe = 0;
        remoteContactSupported = false;
        roundTripMilliseconds = -1;
    }

    private void PollClient(float deltaTime)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        client.PollWebSocket();
#endif
        if (client.Error != null) throw new IOException(client.Error);
        for (int i = 0; i < 128 && client.Incoming.TryDequeue(out var packet); i++)
            ReceiveClientSignal(packet);

        double now = Time.realtimeSinceStartupAsDouble;
        UpdateDebugRegions(now);
        SamplePing(now);
        ProbeRemoteBallContact(now);

        float interval = 1f / manager.TickRate;
        accumulated = Mathf.Min(accumulated + deltaTime, interval * 4);
        while (LocalSession.Value != 0 && accumulated >= interval)
        {
            accumulated -= interval;
            Tick++;
            manager.TickBackendReplicas();
        }
    }

    private void ProbeRemoteBallContact(double now)
    {
        if (!remoteContactSupported || now < nextBallContactProbe) return;
        nextBallContactProbe = now + .08;
        AtlasNet.Samples.Soccer.SoccerBall ball = null;
        NetworkObject player = null;
        foreach (var obj in manager.SpawnedObjects)
        {
            if (obj.IsOwner && obj.TryGetComponent<AtlasNet.Samples.Soccer.SoccerPlayer>(out _))
                player = obj;
            if (obj.TryGetComponent(out AtlasNet.Samples.Soccer.SoccerBall candidate)) ball = candidate;
        }
        if (!player || !ball || player.SimulationWorker == ball.NetworkObject.SimulationWorker ||
            !player.TryGetComponent(out CapsuleCollider capsule)) return;

        if (!AtlasNet.Samples.Soccer.SoccerBall.TryGetContactCapsule(capsule,
                out Vector3 center, out float halfSegment, out float radius)) return;
        // The local player's collider can be disabled for physics on a native
        // replica. Query its geometry directly and retry while contact is plausible.
        if (!ball.IsWithinContactReach(center, halfSegment, radius, .65f) ||
            client.Outgoing.Count >= 64) return;
        SendClientCommand(AdapterBehaviour, RemoteBallContactMethod, writer => writer.Write(ball.NetworkObject.EntityId.Value));
    }

    private void SamplePing(double now)
    {
        if (!pingSupported || LocalSession.Value == 0) return;
        if (pendingPingId != 0)
        {
            if (now - pingSentAt < PingTimeout) return;
            pendingPingId = 0;
            roundTripMilliseconds = -1;
        }
        if (now < nextPingAt || !client.Outgoing.IsEmpty) return;

        if (++nextPingId == 0) nextPingId = 1;
        pendingPingId = nextPingId;
        pingSentAt = now;
        nextPingAt = now + PingInterval;
        using var contents = new NetWriter();
        contents.Write(pendingPingId);
        QueueClientCommand(AdapterBehaviour, PingProbeMethod, contents.ToArray());
    }

    private void ReceivePingReply(NetReader reader)
    {
        if (pendingPingId == 0 || reader.ReadUInt() != pendingPingId) return;
        pendingPingId = 0;
        double elapsed = Time.realtimeSinceStartupAsDouble - pingSentAt;
        if (elapsed > PingTimeout)
        {
            roundTripMilliseconds = -1;
            return;
        }
        float sample = (float)(elapsed * 1000);
        roundTripMilliseconds = roundTripMilliseconds < 0 ? sample :
            Mathf.Lerp(roundTripMilliseconds, sample, .25f);
    }

    private ulong nextCommand;

    private void SendClientCommand(byte behaviour, uint method, Action<NetWriter> write,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        using var content = new NetWriter();
        write?.Invoke(content);
        QueueClientCommand(behaviour, method, content.ToArray(), delivery);
    }

    private void QueueClientCommand(byte behaviour, uint method, byte[] contents,
        NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
    {
        var packet = EncodeGameplayCommand(behaviour, method, contents, ++nextCommand, delivery);
        if (client.Outgoing.Count >= 128)
        {
            if (delivery != NetworkDelivery.ReliableOrdered) return;
            throw new IOException("AtlasNet client command queue is full.");
        }
        client.Outgoing.Enqueue(packet);
    }

    internal static byte[] EncodeGameplayCommand(byte behaviour, uint method, byte[] contents,
        ulong sequence, NetworkDelivery delivery)
    {
        bool reliable = delivery == NetworkDelivery.ReliableOrdered;
        using var payload = new NetWriter();
        payload.Write(reliable ? (byte)0 : (byte)1);
        payload.Write((byte)delivery);
        payload.Write(reliable ? (byte)1 : (byte)0);
        payload.Write(reliable ? 0u : method);
        payload.Write(sequence);
        payload.Write(behaviour);
        payload.Write(method);
        payload.WriteBytes(contents);
        return EncodeClientPacket(12, 1, payload.ToArray());
    }

    internal static byte[] EncodeClientPacket(ushort instruction, uint channel, byte[] body)
    {
        using var writer = new NetWriter();
        writer.Write(PacketMagic);
        writer.Write((byte)1);
        writer.Write(channel);
        writer.Write((ushort)0);
        writer.Write(instruction);
        writer.Write(0u);
        writer.Write((uint)body.Length);
        writer.WriteRaw(body);
        byte[] packet = writer.ToArray();
        uint crc = uint.MaxValue;
        foreach (byte value in packet)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        }
        crc = ~crc;
        for (int i = 0; i < 4; i++) packet[13 + i] = (byte)(crc >> (i * 8));
        return packet;
    }

    private void ReceiveClientSignal(byte[] packet)
    {
        using var reader = new NetReader(packet);
        if (packet.Length < HeaderSize || reader.ReadUInt() != PacketMagic || reader.ReadByte() != 1)
            throw new IOException("Invalid AtlasNet packet header.");
        reader.ReadUInt();
        reader.ReadUShort();
        ushort instruction = reader.ReadUShort();
        reader.ReadUInt();
        if (reader.ReadUInt() != packet.Length - HeaderSize)
            throw new IOException("Invalid AtlasNet packet size.");
        if (instruction != 7) return;
        uint signal = reader.ReadUInt();
        uint instance = reader.ReadUInt();
        if (reader.ReadUInt() != packet.Length - HeaderSize - 12)
            throw new IOException("Invalid AtlasNet signal size.");
        if (signal == ScalePoseBatchSignal)
        {
            if (LocalSession.Value != 0) ReceiveScalePoseBatch(reader);
            return;
        }
        if (signal == PingReplySignal)
        {
            if (packet.Length == HeaderSize + 12 + sizeof(uint)) ReceivePingReply(reader);
            return;
        }
        if (signal == 0x415508)
        {
            ulong source = reader.ReadULong();
            var shape = Newtonsoft.Json.Linq.JToken.Parse(reader.ReadString());
            if (shape is Newtonsoft.Json.Linq.JObject region)
            {
                ulong generation = region.Value<ulong>("generation");
                if (generation < regionGeneration) return;
                double now = Time.realtimeSinceStartupAsDouble;
                if (generation > regionGeneration)
                {
                    regionGeneration = generation;
                    pendingRegions.Clear();
                    regionLastSeen.Clear();
                    regionApplyAt = now + .15;
                }
                regionLastSeen[source] = now;
                if (regionApplyAt > 0) pendingRegions[source] = region;
                else DebugRegions[source] = region;
            }
            else
            {
                pendingRegions.Remove(source);
                regionLastSeen.Remove(source);
                DebugRegions.Remove(source);
            }
            return;
        }
        if (signal == 0x415509)
        {
            if (LocalSession.Value != 0 && !scaleInterestSupported) return;
            var observed = reader.ReadEntityId();
            if (reader.ReadByte() != 0) visibleScaleEntities.Add(observed);
            else
            {
                visibleScaleEntities.Remove(observed);
                rpcStreams.Remove(observed);
                streamInstances.Remove((observed, TransformSignal));
                streamInstances.Remove((observed, AnimatorSignal));
                lastTransformAt.Remove(observed);
                if (manager.TryGet(observed, out var hidden) && !hidden.IsOwner)
                    manager.RemoveReplica(observed);
            }
            return;
        }
        if (signal == SessionSignal)
        {
            if (LocalSession.Value != 0) return;
            LocalSession = reader.ReadSessionId();
            byte capabilities = reader.HasRemaining ? reader.ReadByte() : (byte)0;
            scaleInterestSupported = (capabilities & SessionSupportsScaleInterest) != 0;
            pingSupported = (capabilities & SessionSupportsPing) != 0;
            remoteContactSupported = (capabilities & SessionSupportsRemoteContact) != 0;
            QueueClientCommand(AdapterBehaviour, ReadyMethod, Array.Empty<byte>());
            SessionJoined?.Invoke(LocalSession);
            return;
        }

        if (LocalSession.Value != 0 && signal == 0x415507)
        {
            var removed = reader.ReadEntityId();
            removedEntities.Add(removed);
            visibleScaleEntities.Remove(removed);
            rpcStreams.Remove(removed);
            streamInstances.Remove((removed, TransformSignal));
            streamInstances.Remove((removed, AnimatorSignal));
            lastTransformAt.Remove(removed);
            manager.RemoveReplica(removed);
            return;
        }
        if (LocalSession.Value == 0 || (signal < SnapshotSignal || signal > RpcSignal) && signal != SequencedRpcSignal) return;
        var entity = reader.ReadEntityId();
        if (removedEntities.Contains(entity)) return;
        ulong worker = reader.ReadULong();
        uint epoch = reader.ReadUInt();
        bool exists = manager.TryGet(entity, out var replica);
        if (exists && (replica.AuthorityEpoch > epoch ||
                       (replica.AuthorityEpoch == epoch && replica.SimulationWorker != worker))) return;
        if (signal == SnapshotSignal)
        {

            var key = reader.ReadString();
            var owner = reader.ReadSessionId();
            if (scaleInterestSupported && owner != LocalSession && !visibleScaleEntities.Contains(entity)) return;
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            if (exists)
            {
                if (epoch == replica.AuthorityEpoch &&
                    lastTransformAt.TryGetValue(entity, out double last) &&
                    Time.unscaledTimeAsDouble - last < 1.1) return;
                if (epoch != replica.AuthorityEpoch)
                {
                    streamInstances.Remove((entity, TransformSignal));
                    streamInstances.Remove((entity, AnimatorSignal));
                }
                using var restored = new NetReader(reader.ReadBytes());
                manager.ApplyBackendReplicaSnapshot(entity, restored);
                manager.SetBackendReplicaAuthority(entity, worker, epoch);
                if (replica.TryGetComponent(out NetworkTransform transform))
                    manager.DispatchBackendTransform(entity, transform.BehaviourIndex, 3, position, rotation);
                return;
            }
            using var snapshot = new NetReader(reader.ReadBytes());
            if (!manager.TryGetRegisteredPrefab(key, out var prefab))
                throw new IOException($"AtlasNet server selected unknown prefab {key}.");
            manager.CreateReplica(prefab, entity, owner, position, rotation, worker, epoch, snapshot);
            return;
        }

        if (!exists) return;
        if (epoch != replica.AuthorityEpoch)
        {
            // Scale poses carry their authoritative worker and epoch. An
            // existing cube can resume movement before the full snapshot arrives.
            if (signal != TransformSignal || !replica.TryGetComponent<ScaleMotion>(out _)) return;
            streamInstances.Remove((entity, TransformSignal));
            lastTransformAt.Remove(entity);
            manager.SetBackendReplicaAuthority(entity, worker, epoch);
        }
        byte behaviour = reader.ReadByte();
        switch (signal)
        {
            case VariableSignal:
                manager.DispatchBackendVariable(entity, behaviour, reader.ReadUShort(), reader.ReadBytes());
                break;
            case TransformSignal:
                if (!AcceptStreamInstance(entity, signal, instance)) break;
                lastTransformAt[entity] = Time.unscaledTimeAsDouble;
                byte flags = reader.ReadByte();
                Vector3 position = reader.ReadVector3();
                Quaternion rotation = reader.ReadQuaternion();
                manager.DispatchBackendTransform(entity, behaviour, flags, position, rotation,
                    (flags & 4) != 0 ? reader.ReadVector3() : default);
                break;
            case AnimatorSignal:
                byte[] animation = reader.ReadBytes();
                bool trigger = animation.Length == 8 && animation[6] == (byte)AnimatorControllerParameterType.Trigger;
                if (trigger || AcceptStreamInstance(entity, signal, instance))
                    manager.DispatchBackendAnimator(entity, behaviour, animation);
                break;
            case SequencedRpcSignal:
            case RpcSignal:
                uint method = reader.ReadUInt();
                var destination = (RpcDestination)reader.ReadByte();
                if (signal == SequencedRpcSignal)
                {
                    uint sequence = reader.ReadUInt();
                    if (!rpcStreams.TryGetValue(entity, out var streams))
                        rpcStreams.Add(entity, streams = new());
                    var stream = (behaviour, method);
                    if (streams.TryGetValue(stream, out var previous) && previous.Epoch == epoch &&
                        unchecked((int)(sequence - previous.Sequence)) <= 0) break;
                    streams[stream] = (epoch, sequence);
                }
                manager.DispatchBackendRpc(entity, behaviour, method, reader.ReadBytes(), default, destination, LocalSession);
                break;
        }
    }
    private void ReceiveScalePoseBatch(NetReader reader)
    {
        ulong worker = reader.ReadULong();
        uint tick = reader.ReadUInt();
        int count = reader.ReadByte();
        if (count == 0 || count > ScalePosesPerPacket) throw new IOException("Invalid Scale pose count.");
        for (int i = 0; i < count; ++i)
        {
            EntityId entity = reader.ReadEntityId();
            uint epoch = reader.ReadUInt();
            byte behaviour = reader.ReadByte();
            byte flags = reader.ReadByte();
            Vector3 position = reader.ReadVector3();
            Quaternion rotation = reader.ReadQuaternion();
            Vector3 scale = (flags & 4) != 0 ? reader.ReadVector3() : default;
            if (removedEntities.Contains(entity) ||
                !manager.TryGet(entity, out var replica) || epoch < replica.AuthorityEpoch) continue;
            if (epoch == replica.AuthorityEpoch)
            {
                if (worker != replica.SimulationWorker) continue;
            }
            else
            {
                if (!replica.TryGetComponent<ScaleMotion>(out _)) continue;
                streamInstances.Remove((entity, TransformSignal));
                manager.SetBackendReplicaAuthority(entity, worker, epoch);
            }
            if (!AcceptStreamInstance(entity, TransformSignal, tick)) continue;
            lastTransformAt[entity] = Time.unscaledTimeAsDouble;
            manager.DispatchBackendTransform(entity, behaviour, flags, position, rotation, scale);
        }
        if (reader.HasRemaining) throw new IOException("Unexpected Scale pose batch data.");
    }

    private void UpdateDebugRegions(double now)
    {
        if (regionApplyAt > 0)
        {
            if (now < regionApplyAt) return;
            DebugRegions.Clear();
            foreach (var region in pendingRegions) DebugRegions.Add(region.Key, region.Value);
            pendingRegions.Clear();
            regionApplyAt = 0;
        }

        expiredRegions.Clear();
        foreach (var region in regionLastSeen)
            if (now - region.Value > 3) expiredRegions.Add(region.Key);
        foreach (ulong worker in expiredRegions)
        {
            regionLastSeen.Remove(worker);
            DebugRegions.Remove(worker);
        }
    }

    private bool AcceptStreamInstance(EntityId entity, uint signal, uint instance)
    {
        var stream = (entity, signal);
        if (streamInstances.TryGetValue(stream, out uint previous) &&
            unchecked((int)(instance - previous)) <= 0) return false;
        streamInstances[stream] = instance;
        return true;
    }
}
