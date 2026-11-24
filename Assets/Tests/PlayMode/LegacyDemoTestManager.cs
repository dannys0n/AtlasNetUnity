using AtlasNet;
using UnityEngine;

internal static class LegacyDemoTestManager
{
    public static NetworkManager Create(string name)
    {
        var manager = new GameObject(name).AddComponent<NetworkManager>();
        manager.SetBackend(ScriptableObject.CreateInstance<LegacyDemoBackendProvider>());
        return manager;
    }
}
