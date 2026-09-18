# AtlasNet Unity Framework — Feature Scope and Essentials

## 1. Purpose

AtlasNet is a Unity networking framework for persistent worlds whose authoritative simulation can move between multiple server workers.

The gameplay-facing API should feel familiar to developers who have used Unity Netcode for GameObjects (NGO), FishNet, or similar frameworks:

- `NetworkObject`
- `NetworkBehaviour`
- replicated variables
- RPC attributes
- spawn and despawn operations
- ownership/authority checks
- network lifecycle callbacks

The defining difference is that there is no single permanent server process behind those concepts. An entity's simulation authority may move between workers while its identity, controlling player, observers, and gameplay code remain stable.

> Design goal: gameplay developers work with entities and gameplay intent, not worker addresses, partitions, or routing tables.

This document defines the first prototype's conceptual scope. It is intentionally broader than the first implementation so features can be reviewed, cut, or deferred without losing the overall architecture.

## 2. Prototype Priorities

The first prototype should prove four things:

1. A familiar Unity-facing programming model can hide distributed authority.
2. An entity can retain one stable identity while simulation authority moves between workers.
3. RPCs and replication can continue across an authority handoff without gameplay code addressing a worker directly.
4. All of this can be demonstrated locally, inspected clearly, and tested with Unity Multiplayer Play Mode.

Priority labels used below:

- **Essential** — required to prove the core model.
- **Prototype** — should exist in a deliberately simple form.
- **Later** — preserve an extension point, but do not fully implement yet.
- **Non-goal** — explicitly outside the first prototype.

## 3. Conceptual Layers

```text
Gameplay API
  NetworkObject, NetworkBehaviour, variables, RPCs, spawn/despawn
                         |
Replication and Messaging
  serialization, snapshots, observers, ticks, RPC dispatch
                         |
Distributed Authority
  EntityId, authority epoch, handoff, worker ghosts
                         |
World Routing
  interest cells, entity directory, worker-to-worker routing
                         |
Backend and Transport Adapters
  local dummy backend now; production services later
```

The upper layer should remain stable even when lower-layer implementations change.

## 4. Stable Entity Identity

**Priority: Essential**

Every networked entity has a globally stable `EntityId` that is independent of:

- its current worker;
- its controlling session;
- its connection;
- its scene or interest cell;
- its current authority state.

```csharp
public readonly record struct EntityId(ulong Value);
```

```text
EntityId 829381
  Worker A authority
        -> handoff
  Worker B authority
        -> handoff
  Worker C authority

The EntityId is always 829381.
```

Avoid compound identities such as `Worker4/Object281`; they encode a temporary placement into permanent identity.

## 5. Input Authority vs. Simulation Authority

**Priority: Essential**

AtlasNet separates two concepts that must not be treated as synonyms.

### 5.1 Input authority

Input authority identifies the logical client session allowed to submit player input for an entity.

```text
Player entity 100
Input authority: Session 72
```

### 5.2 Simulation authority

Simulation authority identifies the worker currently allowed to mutate the entity's canonical gameplay state.

```text
Player entity 100
Simulation authority: Worker A
```

When a player crosses a worker boundary, input authority normally remains with the same session while simulation authority moves:

```text
Before                    After
Session 72: input         Session 72: input
Worker A: simulation      Worker B: simulation
```

Observers are a third, separate set. They receive state but do not gain permission to mutate it.

## 6. Authority Epochs

**Priority: Essential**

Each entity has a monotonically increasing authority epoch. The epoch acts as a fencing token against stale messages and split-brain authority.

```text
Entity 8201 on Worker A: epoch 41
Entity 8201 on Worker B after handoff: epoch 42

Late message carrying epoch 41 -> reject, redirect, or retry by policy
```

Authoritative state updates, authority-directed RPCs, and handoff messages should carry both `EntityId` and `AuthorityEpoch`.

The prototype only needs deterministic stale-message rejection and useful logs. Production lease timing, quorum, and failure recovery are later concerns.

## 7. Transient Authority Handoff

**Priority: Essential**

Authority transfer is a protocol, not a destroy-and-respawn operation. A conceptual happy path is:

```text
Source Worker        Coordinator        Destination Worker
     | prepare ---------->|                     |
     |                    |---- prepare ------->|
     |                    |<------ ready -------|
     |------ final authoritative snapshot ----->|
     |                    |---- grant epoch N+1>|
     |<--- revoke --------|                     |
     | becomes ghost      |       becomes authority
```

Required prototype properties:

