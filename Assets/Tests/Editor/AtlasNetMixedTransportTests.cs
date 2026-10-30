using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using AtlasNet;
using NUnit.Framework;
public sealed class AtlasNetMixedTransportTests
{
    static IMessageTransport Open(bool server,int port,ClientTransport client,int loss=0)
    {
        var type=typeof(NetworkManager).Assembly.GetType("AtlasNet.UnityMessageTransport",true);
        return (IMessageTransport)Activator.CreateInstance(type,new object[]{server,"127.0.0.1",port,"127.0.0.1",client,loss});
    }
    [TestCase(0)] [TestCase(20)] public void SingleServerAcceptsUdpAndWebSocketAndPreservesReliableOrder(int loss)
    {
        int port=17853;
        using var server=Open(true,port,ClientTransport.Udp,loss);
        using var udp=Open(false,port,ClientTransport.Udp,loss);
        using var web=Open(false,port,ClientTransport.WebSocket,loss);
        var incoming=new Dictionary<SessionId,List<int>>();int connected=0,udpReply=0,webReply=0;
        udp.Connected+=_=>connected++;web.Connected+=_=>connected++;
        server.Received+=(peer,bytes)=>{if(!incoming.TryGetValue(peer,out var list))incoming[peer]=list=new List<int>();list.Add(BitConverter.ToInt32(bytes,0));};
        udp.Received+=(_,bytes)=>udpReply++;web.Received+=(_,bytes)=>webReply++;
        void Pump(Func<bool> complete)
        {
            var timer=Stopwatch.StartNew();
            while(!complete()&&timer.Elapsed.TotalSeconds<15){server.Poll();udp.Poll();web.Poll();Thread.Sleep(1);}
            Assert.IsTrue(complete(),"Transport progress timed out.");
        }
        Pump(()=>connected==2&&server.PeerCount==2);
        int datagrams=0,streams=0;
        foreach(var peer in server.Sessions){if((server.Capabilities(peer)&TransportCapabilities.Unreliable)!=0)datagrams++;else streams++;}
        Assert.AreEqual(1,datagrams);Assert.AreEqual(1,streams);
        for(int i=0;i<100;i++)
        {
            var bytes=new byte[i==99?20000:4];Array.Copy(BitConverter.GetBytes(i),bytes,4);
            udp.SendToServer(bytes);web.SendToServer(bytes);
        }
        Pump(()=>incoming.Count==2&&System.Linq.Enumerable.All(incoming.Values,list=>list.Count==100));
        foreach(var list in incoming.Values)for(int i=0;i<100;i++)Assert.AreEqual(i,list[i]);
        server.Broadcast(new byte[4]);Pump(()=>udpReply==1&&webReply==1);
        Assert.Greater(server.BytesSent,0);
    }
}
