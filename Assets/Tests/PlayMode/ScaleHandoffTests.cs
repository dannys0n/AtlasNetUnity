#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace AtlasNetDemo.Tests
{
    public sealed class ScaleHandoffTests
    {
        static void Set(object o, string name, object value) => o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, value);
        [UnityTest]
        public IEnumerator ScalePopulationMovesBetweenAllThreeSimulationWorkers()
        {
            const string Shared = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
            var prefab = AssetDatabase.LoadAssetAtPath<NetworkObject>(Shared + "ScaleCube.prefab");
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Shared + "ScaleNetworkPrefabs.asset");
            NetworkManager Create(string name)
            {
                var m = new GameObject(name).AddComponent<NetworkManager>();
                Set(m, "networkPrefabsLists", new[] { list }); Set(m, "automaticLocalHandoffs", true);
                Set(m, "localRegionDebugExtent", 30f);
                m.ConfigureConnection("127.0.0.1", 17871); return m;
            }
            var server = Create("Scale coordinator"); var a = Create("Scale worker A"); var b = Create("Scale worker B");
            var spawner = server.gameObject.AddComponent(Type.GetType("ScaleSpawner, Assembly-CSharp", true));
            Set(spawner, "manager", server); Set(spawner, "prefab", prefab); Set(spawner, "count", 24);
            var locations = new Dictionary<AtlasNet.EntityId, HashSet<ulong>>(); var seenWorkers = new HashSet<ulong>();
            bool sawEntering = false, sawExiting = false;
            try
            {
                server.StartServer(); a.StartWorker(); b.StartWorker();
                float deadline = Time.realtimeSinceStartup + 15;
                while (Time.realtimeSinceStartup < deadline)
                {
                    foreach (var manager in new[] { server, a, b })
                        foreach (var obj in manager.SpawnedObjects.Where(o => o.HasAuthority))
                        {
                            seenWorkers.Add(obj.SimulationWorker);
                            if (!locations.TryGetValue(obj.EntityId, out var workers)) locations[obj.EntityId] = workers = new HashSet<ulong>();
                            workers.Add(obj.SimulationWorker);
                        }
                    foreach (var manager in new[] { server, a, b })
                        foreach (var obj in manager.SpawnedObjects)
                        {
                            var state = manager.GetHandoffStateForDebug(obj);
                            sawEntering |= state == HandoffDebugState.Entering;
                            sawExiting |= state == HandoffDebugState.Exiting;
                            if (state != HandoffDebugState.None)
                            {
                                var block = new MaterialPropertyBlock();
                                obj.GetComponent<Renderer>().GetPropertyBlock(block);
                                var color = block.GetColor("_Color");
                                if (state == HandoffDebugState.Entering) Assert.Greater(color.g, color.r, "Incoming entities must already be green before commit, including on the coordinator.");
                                else Assert.Greater(color.r, color.g, "Outgoing entities must be red.");
                            }
                        }
                    if (seenWorkers.Count == 3 && locations.Count == 24 && locations.Values.Count(v => v.Count > 1) >= 4 && sawEntering && sawExiting) break;
                    yield return null;
                }
                Assert.IsTrue(sawEntering, "Local diagnostics must identify incoming handoffs.");
                Assert.IsTrue(sawExiting, "Local diagnostics must identify outgoing handoffs.");
                Assert.AreEqual(3, server.WorkerCount);
                Assert.AreEqual(24, locations.Count);
                Assert.AreEqual(3, seenWorkers.Count, "Each worker must actually simulate sample entities.");
                Assert.GreaterOrEqual(locations.Values.Count(v => v.Count > 1), 4, "Several entities must change simulation worker.");
            }
            finally
            {
                b.Stop(); a.Stop(); server.Stop(); Object.DestroyImmediate(b.gameObject); Object.DestroyImmediate(a.gameObject); Object.DestroyImmediate(server.gameObject);
            }
        }
        [UnityTest]
        public IEnumerator ConfiguredCenterAndUnboundedRegionsControlAuthority()
        {
            const string Shared = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
            var prefab = AssetDatabase.LoadAssetAtPath<NetworkObject>(Shared + "ScaleCube.prefab");
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Shared + "ScaleNetworkPrefabs.asset");
            var center = new GameObject("Configured partition origin"); center.transform.position = new Vector3(100, 0, 50);
            NetworkManager Create(string name)
            {
                var m = new GameObject(name).AddComponent<NetworkManager>();
                Set(m, "networkPrefabsLists", new[] { list }); Set(m, "automaticLocalHandoffs", true);
                Set(m, "localRegionDebugExtent", 1f); m.ConfigureConnection("127.0.0.1", 17873); return m;
            }
            var server = Create("Centered coordinator"); var worker = Create("Centered worker");
            Set(server, "localPartitionCenter", center.transform);
            try
            {
                server.StartServer(); worker.StartWorker();
                float deadline = Time.realtimeSinceStartup + 10;
                while (server.WorkerCount != 2 && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.AreEqual(2, server.WorkerCount);
                var left = server.Spawn(prefab, new Vector3(98, 1, 20000), Quaternion.identity);
                var rightId = server.Spawn(prefab, new Vector3(102, 1, 20000), Quaternion.identity).EntityId;
                deadline = Time.realtimeSinceStartup + 10;
                while (!worker.SpawnedObjects.Any(o => o.EntityId == rightId && o.HasAuthority) && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(left.HasAuthority, "The center Transform, not world zero, must determine the split.");
                Assert.IsTrue(worker.SpawnedObjects.Any(o => o.EntityId == rightId && o.HasAuthority), "Entities far outside the debug window must still migrate.");
            }
            finally
            {
                worker.Stop(); server.Stop(); Object.DestroyImmediate(worker.gameObject); Object.DestroyImmediate(server.gameObject); Object.DestroyImmediate(center);
            }
        }
        [UnityTest]
        public IEnumerator TwoWorkersSustainTwoHundredMovingEntitiesWithoutStalledHandoffs()
        {
            const string Shared = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
            var prefab = AssetDatabase.LoadAssetAtPath<NetworkObject>(Shared + "ScaleCube.prefab");
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Shared + "ScaleNetworkPrefabs.asset");
            NetworkManager Create(string name)
            {
                var m = new GameObject(name).AddComponent<NetworkManager>();
                Set(m, "networkPrefabsLists", new[] { list }); Set(m, "automaticLocalHandoffs", true);
                m.ConfigureConnection("127.0.0.1", 17875); return m;
            }
            var server = Create("200 entity coordinator"); var worker = Create("200 entity worker");
            var spawner = server.gameObject.AddComponent(Type.GetType("ScaleSpawner, Assembly-CSharp", true));
            Set(spawner, "manager", server); Set(spawner, "prefab", prefab); Set(spawner, "count", 200);
            var locations = new Dictionary<AtlasNet.EntityId, HashSet<ulong>>(); uint maxEpoch = 0;
            try
            {
                server.StartServer(); worker.StartWorker();
                float deadline = Time.realtimeSinceStartup + 22;
                while (Time.realtimeSinceStartup < deadline)
                {
                    foreach (var manager in new[] { server, worker })
                        foreach (var obj in manager.SpawnedObjects.Where(o => o.HasAuthority))
                        {
                            if (!locations.TryGetValue(obj.EntityId, out var writers)) locations[obj.EntityId] = writers = new HashSet<ulong>();
                            writers.Add(obj.SimulationWorker); maxEpoch = Math.Max(maxEpoch, obj.AuthorityEpoch);
                        }
                    yield return null;
                }
                Assert.AreEqual(200, server.TrackedEntityCount);
                Assert.AreEqual(200, locations.Count);
                Assert.GreaterOrEqual(locations.Values.Count(v => v.Count == 2), 190, "Almost every cube must cross the partition while moving.");
                Assert.GreaterOrEqual(maxEpoch, 3, "Transfers must keep progressing in both directions.");
                ((Behaviour)spawner).enabled = false;
                deadline = Time.realtimeSinceStartup + 5;
                do
                {
                    foreach (var manager in new[] { server, worker })
                        foreach (var obj in manager.SpawnedObjects.Where(o => o.HasAuthority))
                            obj.GetComponent("ScaleMotion").SendMessage("SetMoving", false);
                    yield return null;
                } while ((server.PendingHandoffCount != 0 || worker.PendingHandoffCount != 0) && Time.realtimeSinceStartup < deadline);
                Assert.AreEqual(0, server.PendingHandoffCount);
                Assert.AreEqual(0, worker.PendingHandoffCount);
                Assert.AreEqual(200, server.LocalAuthorityCount + worker.LocalAuthorityCount, "Every entity needs exactly one live simulation authority after settling.");
            }
            finally
            {
                worker.Stop(); server.Stop(); Object.DestroyImmediate(worker.gameObject); Object.DestroyImmediate(server.gameObject);
            }
        }
        [UnityTest]
        public IEnumerator ClientBoundaryToggleBuildsNearbyWallsAndUnsubscribes()
        {
            const string Root = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/";
            var player = AssetDatabase.LoadAssetAtPath<NetworkObject>(Root + "Prefabs/ScaleInterestPlayer.prefab");
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Root + "Prefabs/ScaleNetworkPrefabs.asset");
            NetworkManager Create(string name)
            {
                var m = new GameObject(name).AddComponent<NetworkManager>();
                Set(m, "networkPrefabsLists", new[] { list }); Set(m, "playerPrefab", player); Set(m, "automaticLocalHandoffs", true);
                m.ConfigureConnection("127.0.0.1", 17877); return m;
            }
            var server = Create("Boundary server"); var worker = Create("Boundary worker"); var client = Create("Boundary client");
            server.PlayerSpawnPosition = _ => new Vector3(2, 1, 0);
            var type = Type.GetType("ClientBoundaryOverlay, Assembly-CSharp", true);
            var overlay = client.gameObject.AddComponent(type);
            type.GetProperty("Visible").SetValue(overlay, false);
            Set(overlay, "manager", client);
            Set(overlay, "wallMaterial", AssetDatabase.LoadAssetAtPath<Material>(Root + "Materials/ScaleRegion.mat"));
            try
            {
                server.StartServer(); worker.StartWorker(); client.StartClient();
                float deadline = Time.realtimeSinceStartup + 8;
                while (!client.IsConnectedClient && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(client.IsConnectedClient);
                Assert.IsEmpty(client.ClientDebugRegion);
                type.GetProperty("Visible").SetValue(overlay, true);
                deadline = Time.realtimeSinceStartup + 8;
                MeshRenderer walls = null;
                while (Time.realtimeSinceStartup < deadline)
                {
                    walls = client.GetComponentInChildren<MeshRenderer>();
                    if (walls != null && walls.enabled && walls.GetComponent<MeshFilter>().sharedMesh.vertexCount > 0 && client.ClientDebugRegionWorker == worker.LocalWorkerId) break;
                    yield return null;
                }
                Assert.IsNotNull(walls);
                Assert.IsTrue(walls.enabled);
                Assert.Greater(walls.GetComponent<MeshFilter>().sharedMesh.vertexCount, 0);
                Assert.IsNull(walls.GetComponent<Collider>());
                Assert.IsNull(walls.GetComponent<NetworkObject>());
                Assert.AreEqual(UnityEngine.Rendering.ShadowCastingMode.Off, walls.shadowCastingMode);
                var owner = client.SpawnedObjects.Single(o => o.IsOwner);
                foreach (var v in walls.GetComponent<MeshFilter>().sharedMesh.vertices)
                {
                    var p = walls.transform.TransformPoint(v) - owner.transform.position;
                    Assert.LessOrEqual(new Vector2(p.x, p.z).magnitude, 81f, "Walls must be clipped near the local owner.");
                }
                type.GetProperty("Visible").SetValue(overlay, false);
                yield return null;
                Assert.IsFalse(walls.enabled);
                Assert.IsEmpty(client.ClientDebugRegion);
            }
            finally
            {
                client.Stop(); worker.Stop(); server.Stop(); Object.DestroyImmediate(client.gameObject); Object.DestroyImmediate(worker.gameObject); Object.DestroyImmediate(server.gameObject);
            }
        }
    }
}
#endif
