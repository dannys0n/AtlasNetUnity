---
title: Troubleshooting and limits
description: Fix common local-demo setup mistakes and distinguish prototype features from production guarantees.
---

## Missing NetworkObject prefab

If startup reports that a `NetworkPrefabsList` contains a missing prefab:

1. Select the exact `.asset` named in the exception.
2. Remove missing entries and assign the saved prefab assets with root `NetworkObject` components.
3. Confirm the manager uses that list and its default player is included.
4. Let Unity complete its import/refresh before starting again.

An imported old sample can retain stale references after a package update. Do not regenerate all identities to hide the problem; repair the actual list/references.

## No input or missing-axis exceptions

The sample uses legacy `UnityEngine.Input`. Enable Input Manager or Both under Active Input Handling, and restore missing named axes in Project Settings. The framework itself does not require legacy input; your custom motor may use another input system.

## Clients spawn outside the map

Inspect the manager's **World Minimum/Maximum X/Z** and the launcher's `PlayerSpawnPosition` callback. Copying networking into a differently sized shooter map does not automatically adapt spawn points or world bounds. Keep spawn points inside valid geometry; changing an interest radius will not repair a bad spawn position.

## Joining waits until a window is focused

Check `Application.runInBackground` and editor/player background behavior. The sample launcher enables it. A paused Unity update loop can delay both transport processing and ticks. This is distinct from network latency.

## The second server cannot start

Only the first instance should listen as the world coordinator. Use **Join as Worker** in another server instance, with the same address and port. Two independent `StartServer` calls on the same endpoint collide rather than forming one world.

## Server movement or yaw fights the owner

Check transform writers first. Server CharacterController movement uses server-written position and owner-written root rotation. The motor uses yaw accompanying input to compute direction, without overwriting the owner camera's raw look.

For Rigidbody simulation, both root writers must be Server; put mouse aim on a separate pivot. Remove duplicate motors/transform writers and check that animation root motion is not competing.

## Kinematic velocity warning

Do not assign `linearVelocity` or apply player forces on a non-authoritative Rigidbody. Gate physics on `HasAuthority` and confirm `!body.isKinematic`. `NetworkRigidbody` intentionally makes client/ghost copies kinematic.

## Duplicate impacts or missing animation

Check which machine executes each cosmetic event. `SendTo.Everyone` includes immediate local execution; also playing the effect at the call site duplicates it. Keep authoritative hit/damage separate from observer presentation.

Use `NetworkAnimator.SetTrigger` for network trigger events, replicate the intended Animator, and derive remote effects from the current ghost weapon hierarchy. Root yaw alone does not replicate child pitch. First-person overlay materials can make ghost weapons draw through walls regardless of networking.

## RPC or schema errors

RPC methods must end in `Rpc`, be instance `void` methods, and use supported payloads. Remove overloads, `ref`/`out`, generic behaviours, and exception handlers from attributed bodies. Call only while network-spawned.

Use matching package revisions, prefab registrations, behaviour order, and variable/RPC layouts in all instances. The current local protocol version is `9`, an implementation detail—not a stable production protocol. A package update can require updating every participant and refreshing imported samples.

## Why does a server know about a distant entity?

Separate directory tracking, packet routing, resident replicas, and permission to simulate. On the coordinator, a tracked identity does not mean a visible Unity copy. On a worker, an interested ghost does not grant authority. Use `SpawnedCount`, `GhostCount`, and the entity's `HasAuthority` together; global logs alone cannot establish hidden authorship.

A client can receive all entities on its current authoring worker even outside its cross-worker radius. This is the current intentional policy, not necessarily a filtering bug. See [interest](./guides/interest-handoffs.md).

## What this prototype does not promise

- Client-side prediction, reconciliation, rewind, or lag compensation.
- Real AtlasNet C++ integration, ingress retargeting, production worker links, or multiple selectable transports.
- UDP/unreliable RPC channels; the local backend currently uses reliable ordered TCP.
- Seamless reconnect, session resumption, worker crash recovery, persistence, or durable identities.
- Shared/distributed PhysX, deterministic cross-worker contacts, or automatic force/collision replication.
- Automatic NGO-style in-scene network object discovery, arbitrary payload/collection serialization, or drop-in NGO compatibility.
- Production authentication/anti-cheat, version migration, or a measured maximum entity capacity.

Local handoff, interest, and ghost APIs exist to exercise the framework's semantics. A successful localhost demonstration is not proof of correctness under production failures or at MMO scale.
