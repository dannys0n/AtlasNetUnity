#if UNITY_EDITOR
using System.Collections;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

public sealed class BackendModeTests
{
    [Test]
    public void DefaultBackendRequiresAnAtlasNetShardForServerMode()
    {
        var root = new GameObject("Native backend test");
        try
        {
            var manager = root.AddComponent<NetworkManager>();
            var error = Assert.Throws<System.NotSupportedException>(() => manager.StartHost());
            StringAssert.Contains("host", error.Message.ToLowerInvariant());
            var shardError = Assert.Throws<System.InvalidOperationException>(() => manager.StartServer());
            StringAssert.Contains("shard worker", shardError.Message);
            Assert.IsFalse(manager.IsRunning);
        }
        finally { Object.DestroyImmediate(root); }
    }

    [UnityTest]
    public IEnumerator LoopbackStartsTicksStopsAndRestarts()
    {
        var root = new GameObject("Loopback test");
        var manager = root.AddComponent<NetworkManager>();
        var settings = new SerializedObject(manager);
        settings.FindProperty("editorLoopback").boolValue = true;
        const string prefabs = "Assets/Samples/AtlasNet Unity/0.1.0/00 Shared/Prefabs/";
        var player = AssetDatabase.LoadAssetAtPath<GameObject>(prefabs + "QuickStartPlayer.prefab");
        var registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(prefabs + "QuickStartPlayerNetworkPrefabs.asset");
        Assert.IsNotNull(player);
        Assert.IsNotNull(registry);
        settings.FindProperty("playerPrefab").objectReferenceValue = player.GetComponent<NetworkObject>();
        var registries = settings.FindProperty("networkPrefabsLists");
        registries.arraySize = 1;
        registries.GetArrayElementAtIndex(0).objectReferenceValue = registry;
        settings.ApplyModifiedPropertiesWithoutUndo();
        try
        {
            manager.StartHost();
            Assert.IsTrue(manager.IsHost);
            Assert.AreNotEqual(0UL, manager.LocalSession.Value);
            Assert.AreEqual(1, manager.SpawnedCount);
            foreach (var replica in manager.SpawnedObjects)
                Assert.IsTrue(replica.IsOwner);
            float deadline = Time.realtimeSinceStartup + 2f;
            while (manager.Tick == 0 && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.Greater(manager.Tick, 0);
            manager.Stop();
            Assert.AreEqual(0, manager.SpawnedCount);
            Assert.IsFalse(manager.IsRunning);
            manager.StartHost();
            Assert.IsTrue(manager.IsHost);
        }
        finally
        {
            manager.Stop();
            Object.DestroyImmediate(root);
        }
    }
}
#endif
