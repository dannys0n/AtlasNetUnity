---
title: Replicated variables
description: Replicate persistent state with explicit read and write permissions.
---

Use `NetworkVariable<T>` for **current state** that a new observer needs: health, ammo, an equipped weapon index, or an aim value. Use an RPC for an event that happens once, such as a cosmetic flash.

## A server-written value

Declare and initialize variable fields on a concrete `NetworkBehaviour`. The runtime binds them when the object is spawned; do not create a shared variable instance for multiple behaviours.

```csharp title="PlayerHealth.cs"
using AtlasNet;
using UnityEngine;

public sealed class PlayerHealth : NetworkBehaviour
{
    private NetworkVariable<int> health = new(100);

    public override void OnNetworkSpawn()
    {
        health.OnValueChanged += OnHealthChanged;
        DisplayHealth(health.Value);
    }

    public override void OnNetworkDespawn()
    {
        health.OnValueChanged -= OnHealthChanged;
    }

    public void ApplyDamage(int amount)
    {
        if (!HasAuthority) return;

        health.Value = Mathf.Max(0, health.Value - Mathf.Max(0, amount));
    }

    private void OnHealthChanged(int previous, int current)
    {
        DisplayHealth(current);
    }

    private void DisplayHealth(int value)
    {
        Debug.Log($"Entity {NetworkObject.EntityId}: health {value}");
    }
}
```

The default write permission is `Server`, which means the entity's **current simulation authority**, not every process with `IsServer == true`. Call `ApplyDamage` on the target's authoritative copy; for a ghost target, use [cross-entity authority routing](./rpcs.md#cross-entity-server-interactions).

The spawn snapshot is applied before `OnNetworkSpawn`. Initialize presentation from `Value` there; subscribing only to future changes would miss the initial value. Unsubscribe during network despawn.

## Owner-written state

For a field intentionally authored by the controlling session:

```csharp
private NetworkVariable<Quaternion> aim = new(
    Quaternion.identity,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);

public override void OnNetworkTick()
{
    if (IsOwner)
        aim.Value = transform.rotation;
}
```

This fragment demonstrates a writer; remote consumers must apply `aim.Value` to the appropriate visual transform. Do not duplicate a rotation channel already handled by `NetworkTransform`.

For owner-private values, choose `NetworkVariableReadPermission.Owner`. The authoritative worker can still read them to simulate the entity. This is framework replication filtering, not encryption or a complete security boundary.

## Supported types today

Variable serialization supports `int`, `float`, `bool`, `string`, `Vector3`, and `Quaternion`.

Arbitrary structs, collections, custom serializers, and even `Vector2` variable values are not currently supported. `Vector2` **is** supported as an RPC payload; the two surfaces are not identical yet.

## State is not an event log

A new observer receives the current value, not every historical transition. Equal assignments are ignored. Do not toggle a boolean twice in one operation and assume every receiver will observe it as two animation events.

Avoid depending on variable changes for replaying recoil, reload triggers, or impact flashes. Keep persistent weapon selection/ammo in variables and transient actions in appropriate RPCs or `NetworkAnimator.SetTrigger`.

Variable bindings use field names/order derived from the concrete behaviour. Changing declarations or behaviour layout changes the wire schema; run matching code in all instances. There is no production schema migration/version negotiation guarantee.