- the `EntityId` does not change;
- input authority does not change merely because simulation authority moves;
- only one worker accepts mutations for the current epoch;
- the destination receives a complete transferable state snapshot;
- the source becomes a ghost or removes its copy according to interest;
- queued authority RPCs are either forwarded or retried once against the new route;
- callbacks and inspector state make the transition visible.

Short overlap while preparing the destination is acceptable. Dual canonical writers are not.

## 8. RPC Intent

**Priority: Essential**

RPC names describe gameplay intent, not a physical destination.

### `AuthorityRpc`

Runs on the worker holding simulation authority for the target entity.

```csharp
[AuthorityRpc]
private void ApplyDamage(int amount)
{
    Health.Value -= amount;
}
```

It may be invoked by a client, another worker, or local authoritative code. Routing is transparent.

### `ObserversRpc`

Runs on the clients currently observing an entity. It is suited to transient presentation events such as sound, animation, and visual effects.

### `TargetRpc`

Runs for a specific logical client session, not a raw transport connection or worker address.

The prototype should validate sender permission, entity existence, payload shape, and current authority epoch. Production-grade abuse prevention is a later layer.

## 9. Replicated Variables

**Priority: Essential**

`NetworkVariable<T>` represents canonical state replicated from simulation authority to interested observers and worker ghosts.

```csharp
public readonly NetworkVariable<int> Health = new(100);
```

Prototype behavior:

- only simulation authority may write authoritative values;
- new observers receive current state;
- changed values are sent on a network tick;
- client and ghost copies can subscribe to change notifications;
- handoff state includes current variable values;
- unauthorized writes fail loudly in development builds.

The prototype may send whole values rather than optimized deltas.

## 10. Spawn, Visibility, and Despawn

**Priority: Essential**

Spawn creates a canonical network entity with a stable entity ID and registered prefab ID.

```text
SpawnMessage
  EntityId
  PrefabId
  AuthorityEpoch
  InitialTransform
  InitialReplicatedState
```

Despawn removes the canonical entity from the world.

The system must keep these distinct:

- **despawn** — entity no longer exists;
- **interest exit** — this observer stops receiving the entity;
- **disconnect** — a transport connection ended;
- **authority transfer** — the same entity moves to another worker.

A deterministic prefab registry is required across processes. Automated registry generation is useful later; a simple configured registry is enough for the prototype.

## 11. Sessions and Reconnect

**Priority: Prototype**

Long-lived player identity must not be the same as a short-lived transport connection.

```text
AccountId -> SessionId -> ConnectionId
                         -> input authority for EntityId
```

A basic reconnect flow should preserve the session and player entity:

```text
Session 50 / Connection 901 / Entity 1200
connection lost
short reconnect grace period
Session 50 / Connection 1182 / Entity 1200
input authority restored
```

The local prototype can use in-memory session tokens and a short grace period. Authentication providers, durable sessions, account services, and security hardening are non-goals.

## 12. Interest Management and Observers

**Priority: Prototype**

Clients and workers should receive only relevant entities. The initial policy can be a fixed two-dimensional grid:

```text
+-----+-----+-----+
|  X  |  X  |  X  |
+-----+-----+-----+
|  X  | YOU|  X  |
+-----+-----+-----+
|  X  |  X  |  X  |
+-----+-----+-----+
```

Subscribe to the current cell and a configurable neighbor radius. Entering interest spawns an observer copy; leaving interest removes that copy without despawning the canonical entity.

The abstraction should permit later policies such as distance, party, instance, line of sight, gameplay tags, or priority budgets. Those policies are not required now.

## 13. Worker Ghosts

**Priority: Essential**

A worker ghost is a non-authoritative replica kept on another simulation worker. Ghosts support:

- boundary awareness;
- nearby queries;
- short cross-worker interactions;
- warm authority handoff;
- simplified cross-boundary physics tests.

Ghost rules:

- ghosts never write canonical state;
- ghost physics is kinematic or otherwise non-authoritative;
- updates are tagged with authority epoch and tick;
- a ghost may be promoted during handoff;
- the old authority may be demoted to a ghost after handoff.

## 14. Worker-to-Worker RPC Routing

**Priority: Essential**

Gameplay code routes by entity, never by worker endpoint:

```text
AuthorityRpc(EntityId 500)
        -> authority resolver
        -> Worker B, epoch 12
        -> validate epoch and execute
```

This supports cases such as a projectile simulated by Worker A damaging a player simulated by Worker B.

The prototype may implement the resolver with an in-memory dictionary. The API boundary should allow a future distributed directory without changing gameplay code.

## 15. Basic Distributed Physics

**Priority: Prototype**

Each worker owns authoritative physics for entities it simulates. Ghost rigidbodies are driven by replicated state and do not produce canonical movement.

For short cross-worker interactions:

