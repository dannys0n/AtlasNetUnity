using System.Collections.Generic;
using AtlasNet;
using UnityEngine;

/// <summary>Spawns a few server-owned physics cubes. Press R on the host to kick them.</summary>
public sealed class PhysicsSpawner : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private NetworkObject prefab;
    private readonly List<Rigidbody> bodies = new List<Rigidbody>();
    private bool spawned;

    private void Update()
    {
        if (!manager.IsServer || manager.IsWorker)
        {
            spawned = false;
            bodies.Clear();
            return;
        }

        if (!spawned)
        {
            spawned = true;
            for (int i = 0; i < 3; i++)
            {
                var position = new Vector3(0, 2.5f + i * 1.2f, 4);
                var cube = manager.Spawn(prefab, position, Quaternion.identity);
                bodies.Add(cube.GetComponent<Rigidbody>());
            }
        }

        if (Input.GetKeyDown(KeyCode.R))
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodies[i] == null) continue;
                bodies[i].AddForce(new Vector3(i - 1, 1.5f, 0.5f).normalized * 4f, ForceMode.Impulse);
                bodies[i].AddTorque(Vector3.up * 2f, ForceMode.Impulse);
            }
    }
}
