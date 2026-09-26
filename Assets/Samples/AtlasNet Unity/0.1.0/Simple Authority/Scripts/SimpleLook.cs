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
    [SerializeField] private bool yawOnPivot;
    [SerializeField] private bool serverOverview;
    [SerializeField] private Material serverAuthorityMaterial;
    [SerializeField] private Material serverGhostMaterial;
    private float pitch;
    private float yaw;

    public override void OnNetworkSpawn()
    {
        bool firstPerson = IsOwner && !(serverOverview && IsServer);
        if (playerCamera != null) playerCamera.enabled = firstPerson;
        if (playerCamera != null && playerCamera.TryGetComponent<AudioListener>(out var listener)) listener.enabled = firstPerson;
        if (bodyRenderer != null) bodyRenderer.enabled = !IsOwner;
        RefreshServerVisual();
        if (firstPerson) Cursor.lockState = CursorLockMode.Locked;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) Cursor.lockState = CursorLockMode.None;
    }

    public override void OnSimulationAuthorityChanged() => RefreshServerVisual();

    private void RefreshServerVisual()
    {
        if (!serverOverview || !IsServer || bodyRenderer == null) return;
        bodyRenderer.enabled = NetworkManager.ShouldRenderWorkerCopyForDebug(NetworkObject);
        Material material = HasAuthority ? serverAuthorityMaterial : serverGhostMaterial;
        if (material != null && bodyRenderer.sharedMaterial != material) bodyRenderer.sharedMaterial = material;
    }

    private void Update()
    {
        if (!IsSpawned) return;
        if (!IsOwner)
        {
            pitchPivot.localRotation = lookPitch.Value;
            return;
        }
        if (serverOverview && IsServer) return;
        if (Input.GetKeyDown(KeyCode.Escape)) 
            Cursor.lockState = CursorLockMode.None;
        
        float yawDelta = Input.GetAxisRaw("Mouse X") * sensitivity;
        pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * sensitivity, -85f, 85f);
        if (yawOnPivot)
        {
            yaw += yawDelta;
            pitchPivot.localRotation = Quaternion.Euler(pitch, yaw, 0);
        }
        else
        {
            transform.Rotate(0, yawDelta, 0);
            pitchPivot.localRotation = Quaternion.Euler(pitch, 0, 0);
        }
    }

    public override void OnNetworkTick()
    {
        if (IsOwner) lookPitch.Value = pitchPivot.localRotation;
        RefreshServerVisual();
    }
}
