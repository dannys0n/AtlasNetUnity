---
title: Spawn and despawn
description: Create canonical entities from registered prefabs and distinguish despawn from interest eviction.
---

## Prefab registration comes first

Add every spawnable prefab to a `NetworkPrefabsList` assigned to the manager. Calling `Instantiate` alone produces an ordinary local Unity instance, not a registered network entity.

The manager's **Default Player Prefab** automatically spawns for connecting sessions. Set it before starting the world. Do not also spawn another player in a session callback unless that is intentionally your own player-spawning strategy.

## First-server spawn

The current local coordinator (the first server) can spawn synchronously:

```csharp
NetworkObject spawned = manager.Spawn(
    pickupPrefab,
    new Vector3(0f, 1f, 0f),
    Quaternion.identity);
```

This fragment assumes `manager` and a registered `NetworkObject pickupPrefab` reference. An optional `SessionId owner` assigns a controlling session; leaving it `default` creates an unowned entity.

:::warning Joined workers cannot call Spawn directly
`Spawn` currently throws on a joined worker. It is a canonical first-server operation, not a multi-worker gameplay spawning API. For an authoritative entity on any worker, use `RequestSpawn` below.
:::

## Spawn from authoritative gameplay

Use a spawned entity as the authoritative source for `RequestSpawn`:

```csharp title="ProjectileSpawner.cs"
using AtlasNet;
using UnityEngine;

public sealed class ProjectileSpawner : NetworkBehaviour
{
    [SerializeField] private NetworkObject projectilePrefab;
    [SerializeField] private Transform muzzle;

    private void Update()
    {
        if (IsOwner && Input.GetMouseButtonDown(0))
            RequestFireRpc();
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
    private void RequestFireRpc()
    {
        NetworkManager.RequestSpawn(
            NetworkObject, projectilePrefab, muzzle.position, muzzle.rotation);
    }
}
```

Assign the registered projectile prefab and the replicated weapon/muzzle transform in the Inspector. This example demonstrates spawning only: add server ammo/cooldown checks and a correct replicated shot origin for your game. Do not derive remote fire from a local-only camera.

`RequestSpawn` returns **void**. Joined-worker requests are asynchronous; initialize the new projectile in its own `OnNetworkSpawn` rather than expecting an immediately returned instance. The current overload sends prefab, pose, and optional owner—not an arbitrary per-spawn gameplay payload.

For example, a projectile can derive initial direction from its spawn rotation and speed from its prefab configuration. It must simulate only while `HasAuthority`, replicate its server-written transform, and transfer any private velocity/lifetime state needed for handoff.

## Despawn the entity

From its authoritative gameplay logic:

```csharp
if (HasAuthority)
    NetworkObject.Despawn();
```

Or call `NetworkManager.Despawn(obj)` on an eligible server. A joined worker requests canonical deletion from the coordinator. Do not use ordinary `Destroy` on one replica as a substitute for deleting a live network entity.

## Interest eviction is not deletion

When interest no longer includes an entity, the framework can remove a **local replica** while that entity continues to exist elsewhere. Its identity and authority do not change merely because a client/worker no longer sees it.

`OnNetworkDespawn` is a local network-lifecycle callback. Use it to release local subscriptions and resources; it does not mean “this entity permanently died in the world.” Code that awards a kill or deletes persistent data should use an explicit authoritative gameplay event.

`NetworkManager.TryGet(id, out obj)` and `SpawnedObjects` concern **resident local copies**. A failed lookup is not proof the entity has been canonically despawned.
