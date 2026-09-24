using AtlasNet;
using UnityEngine;

/// <summary>F requests an authority action; its count persists, observers see a flash, sender gets an ack.</summary>
public sealed class SimpleRpcExample : NetworkBehaviour
{
    private const ushort Request = 1;
    private const ushort Flash = 2;
    private const ushort Ack = 3;
    private NetworkVariable<int> count;
    private Renderer visual;
    private Color original;
    private float flashUntil;
    public int Count => count?.Value ?? 0;

    public override void OnNetworkSpawn()
    {
        count = RegisterVariable(1, 0);
        count.Changed += (_, next) => Debug.Log($"AtlasNet persistent count is now {next} on entity {NetworkObject.EntityId}");
        visual = GetComponentInChildren<Renderer>();
        if (visual != null) original = visual.material.color;
    }

    private void Update()
    {
        if (IsOwner && Input.GetKeyDown(KeyCode.F)) Fire();
        if (visual != null && flashUntil > 0 && Time.time >= flashUntil)
        {
            visual.material.color = original;
            flashUntil = 0;
        }
    }

    public void Fire() => AuthorityRpc(Request);

    protected override void OnRpc(ushort method, NetReader payload, SessionId sender)
    {
        if (method == Request && HasSimulationAuthority)
        {
            count.Set(count.Value + 1);
            ObserversRpc(Flash);
            TargetRpc(sender, Ack, writer => writer.Write(count.Value));
        }
        else if (method == Flash && visual != null)
        {
            Debug.Log($"AtlasNet observers RPC: flash on entity {NetworkObject.EntityId}");
            visual.material.color = Color.yellow;
            flashUntil = Time.time + 0.15f;
        }
        else if (method == Ack && IsOwner)
            Debug.Log($"AtlasNet target RPC: shot {payload.ReadInt()} acknowledged");
    }
}
