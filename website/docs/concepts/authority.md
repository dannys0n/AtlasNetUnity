---
title: Ownership and authority
description: Understand controlling sessions, simulation workers, channel writers, and stable entity identity.
---

## Three questions, three checks

| Question | Check | Typical use |
| --- | --- | --- |
| Is this process running a server role? | `IsServer` | Server-only infrastructure or diagnostics |
| Does this client's session control this entity? | `IsOwner` | Input, local camera, HUD, owner-written aim |
| May this local copy simulate this entity now? | `HasAuthority` | Movement simulation, damage, AI, physics, canonical gameplay state |

A worker can be a server but hold a **ghost** of an entity authored elsewhere. That copy has `IsServer == true` and `HasAuthority == false`. Using only `IsServer` to run entity gameplay would accidentally simulate the ghost too.

```csharp
public override void OnNetworkTick()
{
    if (!HasAuthority) return;

    // Simulate this entity here, not on every server that sees it.
}
```

Check authority when the work runs. Do not cache it once in `Start` or `OnNetworkSpawn`: it can change during the entity's lifetime.

## Ownership does not grant every write

`OwnerSession` identifies the controlling session. It does not make every field or component client-authoritative.

For example, one player may have:

- Owner-written yaw/pitch for immediate mouse look.
- Server-written position from keyboard input intent.
- Server-written health and ammo.
- Owner-local camera and HUD that are not network objects.

Configure writers independently through `NetworkTransform` and `NetworkVariable`. A client-authoritative movement example grants movement channels, not blanket permission to spawn items, apply damage, or overwrite another entity.

## Identity survives handoff

| Identifier | Meaning | Lifetime |
| --- | --- | --- |
| `PrefabId` | Which registered prefab to instantiate | Asset identity; generated in the editor |
| `EntityId` | Which runtime network entity this is | Stable while that entity exists, including across handoffs |
| `OwnerSession` | Controlling client session | Separate from worker assignment and transport connection |
| `SimulationWorker` | Current local-backend simulation assignment | May change during handoff |
| `AuthorityEpoch` | Version of that assignment | Advances on committed handoff |

An `EntityId` is not a persistent account/database ID. Despawning and later spawning a similar object creates a new entity. Unknown IDs can appear as interest changes; a worker is not expected to have seen every entity before.

Use `AtlasNet.EntityId` explicitly if Unity's own `EntityId` type creates an ambiguous reference.

## Handoff is not ownership transfer

The controlling client can remain the same while simulation moves to another worker. Entity-directed calls continue to target the entity's **current** authority. Gameplay should not store worker addresses or open worker connections.

```csharp
public override void OnSimulationAuthorityChanged()
{
    // Re-evaluate local simulation, timers, or services.
    // HasAuthority is the current permission, not a cached assumption.
    UnityEngine.Debug.Log($"Entity {NetworkObject.EntityId}: authority here = {HasAuthority}");
}
```

Treat this callback as an opportunity to re-evaluate authority. Do not use the number of callbacks as a count of committed handoffs; the local implementation can also notify during handoff transitions.

## A ghost is a local replica

A worker ghost gives local server logic a view of an interested entity authored elsewhere. It can render and consume received state, but should not author that entity's state. Cross-entity effects must route to the target's authority rather than directly mutating a ghost.

Ghosts are not whole neighboring worlds. The first server's global directory also does not mean it should have a live Unity copy of every entity. See [interest and handoff](../guides/interest-handoffs.md) for the local residency model.
