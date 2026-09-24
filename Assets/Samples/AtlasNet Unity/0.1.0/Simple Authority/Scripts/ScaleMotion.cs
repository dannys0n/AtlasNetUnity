using AtlasNet;
using UnityEngine;

/// <summary>Only the server moves selected scale-demo objects. Idle ones send no transform ticks.</summary>
public sealed class ScaleMotion : NetworkBehaviour
{
    private bool moving;
    private Vector3 origin;
    public void SetMoving(bool value) => moving = value;
    public override void OnNetworkSpawn() => origin = transform.position;
    public override void OnNetworkTick()
    {
        if (HasAuthority && moving)
            transform.position = origin + Vector3.up * (0.5f + Mathf.Sin(Time.time * 2f + origin.x) * 0.5f);
    }
}
