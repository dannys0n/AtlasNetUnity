using AtlasNet;
using UnityEngine;

/// <summary>Scale-demo objects travel across X/Z regions and show local ghosts in amber.</summary>
public sealed class ScaleMotion : NetworkBehaviour
{
    private bool moving;
    private Vector3 origin;
    private float phase;
    private Renderer visual;
    [SerializeField] private Material authorityMaterial;
    [SerializeField] private Material ghostMaterial;
    public void SetMoving(bool value) => moving = value;
    public override void OnNetworkSpawn()
    {
        origin = transform.position;
        visual = GetComponent<Renderer>();
        UpdateServerVisual();
    }
    public override void OnSimulationAuthorityChanged() => UpdateServerVisual();
    private void UpdateServerVisual()
    {
        if (visual == null) return;
        bool authority = HasAuthority;
        visual.enabled = !NetworkManager.IsServer || NetworkManager.ShouldRenderWorkerCopyForDebug(NetworkObject);
        Material material = NetworkManager.IsServer && !authority ? ghostMaterial : authorityMaterial;
        if (material != null && visual.sharedMaterial != material) visual.sharedMaterial = material;
    }
    public override void OnNetworkTick()
    {
        if (HasAuthority && moving)
        {
            phase += 3f / NetworkManager.TickRate;
            transform.position = new Vector3(
                Mathf.PingPong(phase + origin.x + 15f, 30f) - 15f,
                origin.y + 0.5f + Mathf.Sin(phase * 2f + origin.x) * 0.5f,
                Mathf.PingPong(phase * 0.7f + origin.z + 12f, 24f) - 12f);
        }
        UpdateServerVisual();
    }
    protected override void WriteHandoffState(NetWriter writer)
    {
        writer.Write(moving);
        writer.Write(origin);
        writer.Write(phase);
    }
    protected override void ReadHandoffState(NetReader reader)
    {
        moving = reader.ReadBool();
        origin = reader.ReadVector3();
        phase = reader.ReadFloat();
    }
}
