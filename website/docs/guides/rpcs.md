---
title: RPCs and events
description: Send entity-directed calls to authority, observers, owners, or a specified session.
---

## Call an attributed method normally

AtlasNet's Unity IL post-processor rewrites attributed methods into network sends. The receiving side runs their bodies without recursively sending again. The attribute is not just a marker, and gameplay code does not normally need `SendRpc(nameof(...))`.

```csharp title="FlashEvents.cs"
using AtlasNet;
using UnityEngine;

public sealed class FlashEvents : NetworkBehaviour
{
    private NetworkVariable<int> flashes = new(0);

    private void Update()
    {
        if (IsOwner && Input.GetKeyDown(KeyCode.F))
            RequestFlashRpc();
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestFlashRpc()
    {
        flashes.Value += 1;
        ShowFlashRpc();
    }

    [Rpc(SendTo.Observers, InvokePermission = RpcInvokePermission.Server)]
    private void ShowFlashRpc()
    {
        Debug.Log($"Flash on entity {NetworkObject.EntityId}");
        // Play a short local visual here on each observing client.
    }
}
```

The persistent count can appear in a late-join snapshot. The flash itself is a transient event and is not replayed for clients who were not observing it.

## Destination is not permission

| Destination | Where the call goes | Typical use |
| --- | --- | --- |
| `SendTo.Authority` | This entity's current simulation authority | Input intent, validated gameplay requests |
| `SendTo.Observers` | Clients currently observing this entity | Server-issued impact/animation events |
| `SendTo.Owner` | This entity's controlling client | Private acknowledgement or local feedback |
| `SendTo.SpecifiedInParams` | The session passed as the first parameter | A targeted response |
| `SendTo.Everyone` | Local execution plus the server/observer-client relay path | Explicitly client-authored cosmetic multicast |

“Observers” is not every connected client or every entity in the world. A host observing an entity can execute observer visuals locally. The current local backend also mirrors observer/everyone events to interested worker replicas, so keep their bodies cosmetic rather than running damage again on ghosts. `Everyone` does not mean every process irrespective of interest.

`InvokePermission` controls the **sender**: `Owner`, `Server`, or `Everyone`. The attribute defaults to `Everyone`, but destination-specific checks still apply. For example, an arbitrary observing client cannot invoke an entity's authority request just because that default exists. Use explicit owner/server permissions when they communicate gameplay intent clearly.

An owner-only cosmetic multicast is as small as:

```csharp
[Rpc(SendTo.Everyone, InvokePermission = RpcInvokePermission.Owner)]
private void CosmeticFlashRpc()
{
    Debug.Log("Owner-authored flash");
}
```

Do not also play the same effect outside this method at the call site: `Everyone` already executes locally, so that would duplicate the owner's effect.

## Targeted replies and sender identity

These are method fragments inside a `NetworkBehaviour`:

```csharp
[Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
private void OwnerMessageRpc(string message)
{
    Debug.Log(message);
}

[Rpc(SendTo.SpecifiedInParams, InvokePermission = RpcInvokePermission.Server)]
private void ReplyRpc(SessionId target, string message)
{
    Debug.Log(message);
}

[Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
private void RequestMessageRpc(SessionId sender)
{
    ReplyRpc(sender, "Received by the current authority");
}
```

For an authority RPC, an optional **last** `SessionId` parameter is injected with the real sending session. It is not an ordinary serialized payload or a session to trust from the caller. Call the example with `RequestMessageRpc(default)`; the receiver gets the supplied-by-framework identity.

For a specified-session RPC, the **first** `SessionId` selects the destination. These parameter positions have different meanings.

## Cross-entity server interactions

An authoritative projectile can intersect a local ghost of a player authored on another worker. Do not write that ghost's health directly or pretend it is locally authoritative.

Expose a method on the target that routes a server-only authority RPC using the actual authoritative source:

```csharp title="DamageReceiver.cs"
using AtlasNet;
using UnityEngine;

public sealed class DamageReceiver : NetworkBehaviour
{
    private NetworkVariable<int> health = new(100);

    public void RequestDamageFrom(NetworkObject source, int amount)
    {
        SendAuthorityFrom(source, nameof(ApplyDamageRpc), amount);
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Server)]
    private void ApplyDamageRpc(int amount)
    {
        health.Value = Mathf.Max(0, health.Value - Mathf.Max(0, amount));
    }
}
```

Call `target.RequestDamageFrom(projectile.NetworkObject, damage)` from server logic while the source projectile has `HasAuthority`. The framework checks the authoritative source and routes the request to the target's current authority. The target must be a resident network replica; this is not a public arbitrary-world entity lookup/query API.

This sample illustrates routing, not a complete combat security or lag-compensation system. Validate hit rules, permitted damage, duplicate actions, and source lifetime in game code where required.

## Delivery and current constraints

The local transport is **TCP: reliable and ordered**. There is no implemented per-RPC unreliable/reliable selector. Do not copy FishNet channel parameters or NGO delivery options into AtlasNet examples and assume they exist.

RPC methods must be non-generic instance `void` methods whose names end in `Rpc`. Use unique names rather than overloads, and no `ref`/`out` arguments. The current generator rejects generic RPC behaviours and RPC bodies with exception handlers; keep `try`/`catch` out of the attributed body. If needed, perform that work in an ordinary helper called by it.

Payload types are `int`, `float`, `bool`, `string`, `Vector2`, `Vector3`, and `Quaternion`, plus the special `SessionId` positions described above. Custom structs, entity references, arbitrary objects, and collections are not supported payloads today.

Only invoke RPCs after network spawn and before network despawn. Updating code requires matching method signatures and prefab schemas on all participants.
