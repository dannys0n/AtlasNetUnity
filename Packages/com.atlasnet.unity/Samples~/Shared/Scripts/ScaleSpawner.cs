using System.Collections.Generic;
using AtlasNet;
using UnityEngine;

public sealed class ScaleSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private NetworkObject prefab;
    [SerializeField, Range(0, 200)] private int count = 50;
    private readonly List<NetworkObject> entities = new List<NetworkObject>();
    public int Count { get => count; set => count = Mathf.Clamp(value, 0, 200); }

    private void Update()
    {
        if (!manager.IsRunning) { entities.Clear(); return; }
        if (!manager.IsServer || manager.IsWorker) return;
        // Amortize user-requested changes; this is a demo control, not a benchmark harness.
        for (int changes = 0; changes < 4 && entities.Count != count; changes++)
        {
            if (entities.Count > count)
            {
                var last = entities[entities.Count - 1];
                entities.RemoveAt(entities.Count - 1);
                if (last != null && last.IsSpawned) last.Despawn();
                continue;
            }
            int i = entities.Count;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(count));
            int rows = Mathf.CeilToInt((float)count / columns);
            var position = new Vector3(
                columns > 1 ? Mathf.Lerp(-14f, 14f, (float)(i % columns) / (columns - 1)) : 0,
                1,
                rows > 1 ? Mathf.Lerp(-11f, 11f, (float)(i / columns) / (rows - 1)) : 0);
            var obj = manager.Spawn(prefab, position, Quaternion.identity);
            obj.GetComponent<ScaleMotion>().SetMoving(true);
            entities.Add(obj);
        }
    }
}


