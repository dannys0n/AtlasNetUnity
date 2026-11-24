#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public partial class NativeShardBackend
{
    private sealed partial class ClientConnection
    {
        private const int MaxPayloadSize = 16 * 1024 * 1024;
        private readonly byte[] header = new byte[HeaderSize];
        private byte[] nextPacket;
        private int received;
        private bool requestedHandshake;
        private bool completedHandshake;
        private double handshakeDeadline;
        private byte[] admission;

        [DllImport("__Internal")] private static extern int AtlasNetWebSocketOpen(string url);
        [DllImport("__Internal")] private static extern void AtlasNetWebSocketClose();
        [DllImport("__Internal")] private static extern int AtlasNetWebSocketStatus();
        [DllImport("__Internal")] private static extern int AtlasNetWebSocketRead(
            [In, Out] byte[] buffer, int offset, int count);
        [DllImport("__Internal")] private static extern int AtlasNetWebSocketSend(byte[] buffer, int count);

        public void OpenWebSocket(string address, int port, byte[] admission)
        {
            if (!address.Contains("://")) address = "ws://" + address;
            if (!Uri.TryCreate(address, UriKind.Absolute, out Uri endpoint) ||
                (endpoint.Scheme != "http" && endpoint.Scheme != "https" &&
                 endpoint.Scheme != "ws" && endpoint.Scheme != "wss"))
                throw new ArgumentException("Enter a WebSocket host or URL.");

            var url = new UriBuilder(endpoint)
            {
                Scheme = endpoint.Scheme == "https" || endpoint.Scheme == "wss" ? "wss" : "ws",
                Port = port
            };
            if (AtlasNetWebSocketOpen(url.Uri.AbsoluteUri) != 1)
                throw new IOException("Unable to open the AtlasNet WebSocket.");
            this.admission = admission;
            handshakeDeadline = Time.realtimeSinceStartupAsDouble + 15;
        }

        public void CloseWebSocket() => AtlasNetWebSocketClose();

        public void PollWebSocket()
        {
            int status = AtlasNetWebSocketStatus();
            if (status < 0) throw new IOException("AtlasNet WebSocket closed or could not connect.");
            if (!completedHandshake && Time.realtimeSinceStartupAsDouble >= handshakeDeadline)
                throw new IOException("AtlasNet WebSocket handshake timed out.");
            if (status == 0) return;

            if (!requestedHandshake)
            {
                if (!SendWebPacket(EncodeClientPacket(1, 0, admission))) return;
                requestedHandshake = true;
            }

            for (int i = 0; i < 128 && TryReadPacket(out byte[] packet); i++)
            {
                if (completedHandshake)
                {
                    Incoming.Enqueue(packet);
                    continue;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)) != 0 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(11)) != 2 ||
                    packet.Length != HeaderSize + 1)
                    throw new IOException("Invalid AtlasNet handshake response.");
                if (packet[HeaderSize] != 0)
                    throw new IOException($"AtlasNet rejected the handshake (status {packet[HeaderSize]}).");
                if (!SendWebPacket(EncodeClientPacket(3, 0, Array.Empty<byte>())))
                    throw new IOException("AtlasNet could not complete the WebSocket handshake.");
                completedHandshake = true;
            }

            while (completedHandshake && Outgoing.TryPeek(out byte[] outgoing) && SendWebPacket(outgoing))
                Outgoing.TryDequeue(out _);
        }

        private bool SendWebPacket(byte[] packet)
        {
            int result = AtlasNetWebSocketSend(packet, packet.Length);
            if (result < 0) throw new IOException("AtlasNet WebSocket send failed.");
            if (result == 0) return false;
            BytesSent += packet.Length;
            return true;
        }

        private bool TryReadPacket(out byte[] packet)
        {
            packet = null;
            if (nextPacket == null)
            {
                received += AtlasNetWebSocketRead(header, received, HeaderSize - received);
                if (received != HeaderSize) return false;

                uint payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(17));
                if (BinaryPrimitives.ReadUInt32LittleEndian(header) != PacketMagic || header[4] != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(9)) != 0 || payloadSize > MaxPayloadSize)
                    throw new IOException("Invalid AtlasNet WebSocket packet header.");
                nextPacket = new byte[HeaderSize + (int)payloadSize];
                header.CopyTo(nextPacket, 0);
            }

            received += AtlasNetWebSocketRead(nextPacket, received, nextPacket.Length - received);
            if (received != nextPacket.Length) return false;
            packet = nextPacket;
            nextPacket = null;
            received = 0;
            return true;
        }
    }
}
#endif
