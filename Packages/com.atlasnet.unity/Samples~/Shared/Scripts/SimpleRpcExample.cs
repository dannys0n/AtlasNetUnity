using AtlasNet;
using UnityEngine;

/// <summary>F requests an authority action; its count persists, observers see a flash, sender gets an ack.</summary>
public sealed class SimpleRpcExample : NetworkBehaviour
{
    private readonly NetworkVariable<int> count = new NetworkVariable<int>(0);
    private Renderer visual;
    private Color original;
    private float flashUntil;
    public int Count => count?.Value ?? 0;

    public override void OnNetworkSpawn()
    {
        count.OnValueChanged += (_, next) =>
        {
            if (NetworkManager.ShouldLog(LogType.Log)) NetworkManager.Log(LogType.Log, $"AtlasNet persistent count is now {next} on entity {NetworkObject.EntityId}");
        };
        visual = GetComponentInChildren<Renderer>();
        if (visual != null) 
            original = visual.material.color;
    }

    private void Update()
    {
        if (IsOwner && Input.GetKeyDown(KeyCode.F)) 
            Fire();
        
        if (visual != null && flashUntil > 0 && Time.time >= flashUntil)
        {
            visual.material.color = original;
            flashUntil = 0;
        }
    }

    public void Fire()
    {
        if (!IsOwner) return;
        ShowFlashRpc();
        RequestFireRpc();
    }

    [Rpc(SendTo.Authority)]
    private void RequestFireRpc()
    {
        count.Set(count.Value + 1);
        AcknowledgeFireRpc(count.Value);
    }

    [Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Owner, Delivery=RpcDelivery.Unreliable)]
    private void ShowFlashRpc()
    {
        if (visual == null) 
            return;
        if (NetworkManager.ShouldLog(LogType.Log)) NetworkManager.Log(LogType.Log, $"AtlasNet observers RPC: flash on entity {NetworkObject.EntityId}");
        visual.material.color = Color.yellow;
        flashUntil = Time.time + 0.15f;
    }

    [Rpc(SendTo.Owner)]
    private void AcknowledgeFireRpc(int shotCount)
    {
        if (NetworkManager.ShouldLog(LogType.Log)) NetworkManager.Log(LogType.Log, $"AtlasNet target RPC: shot {shotCount} acknowledged");
    }
}
