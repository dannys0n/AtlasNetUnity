using System;
using System.Collections.Generic;

namespace AtlasNet
{
    public enum NetworkDelivery : byte { ReliableOrdered, Unreliable, UnreliableSequenced }
    public enum RpcDelivery { Reliable, Unreliable, UnreliableSequenced }
    public enum ClientTransport { Udp, WebSocket, Tcp }
    public enum ServerTransport { UdpAndWebSocket, Tcp }
    [Flags] public enum TransportCapabilities { ReliableOrdered=1, Unreliable=2, Sequenced=4 }

    // Optional send-only phase: flush after gameplay without polling callbacks again.
    internal interface IFlushableMessageTransport { void FlushSend(); }

    // Transport handles stay private to each adapter. Gameplay addresses logical sessions.
    public interface IMessageTransport : IDisposable
    {
        event Action<SessionId> Connected;
        event Action<SessionId> Disconnected;
        event Action<SessionId,byte[]> Received;
        int PeerCount { get; }
        IEnumerable<SessionId> Sessions { get; }
        long BytesSent { get; }
        TransportCapabilities Capabilities(SessionId peer);
        void Poll();
        void SendToServer(byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0);
        void SendTo(SessionId session,byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0);
        void Broadcast(byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0);
        void Disconnect(SessionId peer);
    }

    // Reject duplicates and old state only within a logical stream. One player's
    // update must never discard another player's update sharing the connection.
    internal sealed class MessageSequence
    {
        readonly Dictionary<(SessionId,ulong),uint> sent=new();
        readonly Dictionary<(SessionId,ulong),uint> received=new();
        public uint Next(SessionId peer,ulong stream)
        {var key=(peer,stream);sent.TryGetValue(key,out uint value);sent[key]=++value;return value;}
        public bool Accept(SessionId peer,ulong stream,uint sequence)
        {var key=(peer,stream);if(received.TryGetValue(key,out uint previous)&&unchecked((int)(sequence-previous))<=0)return false;received[key]=sequence;return true;}
        public void Remove(SessionId peer)
        {foreach(var key in new List<(SessionId,ulong)>(sent.Keys))if(key.Item1==peer)sent.Remove(key);
         foreach(var key in new List<(SessionId,ulong)>(received.Keys))if(key.Item1==peer)received.Remove(key);}
    }
}
