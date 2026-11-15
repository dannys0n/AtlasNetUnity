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
    private const uint SessionSignal = 0x415506;

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
                        if (NativeClient.anu_send(handle, packet, (uint)packet.Length) != NativeClient.Result.Ok)
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
    private readonly System.Collections.Generic.HashSet<EntityId> removedEntities = new();

    public float TickInterpolation => 1f;

    public bool TryGetPeerMetrics(SessionId session, out long sent, out float ping)
    {
        sent = BytesSent;
        ping = -1;
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
        DebugRegions.Clear();
        LocalSession = default;
    }

    private void PollClient(float deltaTime)
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        client.PollWebSocket();
#endif
        if (client.Error != null) throw new IOException(client.Error);
        for (int i = 0; i < 128 && client.Incoming.TryDequeue(out var packet); i++)
            ReceiveClientSignal(packet);

        float interval = 1f / manager.TickRate;
        accumulated = Mathf.Min(accumulated + deltaTime, interval * 4);
        while (LocalSession.Value != 0 && accumulated >= interval)
        {
            accumulated -= interval;
            Tick++;
            manager.TickBackendReplicas();
        }
    }

    private ulong nextCommand;

    private void SendClientCommand(byte behaviour, uint method, Action<NetWriter> write)
    {
        using var content = new NetWriter();
        write?.Invoke(content);
        using var payload = new NetWriter();
        payload.Write((byte)0); // Reliable
        payload.Write((byte)0); // Ordered
        payload.Write((byte)1); // Immediate
        payload.Write(0u);     // Default stream
        payload.Write(++nextCommand);
        payload.Write(behaviour);
        payload.Write(method);
        payload.WriteBytes(content.ToArray());
        if (client.Outgoing.Count >= 128) throw new IOException("AtlasNet client command queue is full.");
        client.Outgoing.Enqueue(EncodeClientPacket(12, 1, payload.ToArray()));
    }

    private static byte[] EncodeClientPacket(ushort instruction, uint channel, byte[] body)
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
        reader.ReadUInt();
        if (reader.ReadUInt() != packet.Length - HeaderSize - 12)
            throw new IOException("Invalid AtlasNet signal size.");
        if (signal == 0x415508)
        {
            ulong source = reader.ReadULong();
            var shape = Newtonsoft.Json.Linq.JToken.Parse(reader.ReadString());
            if (shape is Newtonsoft.Json.Linq.JObject region) DebugRegions[source] = region;
            else DebugRegions.Remove(source);
            return;
        }
        if (signal == SessionSignal)
        {
            if (LocalSession.Value != 0) return;
            LocalSession = reader.ReadSessionId();
            SessionJoined?.Invoke(LocalSession);
            return;
        }

        if (LocalSession.Value != 0 && signal == 0x415507)
        {
            var removed = reader.ReadEntityId();
            removedEntities.Add(removed);
            manager.RemoveReplica(removed);
            return;
        }
        if (LocalSession.Value == 0 || signal < SnapshotSignal || signal > RpcSignal) return;
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
            var position = reader.ReadVector3();
            var rotation = reader.ReadQuaternion();
            if (exists)
            {
                if (epoch == replica.AuthorityEpoch) return;
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

        if (!exists || epoch != replica.AuthorityEpoch) return;
        byte behaviour = reader.ReadByte();
        switch (signal)
        {
            case VariableSignal:
                manager.DispatchBackendVariable(entity, behaviour, reader.ReadUShort(), reader.ReadBytes());
                break;
            case TransformSignal:
                manager.DispatchBackendTransform(entity, behaviour, reader.ReadByte(), reader.ReadVector3(), reader.ReadQuaternion());
                break;
            case AnimatorSignal:
                manager.DispatchBackendAnimator(entity, behaviour, reader.ReadBytes());
                break;
            case RpcSignal:
                uint method = reader.ReadUInt();
                var destination = (RpcDestination)reader.ReadByte();
                manager.DispatchBackendRpc(entity, behaviour, method, reader.ReadBytes(), default, destination, LocalSession);
                break;
        }
    }
}
