---
title: API quick reference
description: A compact source-linked reference for AtlasNet Unity's current public gameplay API.
---

This is a hand-written guide to the current small API, not generated coverage of every public member. Full source lives under [Packages/com.atlasnet.unity/Runtime](https://github.com/dannys0n/AtlasNetUnity/tree/main/Packages/com.atlasnet.unity/Runtime). Names are intentionally familiar to NGO users, but AtlasNet is not a drop-in NGO binary/API replacement.

## NetworkManager

| Member | Meaning / constraint |
| --- | --- |
| `StartHost()` | Start the first local server plus a local client |
| `StartServer()` | Start the first local coordinator/server without a client |
| `StartWorker()` | Join an existing world as an additional simulation worker |
| `StartClient()` | Join the world as a client |
| `Stop()` | Stop this local session/role |
| `Spawn(prefab, position, rotation, owner = default)` | Synchronous canonical first-server spawn; returns `NetworkObject` |
| `RequestSpawn(source, prefab, position, rotation, owner = default)` | Spawn request from an authoritative entity, works on joined workers; returns `void` |
| `Despawn(obj)` | Canonical despawn or eligible authoritative-worker request |
| `TryGet(id, out obj)` | Look up a **local resident** entity replica |
| `SpawnedObjects`, `SpawnedCount` | Live local replicas, not every world entity |
| `TrackedEntityCount` | Canonical directory size on the first server; not proof of local simulation |
| `Tick`, `TickRate` | Current network tick and configured cadence |
| `SessionJoined`, `SessionLeft` | Session lifecycle events; not a durable account/reconnect system |
| `PlayerSpawnPosition` | Optional session-to-position callback for automatic player spawning |
| `IsRunning`, `IsServer`, `IsClient`, `IsHost`, `IsWorker` | Process/session role state |
| `LocalWorkerId`, `WorkerCount`, `LocalAuthorityCount`, `GhostCount`, `PendingHandoffCount` | Local-worker diagnostics |

See [manager source](https://github.com/dannys0n/AtlasNetUnity/blob/main/Packages/com.atlasnet.unity/Runtime/NetworkManager.cs) and its [local-worker partial](https://github.com/dannys0n/AtlasNetUnity/blob/main/Packages/com.atlasnet.unity/Runtime/NetworkManager.LocalWorkers.cs).

## NetworkObject and NetworkBehaviour

| Surface | Common members |
| --- | --- |
| `NetworkObject` | `EntityId`, `PrefabId`, `OwnerSession`, `SimulationWorker`, `AuthorityEpoch`, `IsSpawned`, `IsOwner`, `HasAuthority`, `Manager`, `Despawn()` |
| `NetworkBehaviour` | `NetworkObject`, `NetworkManager`, `IsSpawned`, `IsOwner`, `HasAuthority`, `IsServer`, `IsClient`, `IsHost`, `OwnerSession` |
| Public overridable lifecycle | `OnNetworkSpawn()`, `OnNetworkDespawn()`, `OnNetworkTick()`, `OnSimulationAuthorityChanged()` |
| Protected handoff hooks | `WriteHandoffState(NetWriter)`, `ReadHandoffState(NetReader)` |
| Protected advanced snapshot hooks | `WriteExtraSnapshot(NetWriter)`, `ReadExtraSnapshot(NetReader)` |
| Protected cross-entity request helper | `SendAuthorityFrom(NetworkObject source, string name, params object[] arguments)` on the target behaviour |

Attach behaviours to the network root. Override pairs consistently and keep matching prefab schemas. `WriteExtraSnapshot` is an advanced custom-state extension; most game state should start with variables/components rather than manual snapshot code.

## Replicated values and calls

```csharp
new NetworkVariable<int>(100); // Everyone reads; authority writes.

new NetworkVariable<float>(0f,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Owner);
```

Use `.Value` to read/write and `.OnValueChanged` for `(previous, current)` notifications. `Set(value)` and `Changed` are equivalent write/event surfaces currently present in source; the guides use the more NGO-familiar names.

```csharp
[Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
private void MoveRpc(Vector2 input, float yaw, bool jump) { }
```

See [variables](../guides/network-variables.md) for supported types and [RPCs](../guides/rpcs.md) for destinations, special session parameters, and generator constraints.

## Serialization

`NetWriter.Write(...)` and the corresponding `NetReader.ReadInt`, `ReadFloat`, `ReadBool`, `ReadString`, `ReadVector3`, and `ReadQuaternion` are used for paired handoff/custom snapshots. There are also lower-level identity/byte operations in [NetBuffer.cs](https://github.com/dannys0n/AtlasNetUnity/blob/main/Packages/com.atlasnet.unity/Runtime/NetBuffer.cs).

Manual serialization must have a matching read/write layout. The framework does not infer arbitrary object graphs or provide a public custom-struct serializer registration system.

## Built-in components

| Type | Important configuration / API |
| --- | --- |
| `NetworkPrefabsList` | Reusable `.asset` prefab references, assigned on the manager |
| `NetworkTransform` | Inspector target, position/rotation toggles, independent `TransformWriter.Server`/`Owner`, interpolation |
| `NetworkRigidbody` | Root dynamic physics, server/server transform, automatic kinematic replica handling |
| `NetworkAnimator` | Animator reference, `AnimatorWriter.Server`/`Owner`, `SetTrigger(string)` |
| `NetworkInterestSource` | Local-demo `Radius` and `ExitPadding` |

Some configuration is serialized Inspector data rather than public setters. See [component setup](../guides/prefabs-components.md) instead of assuming NGO's entire runtime configuration API exists.
