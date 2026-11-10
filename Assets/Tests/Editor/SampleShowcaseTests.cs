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
                Assert.IsFalse(((Behaviour)player.GetComponent("SamplePlanarMovement")).enabled);
                Assert.IsFalse(((Behaviour)player.GetComponent("SimpleRpcExample")).enabled);
                Assert.IsNotNull(player.GetComponent("SampleWaveLayer"));
                var controller=(UnityEditor.Animations.AnimatorController)player.GetComponent<Animator>().runtimeAnimatorController;
                Assert.AreEqual(2,controller.layers.Length);
                Assert.AreEqual(0,controller.layers[1].defaultWeight);
                Assert.IsNotNull(controller.layers[1].avatarMask);
            }
        }

        [Test]
        public void AllShowcasesHaveNoPrivateProjectDependenciesOrMissingScripts()
        {
            var scenes = AssetDatabase.FindAssets("t:Scene", new[] { Root }).Select(AssetDatabase.GUIDToAssetPath).ToArray();
            Assert.AreEqual(11, scenes.Length);
            foreach (var path in scenes)
            {
                var external = AssetDatabase.GetDependencies(path, true).Where(p => p.StartsWith("Assets/") && !p.StartsWith(Root) && !p.StartsWith("Assets/TextMesh Pro/")).ToArray();
                Assert.IsEmpty(external, path);
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
                bool alreadyOpen = scene.IsValid() && scene.isLoaded;
                if (!alreadyOpen) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                try
                {
                    var roots = scene.GetRootGameObjects();
                    var manager = roots.SelectMany(root => root.GetComponentsInChildren<NetworkManager>(true)).Single();
                    var config = new SerializedObject(manager);
                    Assert.IsNotNull(config.FindProperty("localPartitionCenter").objectReferenceValue, path);
                    Assert.Greater(config.FindProperty("localRegionDebugExtent").floatValue, 0, path);
                    var overlay = manager.GetComponent("ClientBoundaryOverlay");
                    Assert.IsNotNull(overlay, path);
                    var overlayData = new SerializedObject(overlay);
                    Assert.IsNotNull(overlayData.FindProperty("wallMaterial").objectReferenceValue, path);
                    Assert.IsTrue(overlayData.FindProperty("showBoundaries").boolValue, path);
                    if (path.EndsWith("/09_ClientPrediction.unity") || path.EndsWith("/09_CustomPrediction.unity") ||
                        path.EndsWith("/10_PredictedSoccer.unity"))
                    {
                        Assert.DoesNotThrow(manager.ValidateRegistry);
                        var predictionType = System.IO.Path.GetFileNameWithoutExtension(path) switch
                        {
                            "09_CustomPrediction" => "PredictedDashController",
                            "10_PredictedSoccer" => "PredictedSoccerMotor",
                            _ => "PredictedCharacterController"
                        };
                        Assert.IsNull(manager.PlayerPrefab.GetComponent<NetworkTransform>(), path);
                        var prediction = manager.PlayerPrefab.GetComponent(predictionType);
                        Assert.IsNotNull(prediction, path);
                        var visualRoot = new SerializedObject(prediction).FindProperty("visualRoot").objectReferenceValue;
                        Assert.IsNotNull(visualRoot, path);
                        if (predictionType == "PredictedSoccerMotor")
                        {
                            var marker = manager.PlayerPrefab.GetComponent("SampleOwnerMarker");
                            Assert.IsNotNull(marker, path);
                            Assert.AreSame(visualRoot, new SerializedObject(marker).FindProperty("visualAnchor").objectReferenceValue, path);
                        }
                        Assert.AreSame(manager, overlayData.FindProperty("manager").objectReferenceValue);
                        var region = manager.GetComponent("ScaleRegionOverlay");
                        Assert.IsNotNull(region, path);
                        var regionData = new SerializedObject(region);
                        Assert.AreSame(manager, regionData.FindProperty("manager").objectReferenceValue);
                        Assert.IsInstanceOf<MeshFilter>(regionData.FindProperty("regionMeshFilter").objectReferenceValue);
                        var renderer = regionData.FindProperty("regionRenderer").objectReferenceValue as MeshRenderer;
                        Assert.IsNotNull(renderer);
                        Assert.IsNotNull(renderer.sharedMaterial);
                    }
                    foreach (var go in roots.SelectMany(g => g.GetComponentsInChildren<Transform>(true)))
                        Assert.AreEqual(0, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(go.gameObject), path + ": " + go.name);
                }
                finally
                {
                    if (!alreadyOpen) EditorSceneManager.CloseScene(scene, true);
                }
            }
        }
    }
}
