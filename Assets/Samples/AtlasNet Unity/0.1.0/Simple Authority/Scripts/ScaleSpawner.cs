using AtlasNet;
using UnityEngine;

public sealed class ScaleSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private NetworkObject prefab;
    [SerializeField] private int count = 300;
    private bool spawned;

    private void OnEnable() => manager.SessionJoined += OnSessionJoined;
    private void OnDisable() => manager.SessionJoined -= OnSessionJoined;

    private void OnSessionJoined(SessionId session)
    {
        if (!manager.IsServer || spawned) return;
        spawned = true;
        for (int i = 0; i < count; i++)
        {
            Vector3 position = new Vector3((i % 20) * 1.3f - 13, 1, (i / 20) * 1.3f - 9);
            var obj = manager.Spawn(prefab, position, Quaternion.identity);
            obj.GetComponent<ScaleMotion>().SetMoving(i % 6 == 0);
        }
    }
}
