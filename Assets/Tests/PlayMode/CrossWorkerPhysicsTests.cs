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

            public World(bool shared = true)
            {
                template = new GameObject("Physics template"); template.SetActive(false);
                Prefab = template.AddComponent<NetworkObject>(); Set(Prefab, "prefabId", "shared-physics-body");
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
                NetworkManager Create(string name)
                {
                    var m = new GameObject(name).AddComponent<NetworkManager>();
                    Set(m, "networkPrefabsLists", new[] { list }); Set(m, "automaticLocalHandoffs", true);
                    Set(m, "localSharedPhysicsRegion", shared);
                    Set(m, "localPhysicsRegion", new Bounds(new Vector3(0, 5, 0), new Vector3(8, 20, 8)));
                    Set(m, "localPhysicsRegionPadding", 1f); Set(m, "localBoundaryMargin", 0.05f);
                    m.ConfigureConnection("127.0.0.1", 17879); return m;
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
            public EntityId Spawn(Vector3 position, bool ordinary = false) =>
                Server.Spawn(ordinary ? OrdinaryPrefab : Prefab, position, Quaternion.identity).EntityId;
            public IEnumerator Step(int count = 1, Action beforePhysics = null)
            {
                for (int i = 0; i < count; i++)
                {
                    Prepare(); yield return new WaitForFixedUpdate(); Prepare(); beforePhysics?.Invoke();
                    foreach (var scene in scenes) scene.GetPhysicsScene().Simulate(Time.fixedDeltaTime);
                    yield return null;
                }
            }
            public IEnumerator Until(Func<bool> condition, string phase)
            {
                float end = Time.realtimeSinceStartup + 12;
                while (!condition() && Time.realtimeSinceStartup < end) yield return Step();
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
        public IEnumerator ContinuousPushingAcrossSpatialSplitUsesOneSolver()
        {
            using var world = new World(); yield return world.Ready();
            var source = world.Spawn(new Vector3(-1.6f, 0.5f, 0));
            var target = world.Spawn(new Vector3(0.4f, 0.5f, 0));
            var ordinary = world.Spawn(new Vector3(3, 0.5f, 2), ordinary: true);
            yield return world.Until(() => world.Authority(ordinary)?.SimulationWorker > 0, "ordinary actor keeps spatial ownership");
            Assert.AreEqual(0ul, world.Authority(source).SimulationWorker);
            Assert.AreEqual(0ul, world.Authority(target).SimulationWorker);
            yield return world.Step(80, () => world.Authority(source).GetComponent<Rigidbody>().linearVelocity = Vector3.right * 3f);
            var pushed = world.Authority(target);
            Assert.AreEqual(0ul, pushed.SimulationWorker, "The Voronoi boundary must not split a continuous contact.");
            Assert.AreEqual(0u, pushed.AuthorityEpoch, "A body already in the shared area must not migrate back and forth.");
            Assert.Greater(pushed.transform.position.x, 2.5f, "Sustained native contact should move the box, not only give one impulse.");
            yield return world.Until(() => world.Client.TryGet(target, out var ghost) && ghost.transform.position.x > 2f, "client sees pushed body");
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator StackSettlesAcrossSpatialBoundaryWithoutHandoffOrForwardedForces()
        {
            using var world = new World(); yield return world.Ready();
            var ids = new[] { 0, 1, 2 }.Select(i => world.Spawn(new Vector3(0.2f, 0.5f + i * 1.03f, 0))).ToArray();
            foreach (var id in ids) world.Authority(id).GetComponent<Rigidbody>().useGravity = true;
            yield return world.Step(150);
            for (int i = 0; i < ids.Length; i++)
            {
                var obj = world.Authority(ids[i]); var body = obj.GetComponent<Rigidbody>();
                Assert.AreEqual(0ul, obj.SimulationWorker); Assert.AreEqual(0u, obj.AuthorityEpoch);
                Assert.That(obj.transform.position.y, Is.EqualTo(0.5f + i).Within(0.1f));
                Assert.That(obj.transform.position.x, Is.EqualTo(0.2f).Within(0.15f));
                Assert.Less(body.linearVelocity.magnitude, 0.15f, "A settled stack must not receive synthetic contact kicks.");
            }
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator EnteringBodyPinsToRegionAndExitHysteresisPreventsPingPong()
        {
            using var world = new World(); yield return world.Ready();
            var id = world.Spawn(new Vector3(12, 1, 0));
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "outside body transfers spatially");
            var old = world.Authority(id); uint epoch = old.AuthorityEpoch;
            old.GetComponent<Rigidbody>().position = new Vector3(5.5f, 1, 0);
            // Entry must override the ordinary two-second handoff cooldown.
            yield return world.Until(() => world.Authority(id)?.SimulationWorker == 0 && world.Authority(id).AuthorityEpoch > epoch, "entry into shared authority");
            var pinned = world.Authority(id); uint entryEpoch = pinned.AuthorityEpoch;
            pinned.GetComponent<Rigidbody>().position = new Vector3(6.2f, 1, 0);
            yield return world.Step(120);
            Assert.AreEqual(0ul, world.Authority(id).SimulationWorker);
            Assert.AreEqual(entryEpoch, world.Authority(id).AuthorityEpoch, "The release margin must prevent boundary churn.");
            var body = world.Authority(id).GetComponent<Rigidbody>();
            body.position = new Vector3(12, 1, 0); body.linearVelocity = Vector3.right;
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "released body returns to spatial placement");
            Assert.That(world.Authority(id).GetComponent<Rigidbody>().linearVelocity.x, Is.EqualTo(1f).Within(0.15f));
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }

        [UnityTest]
        public IEnumerator DisabledRegionRetainsOrdinarySpatialPhysicsPlacement()
        {
            using var world = new World(shared: false); yield return world.Ready();
            var id = world.Spawn(new Vector3(3, 1, 0));
            yield return world.Until(() => world.Authority(id)?.SimulationWorker > 0, "physics body spatial placement without shared region");
            Assert.AreEqual(1, world.Server.LocalAuthorityCount + world.Worker.LocalAuthorityCount);
            world.AssertGhostsKinematic(); LogAssert.NoUnexpectedReceived();
        }
    }
}
#endif
