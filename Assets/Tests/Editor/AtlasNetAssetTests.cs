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

        [TestCase("ClientAuthority.unity", "simple-client-player", TransformWriter.Owner)]
        [TestCase("ServerAuthority.unity", "simple-server-player", TransformWriter.Server)]
        [TestCase("ScaleDemo.unity", "simple-server-player", TransformWriter.Server)]
        public void SceneRegistryAndPlayerWiring(string sceneName, string playerId, TransformWriter expectedPositionWriter)
        {
            EditorSceneManager.OpenScene(Sample + "Scenes/" + sceneName);
            var manager = Object.FindFirstObjectByType<NetworkManager>();
            Assert.IsNotNull(manager);
            Assert.DoesNotThrow(() => manager.ValidateRegistry());

            var managerData = new SerializedObject(manager);
            var entries = managerData.FindProperty("prefabs");
            Assert.IsTrue(entries.arraySize >= 1);
            var first = entries.GetArrayElementAtIndex(0);
            Assert.AreEqual(playerId, first.FindPropertyRelative("id").stringValue);
            var player = first.FindPropertyRelative("prefab").objectReferenceValue as NetworkObject;
            Assert.IsNotNull(player);
            Assert.AreEqual(playerId, player.PrefabId);
            Assert.IsNotNull(player.GetComponent<CharacterController>());
            Assert.IsNotNull(player.GetComponentInChildren<Camera>(true));
            Assert.AreEqual(5, player.GetComponents<NetworkBehaviour>().Length);
            Assert.AreEqual(expectedPositionWriter, player.GetComponent<NetworkTransform>().PositionWriter);

            var launcher = manager.GetComponent("DemoLauncher");
            Assert.IsNotNull(launcher);
            var launcherData = new SerializedObject(launcher);
            Assert.AreEqual(playerId, launcherData.FindProperty("playerPrefabId").stringValue);
            Assert.AreSame(manager, launcherData.FindProperty("manager").objectReferenceValue);
        }

        [Test]
        public void ScaleSceneRegistersScaleCubeAndSpawner()
        {
            EditorSceneManager.OpenScene(Sample + "Scenes/ScaleDemo.unity");
            var manager = Object.FindFirstObjectByType<NetworkManager>();
            var entries = new SerializedObject(manager).FindProperty("prefabs");
            Assert.AreEqual(2, entries.arraySize);
            var scale = entries.GetArrayElementAtIndex(1);
            Assert.AreEqual("scale-cube", scale.FindPropertyRelative("id").stringValue);
            var prefab = scale.FindPropertyRelative("prefab").objectReferenceValue as NetworkObject;
            Assert.IsNotNull(prefab);
            Assert.AreEqual("scale-cube", prefab.PrefabId);
            Assert.IsNotNull(manager.GetComponent("ScaleSpawner"));
        }

        [Test]
        public void RegistryRejectsMissingPrefabWithClearError()
        {
            var gameObject = new GameObject("Registry validation test");
            try
            {
                var manager = gameObject.AddComponent<NetworkManager>();
                var data = new SerializedObject(manager);
                var entries = data.FindProperty("prefabs");
                entries.arraySize = 1;
                entries.GetArrayElementAtIndex(0).FindPropertyRelative("id").stringValue = "missing";
                data.ApplyModifiedPropertiesWithoutUndo();
                var error = Assert.Throws<System.InvalidOperationException>(() => manager.ValidateRegistry());
                StringAssert.Contains("needs an ID and NetworkObject prefab", error.Message);
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
