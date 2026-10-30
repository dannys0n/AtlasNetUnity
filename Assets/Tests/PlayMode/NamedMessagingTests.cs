using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AtlasNet;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace AtlasNetDemo.Tests
{
    public sealed class NamedMessagingTests
    {
        static NetworkManager Create(string name, int port, ClientTransport transport = ClientTransport.Udp)
        {
            var manager = new GameObject(name).AddComponent<NetworkManager>();
            manager.ConfigureConnection("127.0.0.1", port, transport);
            return manager;
        }

        static IEnumerator Until(Func<bool> condition, string phase)
        {
            float end = Time.realtimeSinceStartup + 12;
            while (!condition() && Time.realtimeSinceStartup < end) yield return null;
            Assert.IsTrue(condition(), "Timed out waiting for: " + phase);
        }

        static byte[] IntPayload(int value)
        {
            using var writer = new NetWriter(); writer.Write(value); return writer.ToArray();
        }

        static void Destroy(NetworkManager manager)
        {
            manager.Stop(); UnityEngine.Object.Destroy(manager.gameObject);
        }

        [UnityTest]
        public IEnumerator NamedMessagesAndLifecycleWorkAcrossUdpAndWebSocketWithoutEntities()
        {
            var server = Create("Named server", 17856);
            var udp = Create("Named UDP", 17856);
            var web = Create("Named WebSocket", 17856, ClientTransport.WebSocket);
            int started = 0, stopped = 0, joined = 0, left = 0, udpLeft = 0, webStopped = 0;
            server.OnServerStarted += () => started++;
            server.OnServerStopped += host => { Assert.IsFalse(host); stopped++; };
            server.OnClientConnectedCallback += id => { Assert.Contains(id, new List<SessionId>(server.ConnectedClientsIds)); joined++; };
            server.OnClientDisconnectCallback += id => { Assert.IsFalse(server.ConnectedClientsIds.Contains(id)); left++; };
            udp.OnClientDisconnectCallback += id => udpLeft++;
            web.OnClientStopped += host => { Assert.IsFalse(host); webStopped++; };
            int udpReply = 0, webReply = 0, targeted = 0, broadcasts = 0, large = 0, staleCallbacks = 0;
            var states = new Dictionary<string, int>();
            server.CustomMessagingManager.RegisterNamedMessageHandler("request", (sender, reader) =>
            {
                Assert.IsTrue(server.ConnectedClientsIds.Contains(sender));
                server.CustomMessagingManager.SendNamedMessage("reply", sender, IntPayload(reader.ReadInt() + 1));
            });
            server.CustomMessagingManager.RegisterNamedMessageHandler("state.a", (sender, reader) => states["a"] = reader.ReadInt());
            server.CustomMessagingManager.RegisterNamedMessageHandler("state.b", (sender, reader) => states["b"] = reader.ReadInt());
            udp.CustomMessagingManager.RegisterNamedMessageHandler("reply", (sender, reader) => { Assert.AreEqual(NetworkManager.ServerSessionId, sender); udpReply = reader.ReadInt(); });
            web.CustomMessagingManager.RegisterNamedMessageHandler("reply", (sender, reader) => webReply = reader.ReadInt());
            udp.CustomMessagingManager.RegisterNamedMessageHandler("targeted", (sender, reader) => targeted += reader.ReadInt());
            web.CustomMessagingManager.RegisterNamedMessageHandler("targeted", (sender, reader) => Assert.Fail("Targeted message leaked to another client."));
            udp.CustomMessagingManager.RegisterNamedMessageHandler("broadcast", (sender, reader) => broadcasts++);
            web.CustomMessagingManager.RegisterNamedMessageHandler("broadcast", (sender, reader) => broadcasts++);
            web.CustomMessagingManager.RegisterNamedMessageHandler("large", (sender, reader) =>
            {
                Assert.AreEqual(49152, reader.Remaining);
                byte[] bytes = reader.ReadRawBytes(reader.Remaining);
                Assert.AreEqual(17, bytes[0]); Assert.AreEqual(231, bytes[bytes.Length - 1]); large++;
            });
            udp.CustomMessagingManager.RegisterNamedMessageHandler("old-session", (sender, reader) => staleCallbacks++);
            try
            {
                Assert.Throws<InvalidOperationException>(() => udp.CustomMessagingManager.SendNamedMessage("request", NetworkManager.ServerSessionId, IntPayload(1)));
                server.StartServer(); udp.StartClient(); web.StartClient();
                yield return Until(() => udp.IsConnectedClient && web.IsConnectedClient && joined == 2, "admission");
                Assert.AreEqual(1, started); Assert.AreEqual(2, server.ConnectedClientsIds.Count);
                yield return Until(() => udp.ConnectedClientsIds.Count == 2 && web.ConnectedClientsIds.Count == 2, "replicated client roster");
                Assert.Contains(web.LocalSession, new List<SessionId>(udp.ConnectedClientsIds));
                Assert.Contains(udp.LocalSession, new List<SessionId>(web.ConnectedClientsIds));
                Assert.AreEqual(0, server.SpawnedCount, "Custom messages must not need a world NetworkObject.");
                Assert.Throws<InvalidOperationException>(() => udp.CustomMessagingManager.SendNamedMessage("request", web.LocalSession, IntPayload(0)));
                Assert.Throws<ArgumentException>(() => server.CustomMessagingManager.SendNamedMessage("request", new SessionId(999), IntPayload(0)));
                Assert.Throws<ArgumentException>(() => server.CustomMessagingManager.SendNamedMessage("request", udp.LocalSession, new byte[CustomMessagingManager.MaxPayloadSize + 1]));
                udp.CustomMessagingManager.SendNamedMessage("request", NetworkManager.ServerSessionId, IntPayload(10));
                web.CustomMessagingManager.SendNamedMessage("request", NetworkManager.ServerSessionId, IntPayload(20), NetworkDelivery.Unreliable);
                yield return Until(() => udpReply == 11 && webReply == 21, "targeted replies");
                server.CustomMessagingManager.SendNamedMessage("targeted", new[] { udp.LocalSession, udp.LocalSession }, IntPayload(3));
                server.CustomMessagingManager.SendNamedMessageToAll("broadcast", Array.Empty<byte>());
                var payload = new byte[49152]; payload[0] = 17; payload[payload.Length - 1] = 231;
                server.CustomMessagingManager.SendNamedMessage("large", web.LocalSession, payload);
                for (int i = 1; i <= 8; i++)
                {
                    udp.CustomMessagingManager.SendNamedMessage("state.a", NetworkManager.ServerSessionId, IntPayload(i), NetworkDelivery.UnreliableSequenced);
                    udp.CustomMessagingManager.SendNamedMessage("state.b", NetworkManager.ServerSessionId, IntPayload(i + 100), NetworkDelivery.UnreliableSequenced);
                    yield return null;
                }
                yield return Until(() => targeted == 6 && broadcasts == 2 && large == 1 && states.TryGetValue("a", out var a) && a == 8 && states.TryGetValue("b", out var b) && b == 108, "large payload, broadcasts and sequenced streams");
                udp.CustomMessagingManager.UnregisterNamedMessageHandler("targeted");
                server.CustomMessagingManager.SendNamedMessage("targeted", udp.LocalSession, IntPayload(100));
                for (int i = 0; i < 5; i++) yield return null;
                Assert.AreEqual(6, targeted);
                server.DisconnectClient(web.LocalSession);
                yield return Until(() => !web.IsRunning && left == 1 && webStopped == 1, "server disconnect");
                Assert.IsFalse(web.IsConnectedClient); Assert.AreEqual(1, server.ConnectedClientsIds.Count);
                yield return Until(() => udp.ConnectedClientsIds.Count == 1, "peer departure roster");
                Assert.AreEqual(0, web.ConnectedClientsIds.Count);
                web.Stop(); Assert.AreEqual(1, webStopped, "Stop must notify exactly once.");
                udp.Stop(); Assert.AreEqual(1, udpLeft);
                yield return Until(() => left == 2, "client shutdown notification");
                var previousRoster = server.ConnectedClientsIds;
                server.Stop(); Assert.AreEqual(1, stopped); Assert.AreEqual(0, previousRoster.Count);
                Assert.AreEqual(0, server.ConnectedClientsIds.Count);
                // NGO custom handlers are session-scoped; register again for the next session.
                server.CustomMessagingManager.RegisterNamedMessageHandler("request", (sender, reader) =>
                    server.CustomMessagingManager.SendNamedMessage("reply", sender, IntPayload(reader.ReadInt() + 1)));
                udp.CustomMessagingManager.RegisterNamedMessageHandler("reply", (sender, reader) => udpReply = reader.ReadInt());
                server.StartServer(); udp.StartClient();
                yield return Until(() => udp.IsConnectedClient && joined == 3, "reconnect");
                Assert.AreEqual(1, udp.ConnectedClientsIds.Count);
                server.CustomMessagingManager.SendNamedMessage("old-session", udp.LocalSession, Array.Empty<byte>());
                udpReply = 0; udp.CustomMessagingManager.SendNamedMessage("request", NetworkManager.ServerSessionId, IntPayload(30));
                yield return Until(() => udpReply == 31, "reply after reconnect");
                Assert.AreEqual(0, staleCallbacks, "Shutdown must remove previous-session message handlers.");
                Assert.AreEqual(2, started, "Manager must remain reusable after shutdown.");
            }
            finally { Destroy(udp); Destroy(web); Destroy(server); }
        }

        [TestCase(null)]
        [TestCase("")]
        public void NullOrEmptyHandlerNamesLogWithoutThrowing(string name)
        {
            var manager = Create("Invalid message-name test", 17858);
            try
            {
                LogAssert.Expect(LogType.Error, "[RegisterNamedMessageHandler] Cannot register a named message of type null or empty!");
                Assert.DoesNotThrow(() => manager.CustomMessagingManager.RegisterNamedMessageHandler(name, (sender, reader) => { }));
                LogAssert.Expect(LogType.Error, "[UnregisterNamedMessageHandler] Cannot unregister a named message of type null or empty!");
                Assert.DoesNotThrow(() => manager.CustomMessagingManager.UnregisterNamedMessageHandler(name));
            }
            finally { UnityEngine.Object.DestroyImmediate(manager.gameObject); }
        }

        [UnityTest]
        public IEnumerator HostLoopbackAndRejectedClientsHaveConsistentCallbacks()
        {
            var host = Create("Loopback host", 17857);
            var rejected = Create("Rejected client", 17857, ClientTransport.WebSocket);
            int connected = 0, left = 0, hostStopped = 0, rejectedStopped = 0, requests = 0, notices = 0;
            host.OnClientConnectedCallback += id => connected++;
            host.OnClientDisconnectCallback += id => left++;
            var stopOrder = new List<string>();
            host.OnClientStopped += wasHost => { Assert.IsTrue(wasHost); hostStopped++; stopOrder.Add("client"); };
            host.OnServerStopped += wasHost => { Assert.IsTrue(wasHost); stopOrder.Add("server"); };
            host.ConnectionApproval = (id, data) => false;
            rejected.OnClientConnectedCallback += id => Assert.Fail("Rejected client was admitted.");
            int rejectedDisconnected = 0;
            rejected.OnClientDisconnectCallback += id => rejectedDisconnected++;
            rejected.SessionLeft += id => Assert.Fail("An unapproved client must not emit an admitted-session departure.");
            rejected.OnClientStopped += wasHost => rejectedStopped++;
            host.CustomMessagingManager.RegisterNamedMessageHandler("request", (sender, reader) => { Assert.AreEqual(host.LocalSession, sender); requests++; });
            host.CustomMessagingManager.RegisterNamedMessageHandler("notice", (sender, reader) => { Assert.AreEqual(NetworkManager.ServerSessionId, sender); notices++; });
            try
            {
                host.StartHost();
                Assert.IsTrue(host.IsConnectedClient); Assert.AreEqual(1, connected);
                Assert.AreEqual(1, host.ConnectedClientsIds.Count);
                host.CustomMessagingManager.SendNamedMessage("request", NetworkManager.ServerSessionId, Array.Empty<byte>());
                using (var writer = new NetWriter()) host.CustomMessagingManager.SendNamedMessageToAll("notice", writer);
                Assert.AreEqual(1, requests); Assert.AreEqual(1, notices);
                host.CustomMessagingManager.SendNamedMessage("notice", new[] { host.LocalSession, host.LocalSession }, Array.Empty<byte>());
                Assert.AreEqual(3, notices, "Like NGO, the recipient-list overload preserves the supplied entries.");
                rejected.StartClient();
                yield return Until(() => rejectedStopped == 1, "rejection");
                Assert.AreEqual(1, connected); Assert.AreEqual(0, left);
                Assert.AreEqual(1, rejectedDisconnected, "NGO notifies the rejected client of its disconnection.");
                Assert.AreEqual(1, host.ConnectedClientsIds.Count);
                host.Stop(); Assert.AreEqual(1, left); Assert.AreEqual(1, hostStopped);
                CollectionAssert.AreEqual(new[] { "client", "server" }, stopOrder);
                host.Stop(); Assert.AreEqual(1, hostStopped);
            }
            finally { Destroy(rejected); Destroy(host); }
        }
    }

}
