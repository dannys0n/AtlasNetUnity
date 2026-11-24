using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine.SceneManagement;

namespace AtlasNet.Editor
{
    internal sealed class EditorLoopbackBuildCheck : IProcessSceneWithReport
    {
        public int callbackOrder => 0;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var manager in root.GetComponentsInChildren<NetworkManager>(true))
                    if (new SerializedObject(manager).FindProperty("editorLoopback").boolValue)
                        throw new BuildFailedException($"Disable Editor Loopback on '{manager.name}' in '{scene.path}' before building a player.");
        }
    }
}
