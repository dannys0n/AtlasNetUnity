using System;
using System.Collections.Generic;
using AtlasNet;
using NUnit.Framework;
using UnityEngine;

namespace AtlasNetDemo.Tests
{
    public sealed class DiagnosticTransport : IMessageTransport, ITransportDiagnostics
    {
        public event Action<SessionId> Connected;
        public event Action<SessionId> Disconnected;
        public event Action<SessionId, byte[]> Received;
        public event Action<SessionId,string> Disconnecting;
        public event Action<string> Failure;
        public bool Full,Disposed;
        public int PeerCount => Disposed ? 0 : 1;
        public IEnumerable<SessionId> Sessions => new[]{default(SessionId)};
        public long BytesSent => 0;
        public TransportCapabilities Capabilities(SessionId _) => TransportCapabilities.ReliableOrdered;
        public void Poll() { }
        public void SendToServer(byte[] _, NetworkDelivery delivery=NetworkDelivery.ReliableOrdered, ulong stream=0)
        { if(Full)throw new NetworkSendException(NetworkSendError.QueueFull,"Test queue is full"); }
        public void SendTo(SessionId _,byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0) => SendToServer(payload,delivery,stream);
        public void Broadcast(byte[] payload,NetworkDelivery delivery=NetworkDelivery.ReliableOrdered,ulong stream=0)=>SendToServer(payload,delivery,stream);
        public void Disconnect(SessionId peer) { Disconnecting?.Invoke(peer,"test disconnected"); Disconnected?.Invoke(peer); }
        public void Dispose()=>Disposed=true;
        public void Fail(string reason)=>Failure?.Invoke(reason);
        public void Admit()
        {
            Connected?.Invoke(default);
            using(var welcome=new NetWriter()){welcome.Write((byte)1);welcome.Write(new SessionId(42));Received?.Invoke(default,welcome.ToArray());}
            using(var ready=new NetWriter()){ready.Write((byte)34);ready.Write(1);ready.Write(new SessionId(42));Received?.Invoke(default,ready.ToArray());}
        }
    }
    public sealed class DiagnosticTransportProvider : NetworkTransportProvider
    {
        public DiagnosticTransport Transport;
        public bool RejectStart;
        public override IMessageTransport CreateTransport(NetworkManager runtime,bool server)
        { if(RejectStart)throw new InvalidOperationException("Cannot bind test listener");return Transport=new DiagnosticTransport(); }
    }
    public sealed class ConnectionDiagnosticsTests
    {
        static void Set(object o,string name,object value)=>o.GetType().GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(o,value);
        [Test] public void FatalTransportFailureReportsBeforeStopAndRestartClearsDiagnostics()
        {
            var go=new GameObject("Diagnostics manager");var manager=go.AddComponent<NetworkManager>();
            var provider=ScriptableObject.CreateInstance<DiagnosticTransportProvider>();Set(manager,"transportProvider",provider);
            int failures=0;manager.OnTransportFailure+=()=>{failures++;Assert.AreEqual("Simulated socket failure",manager.LastTransportError);};
            try
            {
                manager.StartClient();provider.Transport.Admit();
                provider.Transport.Fail("Simulated socket failure");
                Assert.AreEqual(1,failures);Assert.IsFalse(manager.IsRunning);Assert.IsTrue(provider.Transport.Disposed);
                Assert.AreEqual("Simulated socket failure",manager.DisconnectReason);
                manager.StartClient();Assert.IsEmpty(manager.DisconnectReason);Assert.IsEmpty(manager.LastTransportError);
            }
            finally{manager.Stop();UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(provider);}
        }
        [Test] public void ListenerStartFailureRaisesTransportEventAndLeavesNoSession()
        {
            var go=new GameObject("Listener failure");var manager=go.AddComponent<NetworkManager>();
            var provider=ScriptableObject.CreateInstance<DiagnosticTransportProvider>();provider.RejectStart=true;Set(manager,"transportProvider",provider);
            int failures=0;manager.OnTransportFailure+=()=>failures++;
            try
            {
                Assert.Throws<InvalidOperationException>(manager.StartServer);
                Assert.AreEqual(1,failures);Assert.IsFalse(manager.IsRunning);StringAssert.Contains("bind",manager.LastTransportError);
            }
            finally{manager.Stop();UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(provider);}
        }
        [Test] public void TryNamedSendDistinguishesSizeQueueAndConnectionFailures()
        {
            var go=new GameObject("Send diagnostics");var manager=go.AddComponent<NetworkManager>();
            var provider=ScriptableObject.CreateInstance<DiagnosticTransportProvider>();Set(manager,"transportProvider",provider);
            try
            {
                var messages=manager.CustomMessagingManager;
                Assert.IsFalse(messages.TrySendNamedMessage("state",default,new byte[1],out var error));Assert.AreEqual(NetworkSendError.NotConnected,error);
                Assert.IsFalse(messages.TrySendNamedMessage("state",default,new byte[CustomMessagingManager.MaxPayloadSize+1],out error));Assert.AreEqual(NetworkSendError.PayloadTooLarge,error);
                manager.StartClient();provider.Transport.Admit();provider.Transport.Full=true;
                Assert.IsFalse(messages.TrySendNamedMessage("state",default,new byte[1],out error));Assert.AreEqual(NetworkSendError.QueueFull,error);
                Assert.Throws<NetworkSendException>(()=>messages.SendNamedMessage("state",default(SessionId),new byte[1]));
                provider.Transport.Full=false;
                Assert.IsTrue(messages.TrySendNamedMessage("state",default,new byte[1],out error));Assert.AreEqual(NetworkSendError.None,error);
            }
            finally{manager.Stop();UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(provider);}
        }
    }
}
