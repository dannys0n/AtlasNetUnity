#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AtlasNet;
using EntityId = AtlasNet.EntityId;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace AtlasNetDemo.Tests
{
    public sealed class CrossWorkerPhysicsTests
    {
        static readonly List<AsyncOperation> pendingUnloads = new List<AsyncOperation>();
        [UnityTearDown] public IEnumerator FinishSceneUnloads()
        {
            foreach (var operation in pendingUnloads) yield return operation;
            pendingUnloads.Clear();
            // Failed coroutine assertions must not leave fixture managers/scenes
            // behind and turn a single failure into unrelated scene-name failures.
            foreach (var manager in Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (manager.name == "Physics coordinator" || manager.name == "Physics worker" || manager.name == "Physics observer")
                { manager.Stop(); Object.DestroyImmediate(manager.gameObject); }
            foreach (string name in new[] { "Physics coordinator", "Physics worker", "Physics observer" })
            {
                var scene = SceneManager.GetSceneByName(name);
                if (scene.IsValid() && scene.isLoaded) yield return SceneManager.UnloadSceneAsync(scene);
            }
        }
        static void Set(object obj, string field, object value) =>
            obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(obj, value);

        // Each instance has its own PhysX world, just like separate server processes.
        // Otherwise accidental collisions between replicas can make a broken policy pass.
        sealed class World : IDisposable
        {
            public readonly NetworkManager Server, Worker, Client;
            readonly NetworkManager[] managers;
            readonly Scene[] scenes;
            readonly GameObject template, ordinaryTemplate;
            readonly NetworkPrefabsList list;
            public readonly NetworkObject Prefab, OrdinaryPrefab;

            public World()
            {
                template = new GameObject("Physics template"); template.SetActive(false);
                Prefab = template.AddComponent<NetworkObject>(); Set(Prefab, "prefabId", "spatial-physics-body");
                var body = template.AddComponent<Rigidbody>(); body.useGravity = false;
                body.interpolation = RigidbodyInterpolation.None;
                template.AddComponent<BoxCollider>();
                var pose = template.AddComponent<NetworkTransform>(); Set(pose, "rotationWriter", TransformWriter.Server);
                template.AddComponent<NetworkRigidbody>();
                ordinaryTemplate = new GameObject("Ordinary template"); ordinaryTemplate.SetActive(false);
                OrdinaryPrefab = ordinaryTemplate.AddComponent<NetworkObject>(); Set(OrdinaryPrefab, "prefabId", "ordinary-spatial-body");
                Set(ordinaryTemplate.AddComponent<NetworkTransform>(), "rotationWriter", TransformWriter.Server);
                list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
                Set(list, "prefabs", new List<NetworkObject> { Prefab, OrdinaryPrefab });
                int port = TestPorts.FindAvailablePort();
                NetworkManager Create(string name)
                {
                    var m = new GameObject(name).AddComponent<NetworkManager>();
                    Set(m, "networkPrefabsLists", new[] { list }); Set(m, "automaticLocalHandoffs", true);
                    Set(m, "localBoundaryMargin", 0.05f);
                    m.ConfigureConnection("127.0.0.1", port); return m;
                }
                Server = Create("Physics coordinator"); Worker = Create("Physics worker"); Client = Create("Physics observer");
                managers = new[] { Server, Worker, Client };
                scenes = managers.Select(m => SceneManager.CreateScene(m.name, new CreateSceneParameters(LocalPhysicsMode.Physics3D))).ToArray();
                foreach (var scene in scenes)
                {
                    var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    floor.name = "Static floor"; floor.transform.position = new Vector3(0, -0.5f, 0);
                    floor.transform.localScale = new Vector3(100, 1, 100);
                    SceneManager.MoveGameObjectToScene(floor, scene);
                }
                template.transform.position = ordinaryTemplate.transform.position = new Vector3(10000, 2, 0);
                template.SetActive(true); ordinaryTemplate.SetActive(true);
                Server.StartServer(); Worker.StartWorker(); Client.StartClient();
            }
            void Prepare()
            {
                for (int i = 0; i < managers.Length; i++)
                    foreach (var obj in managers[i].SpawnedObjects.ToArray())
                    {
                        if (obj.gameObject.scene != scenes[i]) SceneManager.MoveGameObjectToScene(obj.gameObject, scenes[i]);
                        obj.gameObject.SetActive(true);
                    }
            }
            public NetworkObject Authority(EntityId id)
            {
                Prepare();
                return managers.Take(2).SelectMany(m => m.SpawnedObjects).SingleOrDefault(o => o.EntityId == id && o.HasAuthority);
            }
            public EntityId Spawn(Vector3 position, bool ordinary = false, SessionId owner = default) =>
                Server.Spawn(ordinary ? OrdinaryPrefab : Prefab, position, Quaternion.identity, owner).EntityId;
            public IEnumerator Step(int count = 1, Action beforePhysics = null)
            {
                for (int i = 0; i < count; i++)
                {
                    Prepare(); yield return new WaitForFixedUpdate(); Prepare(); beforePhysics?.Invoke();
                    foreach (var scene in scenes) scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
                    yield return null;
                }
            }
            public IEnumerator Until(Func<bool> condition, string phase, Action beforePhysics = null)
            {
                float end = Time.realtimeSinceStartup + 12;
                while (!condition() && Time.realtimeSinceStartup < end) yield return Step(1, beforePhysics);
                string state = string.Join("; ", managers.Take(2).SelectMany(m => m.SpawnedObjects)
                    .Select(o => $"{o.Manager.name}: {o.EntityId}, authority={o.HasAuthority}, worker={o.SimulationWorker}, pos={o.transform.position}"));
                Assert.IsTrue(condition(), "Timed out: " + phase + ": " + state);
            }
            public IEnumerator Ready() => Until(() => Server.WorkerCount == 2 && Server.RemoteClientCount == 1, "worker and client connection");
            public void AssertGhostsKinematic()
            {
                foreach (var m in managers)
                    foreach (var o in m.SpawnedObjects.Where(o => !o.HasAuthority && o.GetComponent<Rigidbody>() != null))
                        Assert.IsTrue(o.GetComponent<Rigidbody>().isKinematic);
            }
            public void Dispose()
            {
                Client.Stop(); Worker.Stop(); Server.Stop();
                foreach (var m in managers) Object.DestroyImmediate(m.gameObject);
                Object.DestroyImmediate(template); Object.DestroyImmediate(ordinaryTemplate); Object.DestroyImmediate(list);
                foreach (var scene in scenes) pendingUnloads.Add(SceneManager.UnloadSceneAsync(scene));
            }
        }

        [UnityTest]
        public IEnumerator DrainingWorkerPreservesEntityAndPhysicsBeforeDisconnect()
        {
            using var world = new World();
            yield return world.Ready();
            var id = world.Spawn(new Vector3(10, 1, 0));
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "body reaches worker");
            var before = world.Authority(id);
            uint epoch = before.AuthorityEpoch;
            float startX = before.transform.position.x;
            before.GetComponent<Rigidbody>().linearVelocity = Vector3.right;
            ulong worker = world.Worker.LocalWorkerId;
            Assert.IsFalse(world.Server.BeginLocalWorkerDrain(0), "Coordinator cannot retire itself");
            Assert.IsTrue(world.Server.BeginLocalWorkerDrain(worker));
            yield return world.Until(() => world.Server.IsLocalWorkerDrained(worker) && world.Worker.LocalAuthorityCount == 0 &&
                world.Worker.PendingHandoffCount == 0, "worker exports all authority");
            Assert.IsTrue(world.Server.TryGet(id, out var restored));
            Assert.IsTrue(restored.HasAuthority);
            Assert.Greater(restored.AuthorityEpoch, epoch);
            Assert.That(restored.GetComponent<Rigidbody>().linearVelocity.x, Is.EqualTo(1).Within(.1));
            world.Worker.Stop();
            yield return world.Until(() => world.Server.WorkerCount == 1, "retired worker disconnects");
            yield return world.Step(10);
            Assert.Greater(restored.transform.position.x, startX, "Body continues simulating after scale-down");
            Assert.IsTrue(world.Client.TryGet(id, out _), "Client keeps the same entity");
        }

        [UnityTest]
        public IEnumerator CoordinatorDespawnByIdRetiresWorkerTrafficWithoutAcceptingUnknownSenders()
        {
            using var world = new World();
            yield return world.Ready();
            var id = world.Spawn(new Vector3(10, 1, 0), ordinary: true);
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "entity reaches worker");
            var actor = world.Authority(id);
            uint epoch = actor.AuthorityEpoch;
            byte index = (byte)Array.FindIndex(actor.GetComponents<NetworkBehaviour>(), b => b is NetworkTransform);
            var sender = new SessionId(world.Worker.LocalWorkerId);
            var backend = typeof(NetworkManager).GetField("backend", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(world.Server);
            var receive = backend.GetType().GetMethod("ReceiveWorkerTransform", BindingFlags.Instance | BindingFlags.NonPublic);
            var receiveRpc = backend.GetType().GetMethod("ReceiveWorkerOutboundRpc", BindingFlags.Instance | BindingFlags.NonPublic);
            void DeliverRpc(SessionId session, uint generation)
            {
                using var writer = new NetWriter();
                writer.Write(id);
                writer.Write(generation);
                writer.WriteBytes(Array.Empty<byte>());
                using var reader = new NetReader(writer.ToArray());
                receiveRpc.Invoke(backend, new object[] { session, reader });
            }

            Assert.Greater(epoch, 0u);
            Assert.DoesNotThrow(() => DeliverRpc(sender, epoch - 1), "An expired worker RPC must be dropped before dispatch.");
            Assert.Throws<TargetInvocationException>(() => DeliverRpc(world.Client.LocalSession, epoch - 1));
            Assert.Throws<TargetInvocationException>(() => DeliverRpc(sender, epoch + 1));

            Assert.IsTrue(world.Server.TryDespawn(id));
            Assert.IsFalse(world.Server.TryDespawn(id), "Repeated despawn is idempotent");
            void Deliver(EntityId entity, SessionId session, uint generation)
            {
                using var writer = new NetWriter();
                writer.Write(entity); writer.Write(generation); writer.Write(index); writer.Write((byte)1); writer.Write(Vector3.one);
                using var reader = new NetReader(writer.ToArray());
                receive.Invoke(backend, new object[] { session, reader });
            }
            Assert.DoesNotThrow(() => Deliver(id, sender, epoch), "Valid in-flight retired worker update is discarded");
            Assert.Throws<TargetInvocationException>(() => Deliver(id, world.Client.LocalSession, epoch));
            Assert.Throws<TargetInvocationException>(() => Deliver(id, sender, epoch + 1));
            Assert.Throws<TargetInvocationException>(() => Deliver(new EntityId(ulong.MaxValue), sender, epoch));
            yield return world.Step(15);
            Assert.IsFalse(world.Server.TryGet(id, out _));
            Assert.IsFalse(world.Worker.TryGet(id, out _));
        }

        [UnityTest]
        public IEnumerator RemotePlayerPushMovesBoxWithoutCoLocatingPlayer()
        {
            using var world = new World(); yield return world.Ready();
            var player = world.Spawn(new Vector3(-.65f, .5f, 0), owner: world.Client.LocalSession);
            var box = world.Spawn(new Vector3(.6f, .5f, 0));
            yield return world.Until(() => world.Authority(box)?.SimulationWorker > 0, "box starts on neighboring worker");
            yield return world.Step(15);
            ulong worker = world.Authority(box).SimulationWorker;
            var remotePlayer = world.Worker.SpawnedObjects.Single(o => o.EntityId == player);
            yield return world.Step(2);
            Assert.IsTrue(Physics.GetIgnoreCollision(world.Authority(box).GetComponent<Collider>(), remotePlayer.GetComponent<Collider>()),
                "The receiving authority must suppress the duplicate native proxy response.");
            float start = world.Authority(box).transform.position.x;
            yield return world.Until(() => world.Authority(box).transform.position.x > start + .2f,
                "native player contact routes a push across the boundary", () =>
                    world.Authority(player).GetComponent<Rigidbody>().linearVelocity = Vector3.right * 2);
            Assert.Greater(world.Authority(box).transform.position.x, start + .2f, "The routed push must move the remote dynamic box.");
            Assert.AreEqual(0ul, world.Authority(player).SimulationWorker);
            Assert.AreEqual(worker, world.Authority(box).SimulationWorker);
            var playerBody = world.Authority(player).GetComponent<Rigidbody>();
            playerBody.position = new Vector3(2.5f, 1, 0); playerBody.linearVelocity = Vector3.zero;
            yield return world.Until(() => world.Authority(player)?.SimulationWorker == worker, "player follows ordinary spatial handoff");
            yield return world.Step(3);
            Assert.IsFalse(Physics.GetIgnoreCollision(world.Authority(box).GetComponent<Collider>(), world.Authority(player).GetComponent<Collider>()),
                "Co-located bodies must return to normal native collision handling.");
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator OpposingRemoteAndLocalPlayersCanPushOneBoxWithoutAuthorityChurn()
        {
            using var world = new World(); yield return world.Ready();
            var left = world.Spawn(new Vector3(-.65f, .5f, 0), owner: world.Client.LocalSession);
            var box = world.Spawn(new Vector3(.6f, .5f, 0));
            var right = world.Spawn(new Vector3(1.85f, .5f, 0), owner: world.Client.LocalSession);
            yield return world.Until(() => world.Authority(box)?.SimulationWorker > 0 && world.Authority(right)?.SimulationWorker > 0, "right player and box on neighboring worker");
            yield return world.Step(15);
            ulong worker = world.Authority(box).SimulationWorker;
            uint boxEpoch = world.Authority(box).AuthorityEpoch;
            uint leftEpoch = world.Authority(left).AuthorityEpoch;
            uint rightEpoch = world.Authority(right).AuthorityEpoch;
            yield return world.Step(60, () => {
                world.Authority(left).GetComponent<Rigidbody>().linearVelocity = Vector3.right;
                world.Authority(right).GetComponent<Rigidbody>().linearVelocity = Vector3.left;
            });
            var leftPlayer = world.Authority(left);
            var rightPlayer = world.Authority(right);
            var target = world.Authority(box);
            Assert.AreEqual(0ul, leftPlayer.SimulationWorker);
            Assert.AreEqual(worker, target.SimulationWorker);
            Assert.AreEqual(worker, rightPlayer.SimulationWorker);
            Assert.AreEqual(leftEpoch, leftPlayer.AuthorityEpoch);
            Assert.AreEqual(rightEpoch, rightPlayer.AuthorityEpoch);
            Assert.AreEqual(boxEpoch, target.AuthorityEpoch);
            Assert.Less(target.GetComponent<Rigidbody>().linearVelocity.magnitude, 10f);
            // Local contacts and routed pushes can start on different ticks, so the
            // contact cluster can drift. Check that the box stays between the players.
            Vector3 boxPosition = target.transform.position;
            Assert.That(boxPosition.x, Is.InRange(leftPlayer.transform.position.x, rightPlayer.transform.position.x),
                "Opposed pushes must keep the box between the players.");
            Assert.That(Mathf.Abs(boxPosition.z), Is.LessThan(target.GetComponent<Collider>().bounds.extents.z),
                "Opposed pushes must keep the box in the contact lane.");
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RoutedPushRejectsStaleEpochsAndRepeatedRequestsCannotLaunchBox()
        {
            using var world = new World(); yield return world.Ready();
            // Test the routed requests with space between the colliders so native contacts cannot supply an impulse.
            var player = world.Spawn(new Vector3(-1.1f, 1, 0), owner: world.Client.LocalSession);
            var box = world.Spawn(new Vector3(.6f, 1, 0));
            yield return world.Until(() => world.Authority(box)?.SimulationWorker > 0, "remote box authority");
            yield return world.Step(12);
            var source = world.Authority(player);
            Assert.IsTrue(source.Manager.TryGet(box, out var target));
            var send = typeof(NetworkBehaviour).GetMethod("SendAuthorityFrom", BindingFlags.Instance | BindingFlags.NonPublic);
            void Push(uint sourceEpoch, uint targetEpoch, Vector3 impulse) => send.Invoke(target.GetComponent<NetworkRigidbody>(),
                new object[] { source, "ReceiveRemotePushRpc", new object[] { source.EntityId.Value, sourceEpoch, targetEpoch, impulse } });
            Push(source.AuthorityEpoch + 1, target.AuthorityEpoch, Vector3.right * 1000);
            Push(source.AuthorityEpoch, target.AuthorityEpoch + 1, Vector3.right * 1000);
            Push(source.AuthorityEpoch, target.AuthorityEpoch, new Vector3(float.NaN, 0, 0));
            yield return world.Step(8);
            Assert.Less(world.Authority(box).GetComponent<Rigidbody>().linearVelocity.sqrMagnitude, .001f);
            for (int i = 0; i < 30; i++)
            {
                Push(source.AuthorityEpoch, target.AuthorityEpoch, Vector3.right * 1000);
                Push(source.AuthorityEpoch, target.AuthorityEpoch, Vector3.right * 1000);
                yield return world.Step();
                var velocity = world.Authority(box).GetComponent<Rigidbody>().linearVelocity;
                Assert.LessOrEqual(velocity.x, 3.05f, "Repeated requests must converge to the requested speed, not add impulses indefinitely.");
                Assert.Less(Mathf.Abs(velocity.y), .01f);
            }
            Assert.Greater(world.Authority(box).GetComponent<Rigidbody>().linearVelocity.x, .2f);
            yield return world.Step(12);
            Assert.LessOrEqual(world.Authority(box).GetComponent<Rigidbody>().linearVelocity.x, 3.05f);
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator StationaryPenetrationDoesNotBecomeAnExplosivePush()
        {
            using var world = new World(); yield return world.Ready();
            var player = world.Spawn(new Vector3(-.6f, .5f, 0), owner: world.Client.LocalSession);
            var box = world.Spawn(new Vector3(.6f, .5f, 0));
            yield return world.Until(() => world.Authority(box)?.SimulationWorker > 0, "remote box authority");
            yield return world.Step(12);
            yield return world.Step(25, () => {
                var pusher = world.Authority(player).GetComponent<Rigidbody>();
                pusher.position = new Vector3(-.2f, .5f, 0);
                pusher.linearVelocity = Vector3.zero;
            });
            Assert.Less(world.Authority(box).GetComponent<Rigidbody>().linearVelocity.magnitude, .1f,
                "A stationary overlapped player must not forward the solver's depenetration energy.");
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator MovingRigidbodyCrossesBothDirectionsWithOneAuthorityAndPreservedVelocity()
        {
            using var world = new World(); yield return world.Ready();
            var id = world.Spawn(new Vector3(-1.5f, 1, 0));
            var obj = world.Authority(id);
            Assert.AreEqual(0ul, obj.SimulationWorker);
            obj.GetComponent<Rigidbody>().linearVelocity = Vector3.right * 2;
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "moving body enters neighboring worker");
            obj = world.Authority(id);
            Assert.Greater(obj.AuthorityEpoch, 0);
            Assert.That(obj.GetComponent<Rigidbody>().linearVelocity.x, Is.EqualTo(2f).Within(.15f));
            Assert.AreEqual(1, world.Server.LocalAuthorityCount + world.Worker.LocalAuthorityCount);
            uint epoch = obj.AuthorityEpoch;
            obj.GetComponent<Rigidbody>().linearVelocity = Vector3.left * 2;
            yield return world.Until(() => world.Authority(id)?.SimulationWorker == 0 && world.Authority(id).AuthorityEpoch > epoch, "moving body returns to coordinator after cooldown");
            Assert.That(world.Authority(id).GetComponent<Rigidbody>().linearVelocity.x, Is.EqualTo(-2f).Within(.15f));
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator RigidbodyAndOrdinaryEntityUseTheSameSpatialPlacement()
        {
            using var world = new World(); yield return world.Ready();
            var body = world.Spawn(new Vector3(3, 1, 0));
            var ordinary = world.Spawn(new Vector3(3, 1, 2), ordinary: true);
            yield return world.Until(() => world.Authority(body)?.SimulationWorker > 0 && world.Authority(ordinary)?.SimulationWorker > 0, "spatial placement for both entity types");
            Assert.AreEqual(world.Authority(ordinary).SimulationWorker, world.Authority(body).SimulationWorker);
            Assert.AreEqual(2, world.Server.LocalAuthorityCount + world.Worker.LocalAuthorityCount);
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
