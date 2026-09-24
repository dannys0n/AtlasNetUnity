using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AtlasNetDemo.Tests
{
    public sealed class AtlasNetAssetTests
    {
        private const string Sample = "Assets/Samples/AtlasNet Unity/0.1.0/Simple Authority/";

        [Test]
        public void IdentityAndPayloadRoundTrip()
        {
            var entity = new AtlasNet.EntityId(829381);
            var session = new SessionId(42);
            using (var writer = new NetWriter())
            {
                writer.Write(entity);
                writer.Write(session);
                writer.Write(new Vector3(1, 2, 3));
                using (var reader = new NetReader(writer.ToArray()))
                {
                    Assert.AreEqual(entity, reader.ReadEntityId());
                    Assert.AreEqual(session, reader.ReadSessionId());
                    Assert.AreEqual(new Vector3(1, 2, 3), reader.ReadVector3());
                }
            }
        }

        [Test]
        public void VariableAndRpcPermissionsMatchDeclaredDefaults()
        {
            var serverValue = new NetworkVariable<int>(7);
            Assert.AreEqual(NetworkVariableReadPermission.Everyone, serverValue.ReadPermission);
            Assert.AreEqual(NetworkVariableWritePermission.Server, serverValue.WritePermission);

            var ownerValue = new NetworkVariable<Quaternion>(Quaternion.identity,
                NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
            Assert.AreEqual(NetworkVariableWritePermission.Owner, ownerValue.WritePermission);

            var rpc = new RpcAttribute(SendTo.Everyone)
            {
                InvokePermission = RpcInvokePermission.Owner
            };
            Assert.AreEqual(SendTo.Everyone, rpc.Target);
            Assert.AreEqual(RpcInvokePermission.Owner, rpc.InvokePermission);
        }

        [TestCase("ClientAuthority.unity", "simple-client-player", TransformWriter.Owner)]
        [TestCase("ServerAuthority.unity", "simple-server-player", TransformWriter.Server)]
        [TestCase("ScaleDemo.unity", "simple-server-player", TransformWriter.Server)]
        public void SceneRegistryAndPlayerWiring(string sceneName, string playerId, TransformWriter expectedPositionWriter)
        {
            EditorSceneManager.OpenScene(Sample + "Scenes/" + sceneName);
            var manager = Object.FindAnyObjectByType<NetworkManager>();
            Assert.IsNotNull(manager);
            Assert.DoesNotThrow(() => manager.ValidateRegistry());

            var managerData = new SerializedObject(manager);
            var entries = managerData.FindProperty("networkPrefabsLists");
            var player = managerData.FindProperty("playerPrefab").objectReferenceValue as NetworkObject;
            Assert.IsNotNull(player);
            Assert.AreEqual(playerId, player.PrefabId);
            Assert.AreSame(player, manager.PlayerPrefab);
            Assert.AreEqual(sceneName == "ScaleDemo.unity" ? 2 : 1, entries.arraySize);
            var playerList = entries.GetArrayElementAtIndex(0).objectReferenceValue as NetworkPrefabsList;
            Assert.IsNotNull(playerList);
            CollectionAssert.Contains(playerList.Prefabs, player);
            Assert.IsNotNull(player.GetComponent<CharacterController>());
            Assert.IsNotNull(player.GetComponentInChildren<Camera>(true));
            Assert.AreEqual(5, player.GetComponents<NetworkBehaviour>().Length);
            Assert.AreEqual(expectedPositionWriter, player.GetComponent<NetworkTransform>().PositionWriter);

            var launcher = manager.GetComponent("DemoLauncher");
            Assert.IsNotNull(launcher);
            var launcherData = new SerializedObject(launcher);
            Assert.AreSame(manager, launcherData.FindProperty("manager").objectReferenceValue);
        }

        [Test]
        public void ScaleSceneRegistersScaleCubeAndSpawner()
        {
            EditorSceneManager.OpenScene(Sample + "Scenes/ScaleDemo.unity");
            var manager = Object.FindAnyObjectByType<NetworkManager>();
            var entries = new SerializedObject(manager).FindProperty("networkPrefabsLists");
            Assert.AreEqual(2, entries.arraySize);
            var scaleList = entries.GetArrayElementAtIndex(1).objectReferenceValue as NetworkPrefabsList;
            Assert.IsNotNull(scaleList);
            Assert.AreEqual(1, scaleList.Prefabs.Count);
            var prefab = scaleList.Prefabs[0];
            Assert.IsNotNull(prefab);
            Assert.AreEqual("scale-cube", prefab.PrefabId);
            Assert.IsNotNull(manager.GetComponent("ScaleSpawner"));
            var spawner = manager.GetComponent("ScaleSpawner");
            Assert.AreSame(prefab, new SerializedObject(spawner).FindProperty("prefab").objectReferenceValue);
        }

        [Test]
        public void RegistryRejectsMissingPrefabWithClearError()
        {
            var gameObject = new GameObject("Registry validation test");
            var list = ScriptableObject.CreateInstance<NetworkPrefabsList>();
            try
            {
                var listData = new SerializedObject(list);
                listData.FindProperty("prefabs").arraySize = 1;
                listData.ApplyModifiedPropertiesWithoutUndo();
                var manager = gameObject.AddComponent<NetworkManager>();
                var data = new SerializedObject(manager);
                var entries = data.FindProperty("networkPrefabsLists");
                entries.arraySize = 1;
                entries.GetArrayElementAtIndex(0).objectReferenceValue = list;
                data.ApplyModifiedPropertiesWithoutUndo();
                var error = Assert.Throws<System.InvalidOperationException>(() => manager.ValidateRegistry());
                StringAssert.Contains("contains a missing NetworkObject prefab", error.Message);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
                Object.DestroyImmediate(list);
            }
        }

        [Test]
        public void PlayerPrefabMustAppearInAssignedList()
        {
            var gameObject = new GameObject("Player registration test");
            try
            {
                var manager = gameObject.AddComponent<NetworkManager>();
                var player = AssetDatabase.LoadAssetAtPath<NetworkObject>(Sample + "Prefabs/ClientPlayer.prefab");
                Assert.IsNotNull(player);
                var data = new SerializedObject(manager);
                data.FindProperty("playerPrefab").objectReferenceValue = player;
                data.ApplyModifiedPropertiesWithoutUndo();
                var error = Assert.Throws<System.InvalidOperationException>(() => manager.ValidateRegistry());
                StringAssert.Contains("must be registered in a Network Prefabs List", error.Message);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ClientCannotCanonicallySpawn()
        {
            var gameObject = new GameObject("Spawn permission test");
            try
            {
                var manager = gameObject.AddComponent<NetworkManager>();
                var error = Assert.Throws<System.InvalidOperationException>(() =>
                    manager.Spawn("anything", Vector3.zero, Quaternion.identity));
                StringAssert.Contains("Only the server", error.Message);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
