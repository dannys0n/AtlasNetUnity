using AtlasNet;
using UnityEngine;

/// <summary>Top-down, server-authoritative WASD marker for the scale interest demo.</summary>
public sealed class ScalePlayerMovement : NetworkBehaviour
{
    [SerializeField] private float speed = 7f;
    [SerializeField] private Renderer visual;
    [SerializeField] private Material authorityMaterial;
    [SerializeField] private Material ghostMaterial;
    private Vector2 input;

    public override void OnNetworkSpawn() => UpdateVisual();
    public override void OnSimulationAuthorityChanged() => UpdateVisual();

    private void UpdateVisual()
    {
        if (visual == null) return;
        visual.enabled = !NetworkManager.IsServer || NetworkManager.ShouldRenderWorkerCopyForDebug(NetworkObject);
        Material material = NetworkManager.IsServer && !HasAuthority ? ghostMaterial : authorityMaterial;
        if (material != null && visual.sharedMaterial != material) visual.sharedMaterial = material;
    }

    private void Update()
    {
        if (IsOwner)
            input = Vector2.ClampMagnitude(new Vector2(
                Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
    }

    public override void OnNetworkTick()
    {
        if (IsOwner) SubmitInputRpc(input);
        if (HasAuthority)
            transform.position += new Vector3(input.x, 0f, input.y) *
                (speed / NetworkManager.TickRate);
        UpdateVisual();
    }

    [Rpc(SendTo.Authority)]
    private void SubmitInputRpc(Vector2 move) => input = Vector2.ClampMagnitude(move, 1f);

    protected override void WriteHandoffState(NetWriter writer)
    {
        writer.Write(input.x);
        writer.Write(input.y);
    }

    protected override void ReadHandoffState(NetReader reader) =>
        input = new Vector2(reader.ReadFloat(), reader.ReadFloat());
}
