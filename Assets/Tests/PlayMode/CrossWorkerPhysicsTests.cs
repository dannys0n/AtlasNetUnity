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
                // Probe both protocols in the test port range. Windows may reserve
                // its ephemeral ranges differently for UDP and TCP.
                int port = 0;
                for (int candidate = 17879; candidate < 17979 && port == 0; candidate++)
                {
                    var tcp = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, candidate);
                    try
                    {
                        using var udp = new System.Net.Sockets.UdpClient(candidate);
                        tcp.Start(); port = candidate;
                    }
                    catch (System.Net.Sockets.SocketException) { }
                    finally { tcp.Stop(); }
                }
                if (port == 0) throw new InvalidOperationException("No free UDP + WebSocket test port.");
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
