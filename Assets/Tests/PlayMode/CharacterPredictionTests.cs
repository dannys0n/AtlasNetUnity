#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace AtlasNetDemo.Tests
{
    public sealed class CharacterPredictionTests
    {
        [UnityTest]
        public IEnumerator OwnerPredictsAndReconcilesWhileHostAndObserverFollowAuthority() => Run(false);

        [UnityTest]
        public IEnumerator PredictedPlayerTransfersAndStillReconciles() => Run(true);

        [UnityTest]
        public IEnumerator PredictionWorksWithLessFrequentSnapshotsAndSmoothingDisabled() =>
            Run(false, snapshotInterval: 3);


        [UnityTest]
        public IEnumerator CustomDashPrefabPredictsAndReconcilesAfterWorkerHandoff()
        {
            const string path = "Assets/Samples/AtlasNet Unity/0.1.0/09 Client Prediction/PredictedDashPlayer.prefab";
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path)?.GetComponent<NetworkObject>();
            Assert.IsNotNull(prefab);
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            SetArray(list, "prefabs", prefab);
            var link = ScriptableObject.CreateInstance<DelayedPredictionTransport>();
            const int port = 17892;
            var host = Create("Dash host", port, prefab, list, link);
            var client = Create("Dash owner", port, prefab, list, link);
            var worker = Create("Dash worker", port, prefab, list, link);
            var hostSettings = new SerializedObject(host);
            hostSettings.FindProperty("automaticLocalHandoffs").boolValue = true;
            hostSettings.ApplyModifiedPropertiesWithoutUndo();

            NetworkManager[] managers = { host, client, worker };
            var scenes = managers.Select(m => SceneManager.CreateScene(m.name,
                new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
            foreach (var scene in scenes) Block(scene, new Vector3(0, -.5f, 0), new Vector3(80, 1, 80));
            Block(scenes[2], new Vector3(5f, 1, 0), new Vector3(.2f, 3, 2));

            IEnumerator Until(Func<bool> condition, string phase)
            {
                float deadline = Time.realtimeSinceStartup + 8f;
                while (true)
                {
                    for (int i = 0; i < managers.Length; i++)
                        foreach (var obj in managers[i].SpawnedObjects)
                            if (obj.gameObject.scene != scenes[i])
                                SceneManager.MoveGameObjectToScene(obj.gameObject, scenes[i]);

                    if (condition()) yield break;
                    if (Time.realtimeSinceStartup >= deadline) Assert.Fail($"Timed out: {phase}");
                    yield return null;
                }
            }

            try
            {
                host.PlayerSpawnPosition = id => new Vector3(-2, 1, ((int)id.Value - 2) * 5);
                host.StartHost();
                client.StartClient();
                yield return Until(() => client.IsConnectedClient && client.SpawnedCount == 2, "dash owner joined");
                worker.StartWorker();
                yield return Until(() => host.WorkerCount == 2, "dash worker joined");

                var predicted = client.SpawnedObjects.Single(o => o.IsOwner);
                var id = predicted.EntityId;
                Assert.IsTrue(host.TryGet(id, out var serverPlayer));
                var motor = predicted.GetComponent("PredictedDashController");
                Assert.IsNotNull(motor);
                var setInput = motor.GetType().GetMethod("SetInput");
                var cooldown = motor.GetType().GetProperty("CooldownRemaining");
                void Drive(Vector2 move, bool dash = false) => setInput.Invoke(motor, new object[] { move, dash });
                yield return Until(() => predicted.GetComponent<CharacterController>().isGrounded, "dash owner grounded");

                uint epoch = predicted.AuthorityEpoch;
                Drive(Vector2.right, true);
                yield return Until(() => predicted.transform.position.x > .2f, "owner dash is predicted immediately");
                Assert.That(serverPlayer.transform.position.x, Is.EqualTo(-2f).Within(.05f));
                Assert.Greater((float)cooldown.GetValue(motor), 0f);

                yield return Until(() => predicted.AuthorityEpoch > epoch && predicted.SimulationWorker == worker.LocalWorkerId,
                    "custom controller transfers to the worker");
                yield return Until(() => worker.TryGet(id, out var authority) && authority.HasAuthority &&
                    authority.transform.position.x > 4.2f && predicted.transform.position.x > 4.9f,
                    "worker collides while owner predicts past the wall");
                Drive(Vector2.zero);
                yield return Until(() => worker.TryGet(id, out var authority) &&
                    Vector3.Distance(predicted.transform.position, authority.transform.position) < .08f &&
                    Mathf.Abs((float)cooldown.GetValue(motor) -
                        (float)cooldown.GetValue(authority.GetComponent("PredictedDashController"))) < .12f,
                    "owner reconciles pose and cooldown to worker");
                Assert.Less(predicted.transform.position.x, 4.75f);
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                foreach (var manager in managers)
                {
                    manager.Stop();
                    Object.DestroyImmediate(manager.gameObject);
                }
                foreach (var scene in scenes) SceneManager.UnloadSceneAsync(scene);
                Object.DestroyImmediate(list);
                Object.DestroyImmediate(link);
            }
        }

        private static IEnumerator Run(bool handoff, int snapshotInterval = 1)
        {
            const int port = 17891;
            var source = new GameObject("Prediction test player");
            var capsule = source.AddComponent<CharacterController>();
            capsule.height = 2;
            capsule.radius = .4f;
            capsule.minMoveDistance = 0;
            var controllerTemplate = source.AddComponent<PredictedCharacterController>();
            if (snapshotInterval > 1)
            {
                var visual = new GameObject("Visual").transform;
                visual.SetParent(source.transform, false);
                var settings = new SerializedObject(controllerTemplate);
                settings.FindProperty("visualRoot").objectReferenceValue = visual;
                settings.FindProperty("snapshotIntervalTicks").intValue = snapshotInterval;
                settings.FindProperty("smoothLocalVisuals").boolValue = false;
                settings.FindProperty("interpolateSpectators").boolValue = false;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            // An in-memory template keeps this test independent of imported UPM samples.
            capsule.enabled = false;
            var prefab = source.GetComponent<NetworkObject>();
            var template = new SerializedObject(prefab);
            template.FindProperty("prefabId").stringValue = "CharacterPredictionTests.Player";
            template.ApplyModifiedPropertiesWithoutUndo();
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            SetArray(list, "prefabs", prefab);

            var link = ScriptableObject.CreateInstance<DelayedPredictionTransport>();
            var host = Create("Prediction host", port, prefab, list, link);
            var client = Create("Prediction owner", port, prefab, list, link);
            var observer = Create(handoff ? "Prediction worker" : "Prediction observer", port, prefab, list, link);
            if (handoff)
            {
                var settings = new SerializedObject(host);
                settings.FindProperty("automaticLocalHandoffs").boolValue = true;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            NetworkManager[] managers = { host, client, observer };
            var scenes = managers.Select(m => SceneManager.CreateScene(m.name,
                new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();

            foreach (var scene in scenes) Block(scene, new Vector3(0, -.5f, 0), new Vector3(80, 1, 80));
            // The owner deliberately lacks this collider so the authoritative correction is measurable.
            if (handoff) Block(scenes[2], new Vector3(5f, 1, 0), new Vector3(.2f, 3, 2));
            else Block(scenes[0], new Vector3(1.7f, 1, 0), new Vector3(.2f, 3, 2));

            void PlaceReplicas()
            {
                for (int i = 0; i < managers.Length; i++)
                    foreach (var obj in managers[i].SpawnedObjects)
                        if (obj.gameObject.scene != scenes[i])
                            SceneManager.MoveGameObjectToScene(obj.gameObject, scenes[i]);
            }

            IEnumerator Until(Func<bool> condition, string phase)
            {
                float end = Time.realtimeSinceStartup + 8;
                while (!condition() && Time.realtimeSinceStartup < end)
                {
                    PlaceReplicas();
                    yield return null;
                }
                PlaceReplicas();
                Assert.IsTrue(condition(), "Timed out: " + phase + " " + string.Join("; ", managers.Select(m => m.name + ": " + string.Join(", ", m.SpawnedObjects.Select(o => o.OwnerSession + "/" + o.transform.position)))) + " dropped " + link.Dropped);
            }

            try
            {
                host.PlayerSpawnPosition = id => new Vector3(handoff ? -2 : 0, 1, ((int)id.Value - 2) * 5);
                host.StartHost();
                client.StartClient();
                yield return Until(() => client.IsConnectedClient && client.SpawnedCount == 2, "owner joined");
                if (handoff)
                {
                    observer.StartWorker();
                    yield return Until(() => host.WorkerCount == 2, "worker admitted");
                    var predicted = client.SpawnedObjects.Single(o => o.IsOwner);
                    var controller = predicted.GetComponent<PredictedCharacterController>();
                    var id = predicted.EntityId;
                    var epoch = predicted.AuthorityEpoch;
                    Assert.AreEqual(0UL, predicted.SimulationWorker);
                    controller.SetInput(Vector2.right, 0, sprint: true);
                    yield return Until(() => predicted.AuthorityEpoch > epoch &&
                        predicted.SimulationWorker == observer.LocalWorkerId, "authority transferred");
                    yield return Until(() => observer.TryGet(id, out var authority) &&
                        authority.HasAuthority && authority.transform.position.x > 4.2f &&
                        predicted.transform.position.x > 4.9f, "worker movement and owner prediction");
                    controller.SetInput(Vector2.zero, 0);
                    yield return Until(() => observer.TryGet(id, out var authority) &&
                        Vector3.Distance(predicted.transform.position, authority.transform.position) < .08f,
                        "owner reconciles to worker collision");
                    Assert.IsTrue(observer.TryGet(id, out var workerPlayer));
                    Assert.Less(workerPlayer.transform.position.x, 4.75f);
                    Assert.IsTrue(predicted.IsOwner);
                    Assert.Greater(link.Dropped, 0);
                    LogAssert.NoUnexpectedReceived();
                    yield break;
                }
                observer.StartClient();
                yield return Until(() => observer.IsConnectedClient && managers.All(m => m.SpawnedCount == 3), "observer joined");

                var owner = client.SpawnedObjects.Single(o => o.IsOwner);
                Assert.IsTrue(host.TryGet(owner.EntityId, out var serverPlayer));
                Assert.IsTrue(observer.TryGet(owner.EntityId, out var remote));
                var motor = owner.GetComponent<PredictedCharacterController>();
                var hostPlayer = host.SpawnedObjects.Single(o => o.IsOwner);
                var hostMotor = hostPlayer.GetComponent<PredictedCharacterController>();

                yield return Until(() => owner.GetComponent<CharacterController>().isGrounded &&
                    serverPlayer.GetComponent<CharacterController>().isGrounded, "both simulations grounded");
                Assert.IsFalse(remote.GetComponent<CharacterController>().enabled);

                motor.SetInput(Vector2.right, 0);
                yield return Until(() => owner.transform.position.x > .12f, "immediate local prediction");
                Assert.That(serverPlayer.transform.position.x, Is.EqualTo(0).Within(.05f));
                if (snapshotInterval > 1)
                    Assert.AreEqual(Vector3.zero, owner.transform.GetChild(0).localPosition);

                yield return Until(() => serverPlayer.transform.position.x > 1f &&
                    owner.transform.position.x > serverPlayer.transform.position.x + .25f, "authority hits wall while owner predicts");
                motor.SetInput(Vector2.zero, 0);
                yield return Until(() => Vector3.Distance(owner.transform.position, serverPlayer.transform.position) < .08f &&
                    Vector3.Distance(remote.transform.position, serverPlayer.transform.position) < .08f, "owner and observer converge");
                Assert.Less(serverPlayer.transform.position.x, 1.35f, "Server must enforce its collision result.");
                Assert.Greater(link.Dropped, 0, "Test must exercise packet loss.");

                float groundY = owner.transform.position.y;
                motor.SetInput(Vector2.zero, 0, jump: true);
                yield return Until(() => owner.transform.position.y > groundY + .2f, "owner jumps immediately");
                Assert.That(serverPlayer.transform.position.y, Is.EqualTo(groundY).Within(.08f));
                yield return Until(() => serverPlayer.transform.position.y > groundY + .2f, "server receives jump");
                yield return Until(() => owner.transform.position.y < groundY + .08f &&
                    serverPlayer.transform.position.y < groundY + .08f, "jump lands after replay");

                float before = hostPlayer.transform.position.x;
                uint tick = host.Tick;
                hostMotor.SetInput(Vector2.right, 0, sprint: true);
                yield return Until(() => unchecked(host.Tick - tick) >= 4, "host sprint ticks");
                hostMotor.SetInput(Vector2.zero, 0);
                Assert.That(hostPlayer.transform.position.x - before,
                    Is.EqualTo(unchecked(host.Tick - tick) * 8f / host.TickRate).Within(.04f),
                    "Host must advance exactly once per tick.");

                if (snapshotInterval > 1)
                {
                    Assert.IsTrue(observer.TryGet(hostPlayer.EntityId, out var remoteHost));
                    yield return Until(() => Vector3.Distance(remoteHost.transform.position, hostPlayer.transform.position) < .08f,
                        "unbuffered spectator follows host");
                    var presentation = new SerializedObject(remoteHost.GetComponent<PredictedCharacterController>());
                    presentation.FindProperty("interpolateSpectators").boolValue = true;
                    presentation.FindProperty("spectatorBufferTicks").intValue = host.TickRate * 10;
                    presentation.FindProperty("snapSpectatorsOnLargeMove").boolValue = true;
                    presentation.ApplyModifiedPropertiesWithoutUndo();

                    // The test times out before this pose could reach the ten-second interpolation buffer.
                    hostPlayer.transform.position += Vector3.right * 8;
                    yield return Until(() => Vector3.Distance(remoteHost.transform.position, hostPlayer.transform.position) < .08f,
                        "large spectator move bypasses interpolation");
                }
                LogAssert.NoUnexpectedReceived();
            }
            finally
            {
                foreach (var manager in managers)
                {
                    manager.Stop();
                    Object.DestroyImmediate(manager.gameObject);
                }
                foreach (var scene in scenes) SceneManager.UnloadSceneAsync(scene);
                Object.DestroyImmediate(list);
                Object.DestroyImmediate(link);
                Object.DestroyImmediate(source);
            }
        }

        private static NetworkManager Create(string name, int port, NetworkObject prefab,
            NetworkPrefabsList list, NetworkTransportProvider link)
        {
            var manager = new GameObject(name).AddComponent<NetworkManager>();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty("playerPrefab").objectReferenceValue = prefab;
            serialized.FindProperty("transportProvider").objectReferenceValue = link;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            SetArray(manager, "networkPrefabsLists", list);
            manager.ConfigureConnection("127.0.0.1", port);
            return manager;
        }

        private static void SetArray(Object target, string name, Object entry)
        {
            var serialized = new SerializedObject(target);
            var array = serialized.FindProperty(name);
            array.arraySize = 1;
            array.GetArrayElementAtIndex(0).objectReferenceValue = entry;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void Block(Scene scene, Vector3 position, Vector3 scale)
        {
            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.transform.position = position;
            block.transform.localScale = scale;
            SceneManager.MoveGameObjectToScene(block, scene);
        }
    }

    internal static class TestPorts
    {
        internal static int FindAvailablePort()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                using var udp = new System.Net.Sockets.UdpClient(0);
                var endpoint = (System.Net.IPEndPoint)udp.Client.LocalEndPoint;
                var tcp = new System.Net.Sockets.TcpListener(endpoint);
                try
                {
                    tcp.Start();
                    return endpoint.Port;
                }
                catch (System.Net.Sockets.SocketException) { }
                finally { tcp.Stop(); }
            }

            throw new InvalidOperationException("Could not find a port available for both test listeners.");
        }
    }

    // Delay and drop the sequenced datagrams while keeping the real loopback connection and handshake.
    public sealed class DelayedPredictionTransport : NetworkTransportProvider
    {
        public int Dropped { get; private set; }
        public float WorkerCommitDelaySeconds { get; set; }
        public int AuthorityRpcsBeforeCommit { get; private set; }

        private static readonly Type PacketType = typeof(NetworkManager).Assembly.GetType("AtlasNet.LocalWorldBackend+Packet", true);
        private static readonly byte WorkerCommit = Convert.ToByte(Enum.Parse(PacketType, "WorkerCommit"));
        private static readonly byte WorkerAuthorityRpc = Convert.ToByte(Enum.Parse(PacketType, "WorkerAuthorityRpc"));

        public override IMessageTransport CreateTransport(NetworkManager runtime, bool server)
        {
            var type = typeof(NetworkManager).Assembly.GetType("AtlasNet.UnityMessageTransport", true);
            var inner = (IMessageTransport)Activator.CreateInstance(type, server, runtime.Address,
                runtime.Port, runtime.ListenAddress, ClientTransport.Udp, 0, 30000, 30000);
            return new DelayedLink(inner, this);
        }

        private sealed class DelayedLink : IMessageTransport
        {
            private readonly IMessageTransport inner;
            private readonly DelayedPredictionTransport settings;
            private readonly Queue<(double Due, Action Send)> pending = new();
            private readonly Queue<(double Due, SessionId Sender, byte[] Bytes, AtlasNet.EntityId Entity, uint Epoch)> commits = new();
            private int packets;

            public DelayedLink(IMessageTransport inner, DelayedPredictionTransport settings)
            {
                this.inner = inner;
                this.settings = settings;
                inner.Received += Receive;
            }

            public event Action<SessionId> Connected { add => inner.Connected += value; remove => inner.Connected -= value; }
            public event Action<SessionId> Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
            public event Action<SessionId, byte[]> Received;
            public int PeerCount => inner.PeerCount;
            public IEnumerable<SessionId> Sessions => inner.Sessions;
            public long BytesSent => inner.BytesSent;
            public TransportCapabilities Capabilities(SessionId peer) => inner.Capabilities(peer);
            public void Disconnect(SessionId peer) => inner.Disconnect(peer);
            public void Dispose()
            {
                pending.Clear();
                commits.Clear();
                inner.Received -= Receive;
                inner.Dispose();
            }

            private void Receive(SessionId sender, byte[] bytes)
            {
                if (settings.WorkerCommitDelaySeconds > 0 && bytes.Length > 0)
                {
                    using var reader = new NetReader(bytes);
                    byte kind = reader.ReadByte();
                    if (kind == WorkerCommit)
                    {
                        var entity = reader.ReadEntityId();
                        reader.ReadULong();
                        uint epoch = reader.ReadUInt();
                        commits.Enqueue((Time.realtimeSinceStartupAsDouble + settings.WorkerCommitDelaySeconds,
                            sender, bytes, entity, epoch));
                        return;
                    }
                    if (kind == WorkerAuthorityRpc)
                    {
                        var entity = reader.ReadEntityId();
                        uint epoch = reader.ReadUInt();
                        foreach (var commit in commits)
                        {
                            if (commit.Entity != entity || commit.Epoch != epoch) continue;
                            settings.AuthorityRpcsBeforeCommit++;
                            break;
                        }
                    }
                }
                Received?.Invoke(sender, bytes);
            }

            public void Poll()
            {
                double now = Time.realtimeSinceStartupAsDouble;
                while (pending.Count > 0 && pending.Peek().Due <= now) pending.Dequeue().Send();
                while (commits.Count > 0 && commits.Peek().Due <= now)
                {
                    var commit = commits.Dequeue();
                    Received?.Invoke(commit.Sender, commit.Bytes);
                }
                inner.Poll();
            }

            private void Send(NetworkDelivery delivery, Action send)
            {
                if (delivery == NetworkDelivery.ReliableOrdered) { send(); return; }
                if (++packets % 5 == 0) { settings.Dropped++; return; }
                pending.Enqueue((Time.realtimeSinceStartupAsDouble + .16, send));
            }

            public void SendToServer(byte[] payload, NetworkDelivery delivery, ulong stream)
                => Send(delivery, () => inner.SendToServer(payload, delivery, stream));
            public void SendTo(SessionId session, byte[] payload, NetworkDelivery delivery, ulong stream)
                => Send(delivery, () => inner.SendTo(session, payload, delivery, stream));
            public void Broadcast(byte[] payload, NetworkDelivery delivery, ulong stream)
                => Send(delivery, () => inner.Broadcast(payload, delivery, stream));
        }
    }
}
#endif
