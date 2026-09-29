---
title: Prefabs and components
description: Build a registered player prefab and configure AtlasNet's Unity components.
---

## Start with a normal Unity prefab

Clone your working single-player prefab before networking it. Keep the source as a comparison, and leave meshes, animation controllers, cameras, and weapon pivots in their original hierarchy unless networking genuinely requires a change.

A basic setup can look like this:

```text
Scene
├── NetworkManager + sample DemoLauncher
└── LocalDebugView (optional local-only sample prefab)

Player prefab root
├── NetworkObject
├── CharacterController
├── Your movement/look NetworkBehaviours
├── NetworkTransform
├── NetworkInterestSource (optional local-worker interest)
└── Visuals / aim pivot / camera
```

**Attach each `NetworkBehaviour` to the same GameObject as its `NetworkObject`.** The current runtime discovers behaviours on that object, not recursively through all children. Components such as `NetworkTransform` and `NetworkAnimator` can reference child targets from the root.

You do not need to add networking to every visual child. Keep local cameras, HUD, particles, and render-only helpers ordinary Unity components unless they need their own network lifecycle.

Put your own scripts in `Assets` and import the `AtlasNet` namespace. If they belong to a custom assembly definition, add a reference to the package's **AtlasNet.Unity** assembly. Keep the package's editor/code-generation assemblies out of gameplay assembly references.

## Register spawnable prefabs

1. Add `NetworkObject` to the prefab root and save the prefab asset.
2. Create **AtlasNet → Network Prefabs List** through Unity's asset creation menu.
3. Add the player and other spawnable `NetworkObject` prefabs to the list's **Prefabs** array.
4. Assign the list to **NetworkManager → Network Prefabs Lists**.
5. Assign the same player to **Default Player Prefab** if it should spawn for connecting sessions.

The default player must also be registered. Leaving it empty is valid if you want to manage player spawning yourself.

`Prefab ID` is generated from the saved prefab's Unity asset identity and displayed read-only. It is not a number to obtain from the backend. Preserve prefab `.meta` files when moving a project between machines; intentionally duplicated prefabs have different identities and need their own registrations.

Every instance must use matching prefab registrations, behaviour order, variable fields, and RPC signatures. Do not run one client with yesterday's prefab against today's server.

:::warning Scene placement is not network spawning
Putting a `NetworkObject` in a scene or calling ordinary `Instantiate` does not automatically register/spawn a network entity. Use the manager's spawn APIs. Automatic NGO-style in-scene object discovery is not implemented.
:::

## NetworkManager Inspector

| Section / field | Purpose |
| --- | --- |
| **Network Settings → Tick Rate** | Networking and `OnNetworkTick` cadence; default `30` |
| **Local Address / Port** | Current local TCP endpoint; defaults `127.0.0.1:7777` |
| **Local Worker Regions → Automatic Handoffs** | Enable local backend boundary-based authority movement |
| **World Minimum X/Z / World Maximum X/Z** | Local demo region bounds; match the playable map |
| **Boundary Margin** | Local boundary policy margin; not a gameplay authority test |
| **Prefab Settings → Default Player Prefab** | Optional automatically spawned player |
| **Network Prefabs Lists** | Reusable `.asset` registrations for spawnable prefabs |

In Play Mode, the Inspector shows role, tick, tracked entity IDs, local replicas, workers, authority, ghosts, and pending handoffs. These distinguish global directory tracking from actual local Unity objects.

## Choose transform writers

`NetworkTransform` synchronizes position and rotation with independent writers. **Target** defaults to its own transform; assign a child explicitly if needed.

| Setup | Position Writer | Rotation Writer |
| --- | --- | --- |
| Client-authoritative movement/root yaw | `Owner` | `Owner` |
| Server-authoritative movement, owner root yaw | `Server` | `Owner` |
| Dynamic Rigidbody root | `Server` | `Server` |

Enable **Position** and **Rotation** only for channels this component should synchronize. These are whole position/rotation channels, not NGO's complete per-axis/scale configuration surface.

**Interpolate Remote Copies** smooths received transform channels on clients over a network tick. Locally owner-written channels are not corrected by those received server updates. Received server-written position can still be interpolated for the owning client; that is not client-side prediction.

For a physics player, keep owner-written look on a separate visual/aim pivot. Do not make the Rigidbody root owner-rotated while also configuring it for server-authored physics.

## Other components

| Component | When to add it |
| --- | --- |
| `NetworkRigidbody` | Server-simulated dynamic 3D physics; requires root `Rigidbody` and server/server `NetworkTransform` |
| `NetworkAnimator` | Replicate one Animator's parameters/states; use its `SetTrigger` for trigger events |
| `NetworkInterestSource` | Give an owned player a local-worker interest radius and exit padding |
| `LocalDebugView` | Optional sample visualization, not a registered network prefab |

Only enable the owning player's camera, audio listener, input, and HUD. Remote visuals must use ordinary world depth testing; networking will not fix a first-person overlay shader that draws other players through walls.

See [movement](./movement.md), [physics and animation](./physics-animation.md), and [interest/handoff](./interest-handoffs.md) for practical usage.
