using System;
namespace AtlasNet
{
    internal sealed partial class LocalWorldBackend
    {
        public TransportCapabilities GetTransportCapabilities(SessionId peer)=>transport?.Capabilities(peer)??0;
        IMessageTransport CreateTransport(bool server)
        {
            if(runtime.transportProvider)
                return runtime.transportProvider.CreateTransport(runtime,server)??throw new InvalidOperationException("Transport provider returned null.");
#if UNITY_WEBGL && !UNITY_EDITOR
            return new UnityMessageTransport(server,localAddress,port,runtime.listenAddress,ClientTransport.WebSocket);
#else
            if(server?runtime.serverTransport==ServerTransport.Tcp:runtime.clientTransport==ClientTransport.Tcp)
                return new LocalTcpTransport(server,localAddress,port,runtime.listenAddress);
#if ATLASNET_STANDALONE
            throw new NotSupportedException("Unity Transport is verified in Unity; select TCP for standalone checks.");
#else
            return new UnityMessageTransport(server,localAddress,port,runtime.listenAddress,runtime.clientTransport);
#endif
#endif
        }
        (NetworkDelivery delivery,ulong stream) DeliveryFor(byte[] packet)
        {
            if(packet==null||packet.Length==0)throw new ArgumentException("Empty network message.");
            var type=(Packet)packet[0];
            if(type!=Packet.Rpc&&type!=Packet.WorkerOutboundRpc&&type!=Packet.WorkerAuthorityRpc&&type!=Packet.WorkerInteractionRpc)
                return (NetworkDelivery.ReliableOrdered,0);
            using var reader=new NetReader(packet);reader.ReadByte();
            EntityId id;byte index;uint method;
            if(type==Packet.WorkerOutboundRpc){reader.ReadEntityId();reader.ReadUInt();return DeliveryFor(reader.ReadBytes());}
            if(type==Packet.Rpc){reader.ReadByte();id=reader.ReadEntityId();index=reader.ReadByte();reader.ReadSessionId();method=reader.ReadUInt();}
            else if(type==Packet.WorkerInteractionRpc){reader.ReadEntityId();reader.ReadUInt();id=reader.ReadEntityId();index=reader.ReadByte();method=reader.ReadUInt();}
            else if(type==Packet.WorkerAuthorityRpc){id=reader.ReadEntityId();reader.ReadUInt();index=reader.ReadByte();method=reader.ReadUInt();}
            else return (NetworkDelivery.ReliableOrdered,0);
            NetworkDelivery delivery;
            if(directory.TryGetValue(id,out var entity)&&index<entity.Behaviours.Length)delivery=RpcSchemaFor(entity.Behaviours[index],method).Delivery;
            else delivery=FindBehaviour(id,index)?.GetRpcDelivery(method)??NetworkDelivery.ReliableOrdered;
            if(!Enum.IsDefined(typeof(NetworkDelivery),delivery))throw new InvalidOperationException("Invalid RPC delivery mode.");
            return (delivery,unchecked(id.Value*1099511628211UL^((ulong)index<<32)^method));
        }
        void SendToServer(byte[] packet){var mode=DeliveryFor(packet);transport.SendToServer(packet,mode.delivery,mode.stream);}
        void SendTo(SessionId session,byte[] packet){var mode=DeliveryFor(packet);transport.SendTo(session,packet,mode.delivery,mode.stream);}
        void Broadcast(byte[] packet){var mode=DeliveryFor(packet);transport.Broadcast(packet,mode.delivery,mode.stream);}
    }
}
