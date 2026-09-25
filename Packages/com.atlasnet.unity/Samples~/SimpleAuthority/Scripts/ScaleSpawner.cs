using AtlasNet;
using UnityEngine;

public sealed class ScaleSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private NetworkObject prefab;
    [SerializeField] private int count = 50;
    private bool spawned;

    private void Update()
    {
        if (!manager.IsRunning) { spawned = false; return; }
        if (!manager.IsServer || manager.IsWorker || spawned) return;
        spawned = true;
        for (int i = 0; i < count; i++)
        {
            int column = i % 10, row = i / 10;
            Vector3 position = new Vector3(column * 2.8f - 12.6f, 1, row * 5f - 10f);
            var obj = manager.Spawn(prefab, position, Quaternion.identity);
            obj.GetComponent<ScaleMotion>().SetMoving(true);
        }
    }
}
