#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace AtlasNetDemo.Tests
{
    public sealed class SampleAnimationTests
    {
        static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        [UnityTest]
        public IEnumerator AnimatorSampleReplicatesMovementParametersTriggersAndLateJoinState()
        {
            const string Root = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
            var player = AssetDatabase.LoadAssetAtPath<NetworkObject>(Root + "AnimatorPlayer.prefab");
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(Root + "AnimatorPlayerNetworkPrefabs.asset");
            NetworkManager Create(string name, ClientTransport transport)
            {
                var m = new GameObject(name).AddComponent<NetworkManager>();
                Set(m, "playerPrefab", player); Set(m, "networkPrefabsLists", new[] { list });
                m.ConfigureConnection("127.0.0.1", 17869, transport);
                return m;
            }
            var server = Create("Animator sample server", ClientTransport.Udp);
            var owner = Create("Animator sample owner", ClientTransport.Udp);
            var late = Create("Animator sample late join", ClientTransport.WebSocket);
            var cameraObject = new GameObject("Local sample camera");
            var camera = cameraObject.AddComponent<Camera>();
            var follow = cameraObject.AddComponent(System.Type.GetType("SampleOwnerCamera, Assembly-CSharp", true));
            Set(follow, "manager", owner);
            try
            {
                server.StartServer(); owner.StartClient();
                float deadline = Time.realtimeSinceStartup + 10;
                while (!owner.IsConnectedClient && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(owner.IsConnectedClient);
                var local = owner.SpawnedObjects.Single(o => o.IsOwner);
                ((Behaviour)local.GetComponent("SampleAnimatorInput")).enabled = false;
                ((Behaviour)local.GetComponent("SamplePlanarMovement")).enabled = false;
                var animator = local.GetComponent<Animator>();
                animator.SetFloat("Speed", 1);
                local.transform.position = new Vector3(3, 0, 2);
                yield return new WaitForSeconds(.4f);
                var centered = camera.WorldToViewportPoint(local.transform.position + Vector3.up);
                Assert.AreEqual(.5f, centered.x, .001f);
                Assert.AreEqual(.5f, centered.y, .001f);
                Assert.IsNull(cameraObject.GetComponent<NetworkObject>(), "The camera must remain local.");
                late.StartClient();
                deadline = Time.realtimeSinceStartup + 10;
                while ((!late.IsConnectedClient || late.SpawnedCount != 2) && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.IsTrue(late.IsConnectedClient);
                var remote = late.SpawnedObjects.Single(o => o.EntityId == local.EntityId);
                var remoteAnimator = remote.GetComponent<Animator>();
                Assert.AreEqual(1f, remoteAnimator.GetFloat("Speed"), .001f);
                Assert.Less(Vector3.Distance(local.transform.position, remote.transform.position), .1f);
                yield return new WaitForSeconds(.2f);
                Assert.IsTrue(remoteAnimator.GetCurrentAnimatorStateInfo(0).IsName("Walk"));

                int waveLayer = remoteAnimator.GetLayerIndex("Wave overlay");
                Assert.GreaterOrEqual(waveLayer, 0);
                local.GetComponent<NetworkAnimator>().SetTrigger("Wave");
                deadline = Time.realtimeSinceStartup + 2;
                while (!remoteAnimator.GetCurrentAnimatorStateInfo(waveLayer).IsName("Wave") &&
                    Time.realtimeSinceStartup < deadline)
                    yield return null;
                Assert.IsTrue(remoteAnimator.GetCurrentAnimatorStateInfo(waveLayer).IsName("Wave"),
                    "A connected observer must see the network trigger on the wave overlay.");
                Assert.IsTrue(remoteAnimator.GetCurrentAnimatorStateInfo(0).IsName("Walk"),
                    "The wave overlay must preserve locomotion.");
            }
            finally
            {
                late.Stop(); owner.Stop(); server.Stop();
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(late.gameObject); Object.DestroyImmediate(owner.gameObject); Object.DestroyImmediate(server.gameObject);
            }
        }
    }
}
#endif