```text
Worker A detects its authoritative projectile overlapping a ghost target
  -> sends AuthorityRpc to target EntityId
Worker B validates and applies damage to its authoritative target
```

For tightly coupled or long-lived interactions—vehicles with passengers, joints, pushing, standing on moving platforms—the preferred future strategy is to co-locate authority for the interaction group.

The prototype demonstrates one simple boundary hit and documents its limitations. It does not attempt deterministic PhysX across processes, distributed rollback, or atomic multi-worker physics transactions.

## 16. Network Ticks and Time

**Priority: Essential**

AtlasNet needs a logical network tick independent of Unity render frames.

```csharp
NetworkTick Tick { get; }
double Time { get; }
float TickDeltaTime { get; }
```

Messages should include the tick at which their data was produced. The local coordinator may provide one shared tick source for all local workers.

Clock drift correction, global time synchronization, rollback, and client prediction can be added later. The first prototype establishes ordering and repeatable tick-driven tests.

## 17. Serialization

**Priority: Prototype**

One serialization abstraction should serve:

- RPC arguments;
- replicated variables;
- spawn state;
- snapshots;
- authority-transfer state.

A common envelope may conceptually include:

```csharp
public readonly struct MessageEnvelope
{
    public MessageType Type { get; init; }
    public EntityId Entity { get; init; }
    public NetworkTick Tick { get; init; }
    public AuthorityEpoch Epoch { get; init; }
    public SessionId? Sender { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}
```

Reflection and straightforward binary serialization are acceptable in the prototype. Source generation, bit packing, zero-allocation paths, compression, and schema evolution are later optimizations.

## 18. Backend Abstractions

**Priority: Essential**

Unity-facing systems should depend on narrow interfaces rather than a concrete local coordinator.

Expected boundaries include:

- authority resolution and updates;
- world/entity registration;
- worker and session messaging;
- interest subscriptions;
- tick/time source;
- session lifecycle.

The first implementation can be an in-process or loopback `LocalBackend`. A production backend must be replaceable without rewriting `NetworkBehaviour` gameplay components.

## 19. Multiplayer Play Mode Support

**Priority: Essential for demo workflow**

The local prototype should work with Unity Multiplayer Play Mode so one editor workflow can represent:

- one coordinator;
- two simulation workers;
- two or more clients;
- configurable latency, jitter, loss, or delayed messages when practical.

The exact process topology may evolve, but a developer must be able to reproduce the handoff demo locally without cloud infrastructure.

## 20. Debugging and Inspection

**Priority: Essential for trust**

Distributed behavior must be visible. At minimum provide a runtime panel or inspector showing:

- `EntityId` and prefab ID;
- input-authority session;
- simulation-authority worker;
- authority epoch;
- current tick;
- authoritative, ghost, or client-observer role;
- interest cell and observer count;
- last handoff and RPC route;
- rejected stale messages.

Logs should include entity, epoch, worker, session, tick, and message type as structured fields. The demo should make expected transitions easy to distinguish from bugs.

## 21. Explicit Non-Goals for the First Prototype

The prototype does **not** need:

- a production distributed coordinator or consensus system;
- cloud orchestration, autoscaling, or fleet management;
- a database-backed persistent world;
- account registration or production authentication;
- anti-cheat or denial-of-service protection;
- seamless recovery from arbitrary worker crashes;
- dynamic load balancing or automatic repartitioning;
- production transport selection or transport benchmarks;
- global deterministic physics;
- full client prediction, rollback, or lag compensation;
- delta compression, bit packing, source-generated serializers, or bandwidth tuning;
- scene streaming or content delivery systems;
- matchmaking, parties, chat, inventory, quests, or other game services;
- compatibility parity with every NGO or FishNet feature;
- a stable public package/API promise before the prototype is validated.

Persistence is intentionally separate from replication and authority transfer. A database must not participate in every entity handoff.

## 22. First-Pass Success Criteria

The framework concept is validated when a local demo can show all of the following:

1. Two clients observe the same spawned entity.
2. One session supplies input while one worker owns simulation.
3. The entity crosses a boundary and changes simulation worker without changing `EntityId` or session ownership.
4. The epoch increments and an intentionally late old-epoch update is rejected.
5. Replicated variables continue updating after handoff.
6. An `AuthorityRpc` reaches the correct worker before and after handoff.
7. An `ObserversRpc` and `TargetRpc` reach their intended recipients.
8. A worker-to-worker boundary interaction applies an authoritative result once.
9. Disconnect and reconnect reattach the same session to the same entity.
10. The inspector clearly explains every role and route involved.

Anything beyond these criteria should be treated as optional until this core loop is reliable.
