#if !ATLASNET_STANDALONE
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;

namespace AtlasNet
{
    // One process, multiple listeners. A client chooses one driver; the backend
    // sees logical sessions and messages, never UDP/WebSocket connection handles.
    internal sealed class UnityMessageTransport : IMessageTransport, IFlushableMessageTransport
    {
        const int HeaderSize=13,MaxPayload=65536-HeaderSize,MaxQueuedBytes=4*1024*1024,MaxBatchBytes=1100;
        const byte BatchMarker=255;
        sealed class Pending { public byte[] bytes;public NetworkDelivery delivery;public ulong stream;public uint sequence; }
        sealed class Peer
        {
            public SessionId session;public NetworkConnection connection;public bool webSocket;
            public readonly Queue<Pending> reliable=new();
            public readonly List<Pending> batch=new(64);
            public int queuedBytes;
        }
        MultiNetworkDriver driver;
        NetworkPipeline reliablePipeline,datagramPipeline;
        readonly Dictionary<SessionId,Peer> peers=new();
        readonly Dictionary<NetworkConnection,SessionId> sessions=new();
        readonly List<SessionId> disconnected=new();
        readonly MessageSequence sequence=new();
        readonly bool server;readonly int simulatedLoss;ulong nextSession=2;int webDriverId;
        public event Action<SessionId> Connected;
        public event Action<SessionId> Disconnected;
        public event Action<SessionId,byte[]> Received;
        public int PeerCount=>peers.Count;
        public IEnumerable<SessionId> Sessions=>peers.Keys;
        public long BytesSent {get;private set;}
        internal long SendOperations {get;private set;}
        internal int LargestBatchBytes {get;private set;}
        internal long FailedBatchAttempts {get;private set;}
        bool sendPending;
        public UnityMessageTransport(bool server,string address,int port,string bindAddress,ClientTransport client,int simulatedLoss=0)
        {
            this.server=server;this.simulatedLoss=simulatedLoss;
#if UNITY_WEBGL && !UNITY_EDITOR
            if(server)throw new NotSupportedException("Browsers join native servers; they cannot listen for connections.");
            client=ClientTransport.WebSocket;
#endif
            driver=MultiNetworkDriver.Create();
            try
            {
                int udp=0;
                if(server||client==ClientTransport.Udp)udp=AddDriver(false,server,bindAddress,port);
                if(server||client==ClientTransport.WebSocket)webDriverId=AddDriver(true,server,bindAddress,port);
                // Identical pipeline layouts on every driver allow unified routing.
                datagramPipeline=simulatedLoss>0?driver.CreatePipeline(typeof(FragmentationPipelineStage),typeof(SimulatorPipelineStage))
                    :driver.CreatePipeline(typeof(FragmentationPipelineStage));
                reliablePipeline=simulatedLoss>0?driver.CreatePipeline(typeof(FragmentationPipelineStage),typeof(ReliableSequencedPipelineStage),typeof(SimulatorPipelineStage))
                    :driver.CreatePipeline(typeof(FragmentationPipelineStage),typeof(ReliableSequencedPipelineStage));
                if(!server)
                {
                    var connection=driver.Connect(client==ClientTransport.WebSocket?webDriverId:udp,new FixedString512Bytes(address),(ushort)port);
                    AddPeer(default,connection,client==ClientTransport.WebSocket);
                }
            }
            catch {driver.Dispose();throw;}
        }
        int AddDriver(bool web,bool listen,string address,int port)
        {
            var settings=new NetworkSettings(Allocator.Temp);
            settings.WithFragmentationStageParameters(payloadCapacity:65536);
            settings.WithReliableStageParameters(windowSize:64);
            if(simulatedLoss>0)settings.WithSimulatorStageParameters(maxPacketCount:512,maxPacketSize:2000,packetDropPercentage:simulatedLoss,randomSeed:12345);
            settings.WithNetworkConfigParameters(sendQueueCapacity:512,receiveQueueCapacity:512);
#if UNITY_WEBGL && !UNITY_EDITOR
            var next=NetworkDriver.Create(new WebSocketNetworkInterface(),settings);
#else
            var next=web?NetworkDriver.Create(new WebSocketNetworkInterface(),settings):NetworkDriver.Create(new UDPNetworkInterface(),settings);
#endif
            settings.Dispose();
            try
            {
                if(listen)
                {
                    if(!NetworkEndpoint.TryParse(address,(ushort)port,out var endpoint))throw new ArgumentException("Invalid listen address.");
                    if(next.Bind(endpoint)!=0||next.Listen()!=0)throw new InvalidOperationException($"Cannot listen on {address}:{port} ({(web?"WebSocket":"UDP")}).");
                }
                return driver.AddDriver(next);
            }
            catch {next.Dispose();throw;}
        }
        void AddPeer(SessionId id,NetworkConnection connection,bool web)
        {peers.Add(id,new Peer{session=id,connection=connection,webSocket=web});sessions.Add(connection,id);}
        public TransportCapabilities Capabilities(SessionId id)
            =>TransportCapabilities.ReliableOrdered|TransportCapabilities.Sequenced
                |(peers.TryGetValue(id,out var peer)&&!peer.webSocket?TransportCapabilities.Unreliable:0);
        public void Poll()
        {
            if(!driver.IsCreated)return;
            driver.ScheduleUpdate().Complete();
            sendPending=true; // Include transport-generated ACKs and retransmissions in the poll flush.
            if(server)
            {
                NetworkConnection connection;
                while((connection=driver.Accept())!=default)
                {
                    var id=new SessionId(nextSession++);
                    bool web=webDriverId!=0&&driver.GetDriverForConnection(connection).Equals(driver.GetDriver(webDriverId));
                    AddPeer(id,connection,web);Connected?.Invoke(id);
                }
            }
            NetworkConnection sender;DataStreamReader reader;NetworkEvent.Type kind;
            while(driver.IsCreated&&(kind=driver.PopEvent(out sender,out reader))!=NetworkEvent.Type.Empty)
            {
                if(!sessions.TryGetValue(sender,out var id))continue;
                if(kind==NetworkEvent.Type.Connect){Connected?.Invoke(id);continue;}
                if(kind==NetworkEvent.Type.Disconnect){disconnected.Add(id);continue;}
                if(kind!=NetworkEvent.Type.Data)continue;
                var start=reader;
                if(reader.Length==0){disconnected.Add(id);continue;}
                if(reader.ReadByte()==BatchMarker)
                {
                    while(driver.IsCreated&&peers.ContainsKey(id)&&reader.GetBytesRead()<reader.Length)
                    {
                        if(reader.Length-reader.GetBytesRead()<2){disconnected.Add(id);break;}
                        int length=reader.ReadUShort();
                        if(!ReceiveFrame(id,ref reader,length)){disconnected.Add(id);break;}
                    }
                }
                else {reader=start;if(!ReceiveFrame(id,ref reader,reader.Length))disconnected.Add(id);}
            }
            foreach(var id in disconnected)RemovePeer(id);disconnected.Clear();
            if(!driver.IsCreated)return;
            FlushSend();
        }
        public void FlushSend()
        {
            if(!driver.IsCreated)return;
            // Build batches afresh from the ordered message queue. Failed sends
            // keep their messages there, so later messages fill the same bounded batches.
            foreach(var peer in peers.Values)if(peer.reliable.Count>0)Flush(peer);
            if(sendPending){driver.ScheduleFlushSend().Complete();sendPending=false;}
        }
        bool ReceiveFrame(SessionId id,ref DataStreamReader reader,int length)
        {
            if(length<=HeaderSize||length>reader.Length-reader.GetBytesRead())return false;
            var delivery=(NetworkDelivery)reader.ReadByte();ulong stream=reader.ReadULong();uint serial=reader.ReadUInt();
            if(delivery>NetworkDelivery.UnreliableSequenced)return false;
            bool accept=delivery!=NetworkDelivery.UnreliableSequenced||sequence.Accept(id,stream,serial);
            var bytes=new byte[length-HeaderSize];
            using(var buffer=new NativeArray<byte>(bytes.Length,Allocator.Temp)){reader.ReadBytes(buffer);buffer.CopyTo(bytes);}
            if(accept)Received?.Invoke(id,bytes);
            return true;
        }
        static void WriteFrame(ref DataStreamWriter writer,Pending message)
        {
            writer.WriteByte((byte)message.delivery);writer.WriteULong(message.stream);writer.WriteUInt(message.sequence);
            using(var bytes=new NativeArray<byte>(message.bytes,Allocator.Temp))writer.WriteBytes(bytes);
        }
        bool TrySend(Peer peer,Pending message)
        {
            if(driver.GetConnectionState(peer.connection)!=NetworkConnection.State.Connected)return false;
            var pipe=message.delivery==NetworkDelivery.ReliableOrdered?reliablePipeline:datagramPipeline;
            if(driver.BeginSend(pipe,peer.connection,out var writer,message.bytes.Length+HeaderSize)!=0)return false;
            WriteFrame(ref writer,message);
            int written=driver.EndSend(writer);if(written<0)return false;BytesSent+=written;SendOperations++;sendPending=true;return true;
        }
        bool TrySendBatch(Peer peer,List<Pending> batch,int length)
        {
            if(driver.GetConnectionState(peer.connection)!=NetworkConnection.State.Connected)return false;
            if(length>MaxBatchBytes)throw new InvalidOperationException("Batch exceeds packet budget.");
            if(driver.BeginSend(reliablePipeline,peer.connection,out var writer,length)!=0){FailedBatchAttempts++;return false;}
            writer.WriteByte(BatchMarker);
            foreach(var message in batch){writer.WriteUShort((ushort)(HeaderSize+message.bytes.Length));WriteFrame(ref writer,message);}
            int written=driver.EndSend(writer);if(written<0){FailedBatchAttempts++;return false;}
            LargestBatchBytes=Math.Max(LargestBatchBytes,written);BytesSent+=written;SendOperations++;sendPending=true;return true;
        }
        void Flush(Peer peer)
        {
            while(peer.reliable.Count>0)
            {
                var first=peer.reliable.Peek();
                if(first.bytes.Length+HeaderSize+3>MaxBatchBytes)
                {
                    if(!TrySend(peer,first))break;
                    peer.reliable.Dequeue();peer.queuedBytes-=first.bytes.Length;continue;
                }
                // Preserve message order while packing below the UDP packet budget.
                // Large messages retain the existing fragmentation path.
                var batch=peer.batch;batch.Clear();int length=1;
                foreach(var message in peer.reliable)
                {
                    int size=2+HeaderSize+message.bytes.Length;
                    if(length+size>MaxBatchBytes)break;
                    batch.Add(message);length+=size;
                }
                bool sent=TrySendBatch(peer,batch,length);
                if(sent)foreach(var message in batch){peer.reliable.Dequeue();peer.queuedBytes-=message.bytes.Length;}
                batch.Clear();
                if(!sent)break;
            }
        }
        void Send(Peer peer,byte[] bytes,NetworkDelivery delivery,ulong stream)
        {
            if(bytes==null||bytes.Length==0||bytes.Length>MaxPayload)throw new ArgumentException($"Transport message must contain 1..{MaxPayload} bytes.");
            if(delivery>NetworkDelivery.UnreliableSequenced)throw new ArgumentOutOfRangeException(nameof(delivery));
            var message=new Pending{bytes=bytes,delivery=delivery,stream=stream,sequence=sequence.Next(peer.session,stream)};
            if(delivery!=NetworkDelivery.ReliableOrdered){TrySend(peer,message);return;}
            if(peer.queuedBytes+bytes.Length>MaxQueuedBytes)throw new InvalidOperationException("Reliable send queue is full; stop producing reliable traffic faster than this peer can receive it.");
            message.bytes=(byte[])bytes.Clone();
            peer.reliable.Enqueue(message);peer.queuedBytes+=bytes.Length;
        }
        public void SendToServer(byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0)
        {if(peers.TryGetValue(default,out var peer))Send(peer,payload,delivery,stream);}
        public void SendTo(SessionId id,byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0)
        {if(peers.TryGetValue(id,out var peer))Send(peer,payload,delivery,stream);}
        public void Broadcast(byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0)
        {foreach(var peer in peers.Values)Send(peer,payload,delivery,stream);}
        void RemovePeer(SessionId id)
        {if(!peers.TryGetValue(id,out var peer))return;sessions.Remove(peer.connection);peers.Remove(id);sequence.Remove(id);Disconnected?.Invoke(id);}
        public void Disconnect(SessionId id)
        {if(peers.TryGetValue(id,out var peer)){driver.Disconnect(peer.connection);RemovePeer(id);}}
        public void Dispose()
        {
            if(driver.IsCreated)
            {
                foreach(var peer in peers.Values){Flush(peer);driver.Disconnect(peer.connection);}
                // UTP needs an update to put disconnect notifications on the wire.
                driver.ScheduleUpdate().Complete();
                driver.ScheduleFlushSend().Complete();
                driver.Dispose();
            }
            peers.Clear();sessions.Clear();
        }
    }
}
#endif
