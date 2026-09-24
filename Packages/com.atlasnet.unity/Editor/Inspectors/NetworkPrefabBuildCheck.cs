using UnityEditor;
using UnityEditor.Build.Reporting;

namespace AtlasNet.Editor
{
    // Bake generated prefab keys before a player build, including existing
    // projects whose prefabs still have old hand-written keys on disk.
    public sealed class NetworkPrefabBuildCheck : UnityEditor.Build.IPreprocessBuildWithReport
    {
        public int callbackOrder => -1000;

        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:NetworkPrefabsList"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(path);
                if (list == null) continue;
                foreach (var prefab in list.Prefabs)
                {
                    if (prefab != null) _ = prefab.PrefabId;
                }
            }
            AssetDatabase.SaveAssets();
        }
    }
}
