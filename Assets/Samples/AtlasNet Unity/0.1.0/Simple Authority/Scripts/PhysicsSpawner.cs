using AtlasNet;
using UnityEngine;

/// <summary>Spawns a few physics cubes. Press R in a server window to kick its authoritative cubes.</summary>
public sealed class PhysicsSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private NetworkObject prefab;
    private bool spawned;

    private void Update()
    {
        if (!manager.IsServer)
        {
            spawned = false;
            return;
        }

        if (!manager.IsWorker && !spawned)
        {
            spawned = true;
            manager.Spawn(prefab, new Vector3(-4, 2.5f, -3), Quaternion.identity);
            manager.Spawn(prefab, new Vector3(0, 3.7f, 4), Quaternion.identity);
            manager.Spawn(prefab, new Vector3(4, 4.9f, 4), Quaternion.identity);
        }

        if (!Input.GetKeyDown(KeyCode.R)) return;
        foreach (var obj in manager.SpawnedObjects)
        {
            if (obj == null || !obj.HasAuthority || obj.PrefabId != prefab.PrefabId) continue;
            var body = obj.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic) continue;
            body.AddForce(new Vector3(1, 1.5f, 0.5f).normalized * 4f, ForceMode.Impulse);
            body.AddTorque(Vector3.up * 2f, ForceMode.Impulse);
        }
    }
}
