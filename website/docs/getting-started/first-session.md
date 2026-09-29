---
title: Run your first session
description: Start a local world, join workers and clients, and compare authority modes.
---

## Pick a scene

Open a scene from the imported sample's `Scenes` folder. Use the same scene and package revision in every participating instance.

| Scene | What to examine |
| --- | --- |
| `ClientCrossServer` | Owner-written movement and aim, cross-worker interest, and boundary handoff |
| `ServerCrossServer` | Input intent sent to a server-authoritative movement controller; aim stays client-written |
| `ScaleDemo` | Multiple moving entities and local-worker residency/metrics; not a universal capacity benchmark |
| `PhysicsDemo` | A separate physics player and dynamic networked rigidbodies |

The supplied simple movement examples currently use `CharacterController`. The physics scene is a separate Rigidbody example. The separate shooter repository is not required to run these scenes.

## Start one world, then join it

For an easy-to-read multi-worker test, use four instances:

1. In the first instance, select **Start World (Server)**. This is the coordinator and first simulation worker.
2. In the second instance, select **Join as Worker**. It connects to the existing world rather than listening on a second world port.
3. In the remaining two instances, select **Join as Client**.

These are the sample launcher's labels. The `NetworkManager` Inspector also exposes **Start Server**, **Join Worker**, and **Start Client**. Other sample configurations offer **Start Host**, combining a client and coordinator in one process.

Leave **Local Address** as `127.0.0.1`, **Port** as `7777`, and **Tick Rate** as `30` for a first localhost test. All instances must agree on their prefab registrations and gameplay schema.

:::note Host versus joined client
A host can hide timing and authority mistakes because its client and server share a process. Always test at least one separately joined client, not only a host-controlled player.
:::

## Try the controls

The simple player uses WASD/legacy movement axes, mouse look, and Space/`Jump`. **F** runs the sample RPC/flash interaction. It demonstrates events and state; it is not a complete validated weapon system.

Server views are top-down orthographic: **middle-mouse drag** pans and the **scroll wheel** zooms. Joined clients can toggle debug overlays with **F8** without changing their camera position.

For multi-worker scenes, move a player across a highlighted boundary. Observe **Simulation Worker** and **Authority Epoch** on its `NetworkObject`, plus the runtime metrics. Entity identity should stay unchanged while authority moves.

The sample launcher enables `Application.runInBackground`. If you replace it, set that yourself when testing windowed instances; otherwise an unfocused player can appear to pause networking.

## Verify the basics

Before adapting your own game, check these outcomes in your environment:

- Both joined clients spawn, move, and see each other when eligible for interest.
- Client-written aim remains responsive while server-authoritative movement follows the server.
- Crossing a boundary changes the authoring worker without changing `EntityId`.
- An interested ghost does not run authoritative movement, damage, or dynamic physics.
- A late observer receives current persistent values; an old cosmetic RPC is not replayed.
- Stopping and relaunching instances produces no prefab/protocol errors.

This is a manual smoke checklist, not a claim that every combination has passed. Use the [debugging guide](../guides/interest-handoffs.md#debug-views) and [troubleshooting](../troubleshooting.md) to investigate failures.

## Build-based testing

Add the chosen scene to your build's scene list. Run one window as server, then other windows as worker/client using the launcher. The sample also recognizes role flags such as `-atlas-server`, `-atlas-worker`, and `-atlas-client`; these belong to the **sample launcher**, not a framework-wide command-line API.

Read [authority and ownership](../concepts/authority.md) next, then [prefab setup](../guides/prefabs-components.md).
