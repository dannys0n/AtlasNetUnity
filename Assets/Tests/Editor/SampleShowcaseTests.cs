using System.Linq;
using AtlasNet;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace AtlasNetDemo.Tests
{
    public sealed class SampleShowcaseTests
    {
        const string Root = "Assets/Samples/AtlasNet Unity/0.1.0/";
        [TestCase("01 QuickStart/01_QuickStart.unity", false)]
        [TestCase("06 AnimatorSync/06_AnimatorSync.unity", true)]
        public void IntroductoryScenesUseRegisteredOwnerWrittenPlayers(string path, bool animated)
        {
            EditorSceneManager.OpenScene(Root + path);
            var manager = Object.FindAnyObjectByType<NetworkManager>();
            Assert.IsNotNull(manager);
            Assert.DoesNotThrow(manager.ValidateRegistry);
            Assert.IsFalse(manager.AutomaticLocalHandoffs);
            var player = manager.PlayerPrefab;
            Assert.IsNotNull(player);
            Assert.AreEqual(TransformWriter.Owner, player.GetComponent<NetworkTransform>().PositionWriter);
            Assert.IsNotNull(player.GetComponent("SamplePlanarMovement"));
            Assert.IsNotNull(player.GetComponent("SimpleRpcExample"));
            Assert.IsFalse(new SerializedObject(manager.GetComponent("DemoLauncher")).FindProperty("advancedDiagnostics").boolValue);
            Assert.AreEqual(animated, player.GetComponent<NetworkAnimator>() != null);
            if (animated)
            {
                Assert.IsNotNull(Camera.main.GetComponent("SampleOwnerCamera"));
                Assert.AreSame(manager, new SerializedObject(Camera.main.GetComponent("SampleOwnerCamera")).FindProperty("manager").objectReferenceValue);
                Assert.AreEqual(AnimatorWriter.Owner, player.GetComponent<NetworkAnimator>().Writer);
                Assert.IsNotNull(player.GetComponent<Animator>().runtimeAnimatorController);
                Assert.IsNotNull(player.GetComponent("SampleAnimatorInput"));
            }
        }

        [Test]
        public void AllShowcasesHaveNoPrivateProjectDependenciesOrMissingScripts()
        {
            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            Assert.AreEqual(8, scenes.Length);
            foreach (var path in scenes)
            {
                var external = AssetDatabase.GetDependencies(path, true).Where(p => p.StartsWith("Assets/") && !p.StartsWith(Root) && !p.StartsWith("Assets/TextMesh Pro/")).ToArray();
                Assert.IsEmpty(external, path);
                var scene = EditorSceneManager.OpenScene(path);
                var config = new SerializedObject(Object.FindAnyObjectByType<NetworkManager>());
                Assert.IsNotNull(config.FindProperty("localPartitionCenter").objectReferenceValue, path);
                Assert.Greater(config.FindProperty("localRegionDebugExtent").floatValue, 0, path);
                var overlay = config.targetObject is NetworkManager manager ? manager.GetComponent("ClientBoundaryOverlay") : null;
                Assert.IsNotNull(overlay, path);
                var overlayData = new SerializedObject(overlay);
                Assert.IsNotNull(overlayData.FindProperty("wallMaterial").objectReferenceValue, path);
                Assert.IsTrue(overlayData.FindProperty("showBoundaries").boolValue, path);
                foreach (var go in scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Transform>(true)))
                    Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject), path + ": " + go.name);
            }
        }
    }
}
