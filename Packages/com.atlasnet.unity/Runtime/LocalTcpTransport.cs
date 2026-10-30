using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace AtlasNet
{
    internal sealed class LocalTcpTransport : IMessageTransport
    {
        private sealed class Peer
        {
            public SessionId Session;
            public TcpClient Client;
            public readonly List<byte> Buffer = new List<byte>(4096);
            public readonly byte[] Scratch = new byte[4096];
        }

        private const int MaxFrame = 1024 * 1024;
        private readonly Dictionary<SessionId, Peer> peers = new Dictionary<SessionId, Peer>();
        private readonly bool server;
        private TcpListener listener;
        private Peer upstream;
        private ulong nextSession = 2; // Session 1 is reserved for a local host player.

        public event Action<SessionId> Connected;
        public event Action<SessionId> Disconnected;
        public event Action<SessionId, byte[]> Received;
        public int PeerCount => server ? peers.Count : (upstream == null ? 0 : 1);
        public IEnumerable<SessionId> Sessions => peers.Keys;
        public long BytesSent { get; private set; }

        public LocalTcpTransport(bool server, string address, int port, string bindAddress = "127.0.0.1")
        {
            this.server = server;
            if (server)
            {
                listener = new TcpListener(IPAddress.Parse(bindAddress), port);
                listener.Start();
            }
            else
            {
                var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                client.Connect(IPAddress.Parse(address), port);
                upstream = new Peer { Session = new SessionId(0), Client = client };
            }
        }

        public void Poll()
        {
            if (server)
            {
                while (listener.Pending())
                {
                    var client = listener.AcceptTcpClient();
                    client.NoDelay = true;
                    var peer = new Peer { Session = new SessionId(nextSession++), Client = client };
                    peers.Add(peer.Session, peer);
                    Connected?.Invoke(peer.Session);
                }

                var disconnected = new List<SessionId>();
                foreach (var peer in new List<Peer>(peers.Values))
                    if (!ReadAvailable(peer)) disconnected.Add(peer.Session);
                foreach (var session in disconnected)
                {
                    if(!peers.TryGetValue(session,out var peer))continue;
                    peer.Client.Close();
                    peers.Remove(session);
                    Disconnected?.Invoke(session);
                }
            }
            else if (upstream != null && !ReadAvailable(upstream))
            {
                upstream.Client.Close();
                upstream = null;
                Disconnected?.Invoke(new SessionId(0));
            }
        }

        private bool ReadAvailable(Peer peer)
        {
            try
            {
                var socket = peer.Client.Client;
                if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) return false;
                var stream = peer.Client.GetStream();
                while (socket.Available > 0)
                {
                    int count = stream.Read(peer.Scratch, 0, Math.Min(peer.Scratch.Length, socket.Available));
                    if (count == 0) return false;
                    for (int i = 0; i < count; i++) peer.Buffer.Add(peer.Scratch[i]);
                }

                while (peer.Buffer.Count >= 4)
                {
                    int length = peer.Buffer[0] | (peer.Buffer[1] << 8) | (peer.Buffer[2] << 16) | (peer.Buffer[3] << 24);
                    if (length < 1 || length > MaxFrame) throw new InvalidDataException($"Invalid frame length {length}");
                    if (peer.Buffer.Count < length + 4) break;
                    byte[] payload = peer.Buffer.GetRange(4, length).ToArray();
                    peer.Buffer.RemoveRange(0, length + 4);
                    Received?.Invoke(peer.Session, payload);
                }
                return true;
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException || error is InvalidDataException)
            {
                return false;
            }
        }

        private void Send(Peer peer, byte[] payload)
        {
            if (peer == null) return;
            try
            {
                byte[] length = BitConverter.GetBytes(payload.Length);
                var stream = peer.Client.GetStream();
                stream.Write(length, 0, length.Length);
                stream.Write(payload, 0, payload.Length);
                BytesSent += payload.Length + length.Length;
            }
            catch (Exception error) when (error is IOException || error is SocketException || error is ObjectDisposedException)
            {
                // Poll removes the disconnected peer on the next update.
            }
        }

        public void SendToServer(byte[] payload, NetworkDelivery delivery=NetworkDelivery.ReliableOrdered, ulong stream=0) => Send(upstream, payload);
        public void SendTo(SessionId session, byte[] payload, NetworkDelivery delivery=NetworkDelivery.ReliableOrdered, ulong stream=0)
        {
            if (peers.TryGetValue(session, out var peer)) Send(peer, payload);
        }
        public void Broadcast(byte[] payload, NetworkDelivery delivery=NetworkDelivery.ReliableOrdered, ulong stream=0)
        {
            foreach (var peer in peers.Values) Send(peer, payload);
        }
        public TransportCapabilities Capabilities(SessionId peer)=>TransportCapabilities.ReliableOrdered|TransportCapabilities.Sequenced;
        public void Disconnect(SessionId session)
        {
            if(server&&peers.TryGetValue(session,out var peer)){peer.Client.Close();peers.Remove(session);Disconnected?.Invoke(session);}
            else if(!server&&upstream!=null){upstream.Client.Close();upstream=null;Disconnected?.Invoke(default);}
        }
        public void Dispose()
        {
            foreach (var peer in peers.Values) peer.Client.Close();
            peers.Clear();
            upstream?.Client.Close();
            upstream = null;
            listener?.Stop();
            listener = null;
        }
    }
}
