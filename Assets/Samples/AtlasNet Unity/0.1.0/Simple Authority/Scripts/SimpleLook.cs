using AtlasNet;
using UnityEngine;

/// <summary>Mouse aim always runs on the controlling client, including in the server-movement scene.</summary>
public sealed class SimpleLook : NetworkBehaviour
{
    private readonly NetworkVariable<Quaternion> lookPitch = new NetworkVariable<Quaternion>(
        Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    [SerializeField] private Transform pitchPivot;
    [SerializeField] private Camera playerCamera;
    [SerializeField] private Renderer bodyRenderer;
    [SerializeField] private float sensitivity = 2f;
    private float pitch;

    public override void OnNetworkSpawn()
    {
        if (playerCamera != null) playerCamera.enabled = IsOwner;
        if (playerCamera != null && playerCamera.TryGetComponent<AudioListener>(out var listener)) listener.enabled = IsOwner;
        if (bodyRenderer != null) bodyRenderer.enabled = !IsOwner;
        if (IsOwner) Cursor.lockState = CursorLockMode.Locked;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) Cursor.lockState = CursorLockMode.None;
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (!IsOwner)
        {
            pitchPivot.localRotation = lookPitch.Value;
            return;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) 
            Cursor.lockState = CursorLockMode.None;
        
        transform.Rotate(0, Input.GetAxisRaw("Mouse X") * sensitivity, 0);
        pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * sensitivity, -85f, 85f);
        pitchPivot.localRotation = Quaternion.Euler(pitch, 0, 0);
    }

    public override void OnNetworkTick()
    {
        if (IsOwner) lookPitch.Value = pitchPivot.localRotation;
    }
}
