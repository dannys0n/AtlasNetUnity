using AtlasNet;
using UnityEngine;

/// <summary>A local view only: follow the owning player's X/Z without networking a camera.</summary>
public sealed class SampleOwnerCamera : MonoBehaviour
{
    [SerializeField] private NetworkManager manager;
    [SerializeField] private Vector3 offset = new Vector3(0, 16, -18);
    [SerializeField] private float targetHeight = 1f;
    private NetworkObject owner;
    private Vector3 initialPosition;
    private Quaternion initialRotation;

    private void Awake()
    {
        initialPosition = transform.position;
        initialRotation = transform.rotation;
    }

    private void LateUpdate()
    {
        if (!manager.IsRunning || !manager.IsClient)
        {
            owner = null;
            transform.SetPositionAndRotation(initialPosition, initialRotation);
            return;
        }
        if (owner == null || !owner.IsSpawned || !owner.IsOwner)
        {
            owner = null;
            foreach (var player in manager.SpawnedObjects)
                if (player.IsOwner) { owner = player; break; }
        }
        if (owner == null) return;
        var horizontal = new Vector3(owner.transform.position.x, 0, owner.transform.position.z);
        transform.position = horizontal + offset;
        transform.LookAt(horizontal + Vector3.up * targetHeight);
    }
}
