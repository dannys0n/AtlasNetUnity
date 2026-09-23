# AtlasNet — Local Dummy-Backend Prototype and Demo Plan

> **Status: deferred two-worker handoff plan.** This is not the first demo's build order or completion checklist. The current demo uses one local Unity server and clients; see [AGENTS.md](../AGENTS.md) for its scope and milestones.

## 1. Objective

Build the smallest local environment that convincingly demonstrates AtlasNet's Unity developer experience and mobile simulation authority.

The prototype is not a production MMO backend. It is an executable architectural test that should answer:

- Does the gameplay API remain understandable during worker handoff?
- Are stable entity identity, input authority, simulation authority, and observers truly independent?
- Can replication and RPC routing survive authority movement?
- Can developers reproduce and inspect the behavior locally?
- Which planned abstractions are useful, awkward, or unnecessary?

## 2. Recommended Local Topology

```text
                         Local Coordinator
                    entity directory, epochs,
                    sessions, ticks, routing
                         /            \
                        /              \
               Simulation Worker A   Simulation Worker B
                  west authority         east authority
                  east-border ghosts    west-border ghosts
                        \              /
                         \            /
                         Client 1   Client 2
```

The first implementation may host these logical nodes in one process if each node has an explicit identity, isolated world state, and all communication passes through message interfaces. Separate local processes are useful once lifecycle and routing work, but are not required on day one.

Do not shortcut distributed behavior by sharing Unity object references between logical nodes. Even in-process messages should serialize through the same envelope used by loopback transport so invalid assumptions appear early.

## 3. Prototype Components

### 3.1 Local coordinator

Responsibilities:

- allocate stable `EntityId` values;
- register workers and sessions;
- store `EntityId -> WorkerId + AuthorityEpoch`;
- advance or distribute the shared prototype tick;
- coordinate prepare/commit authority handoffs;
- maintain session-to-connection mappings;
- expose state to debugging tools.

Simple storage is sufficient:

```csharp
Dictionary<EntityId, AuthorityLocation> authorityDirectory;
Dictionary<SessionId, ConnectionId> activeConnections;
Dictionary<WorkerId, WorkerRegistration> workers;
```

### 3.2 Local message bus

Responsibilities:

- route messages to worker, authority, session, or observer set;
- serialize and deserialize envelopes;
- optionally inject latency, jitter, loss, reordering, and duplication;
- record routes for the debug timeline;
- support one bounded reroute for authority movement.

Start with reliable ordered delivery by default. Add adverse-network toggles only where they help validate stale-message handling.

### 3.3 Simulation workers

Each worker maintains:

- authoritative entities for assigned cells/domains;
- ghost entities required near boundaries;
- its own Unity physics scene or an equivalent isolated simulation context;
- replicated-variable state;
- RPC dispatch tables;
- interest information for its entities;
- authority epoch validation.

### 3.4 Local clients

Each client maintains:

- a transport connection and logical `SessionId`;
- input authority for its player entity;
- observer copies within its interest set;
- input submission by tick;
- presentation of replicated state and observer RPCs.

### 3.5 Prefab registry

The prototype needs a deterministic mapping such as:

```text
1  Player
2  TargetDummy
3  Projectile
4  MovingCrate
```

A ScriptableObject or other simple asset is enough. Validation should detect duplicate IDs and missing prefabs.

## 4. Demo World

Use one small scene split visually into west and east authority domains.

```text
Worker A domain                     Worker B domain

spawn                 boundary                  target
  P ----------------------|---------------------- T
                           |
                  ghost overlap band
```

Recommended actors:

- one player controlled by Client 1;
- one observing Client 2;
- one stationary or moving target owned by Worker B;
- optional projectile or interaction trigger;
- visible boundary and ghost-overlap band.

Color coding should make roles obvious:

- authoritative on Worker A;
- authoritative on Worker B;
- worker ghost;
- client observer;
- entity currently handing off.

Colors are presentation aids only; labels must also be present for accessibility and screenshots.

## 5. End-to-End Demo Script

### Scenario A: spawn and initial replication

1. Start coordinator, Workers A and B, and two clients.
2. Establish Client 1 session and spawn one player with a stable `EntityId`.
3. Grant Client 1 input authority and Worker A simulation authority at epoch 1.
4. Replicate the player to both interested clients.
5. Create a ghost on Worker B as the player enters the border overlap band.

Expected evidence:

- all copies show the same `EntityId`;
- only Client 1 has input authority;
- only Worker A has simulation authority;
- Worker B is visibly a ghost;
- replicated health and transform match.

### Scenario B: transparent authority handoff

1. Client 1 keeps moving east.
2. Coordinator prepares Worker B using its warm ghost.
3. Worker A sends the final snapshot at a known tick.
4. Coordinator commits Worker B at epoch 2.
5. Worker A loses authority and remains a ghost while still interested.
6. Client 1 continues controlling the same entity.

Expected evidence:

- no destroy-and-respawn identity change;
- `EntityId` and `SessionId` remain stable;
- simulation authority changes A -> B;
- epoch changes 1 -> 2;
- movement and variable replication continue;
- authority callbacks fire once in the expected order.

### Scenario C: stale message fencing

1. Delay an epoch-1 authoritative update or RPC before handoff.
2. Complete handoff to epoch 2.
3. Release the delayed epoch-1 message.

Expected evidence:

- Worker B rejects the stale message;
- canonical state is unchanged;
- the event appears in the debug timeline with entity, old epoch, current epoch, and tick.

### Scenario D: RPC families

Demonstrate:

- an `AuthorityRpc` invoked before handoff executes on Worker A;
- the same gameplay method invoked after handoff executes on Worker B;
- an RPC routed during handoff is forwarded or retried at most once;
- an `ObserversRpc` plays an effect on interested clients;
- a `TargetRpc` reaches only the specified session.

### Scenario E: cross-worker interaction

1. An authoritative projectile or hit source on Worker A overlaps a ghost target owned by Worker B.
2. Worker A sends an authority-directed damage request to the target `EntityId`.
3. Worker B validates the request and changes its authoritative `Health` variable.
4. The changed value replicates to clients and Worker A's ghost.

Expected evidence:

- damage is applied exactly once;
- Worker A never mutates the target's canonical health;
- the route is visible in diagnostics.

### Scenario F: disconnect and reconnect

1. Disconnect Client 1 without logging out.
2. Keep its session in a short reconnect grace period and retain the player entity.
3. Reconnect using a new `ConnectionId` and the same session token.
4. Restore input authority and send a fresh snapshot.

Expected evidence:

- `ConnectionId` changes;
- `SessionId` and player `EntityId` remain stable;
- reconnect does not create a duplicate player;
- the client cannot send input while detached.

## 6. Authority Handoff State Machine

Keep the state machine explicit and small:

```text
Stable
  -> PreparingDestination
  -> TransferringFinalState
  -> CommittingNewEpoch
  -> Stable

Failure before commit -> AbortToSource
Failure after commit  -> New authority remains canonical; old epoch is fenced
```

Suggested handoff ticket:

```csharp
public readonly record struct HandoffTicket(
    Guid TransferId,
    EntityId Entity,
    WorkerId Source,
    WorkerId Destination,
    AuthorityEpoch SourceEpoch,
    AuthorityEpoch DestinationEpoch);
```

Only one in-flight handoff per entity is needed in the prototype. Timeouts may abort before commit. Worker crash recovery after commit is explicitly deferred.

## 7. State Included in Handoff

The transferable snapshot should include at least:

- `EntityId` and prefab ID;
- source and destination epochs;
- effective network tick;
- transform and rigidbody velocity;
- replicated variable values;
- input-authority `SessionId`;
- enabled network behaviours and component state needed by the demo;
- small amounts of explicit custom handoff state, if required.

Do not serialize arbitrary Unity engine object graphs. Use registered network components and deterministic component identifiers.

Transient worker-local resources—temporary caches, inspector objects, transport handles—must not enter the snapshot.

## 8. Replication Plan

Use two conceptually distinct replication paths even if they share code:

### Worker to client

- spawn/despawn and interest enter/exit;
- replicated variable changes;
- transform snapshots;
- observer RPC events.

### Worker to worker

- ghost spawn/remove;
- ghost state snapshots;
- handoff preparation/final state;
- authority-directed RPCs.

Prototype simplifications:

- full changed values rather than delta compression;
- fixed send rates;
- reliable messages except high-frequency input/transform experiments;
- one small set of serializable types;
- no bandwidth budget scheduler.

## 9. Interest and Ghost Plan

Divide the demo world into fixed grid cells. Assign western cells to Worker A and eastern cells to Worker B.

For clients:

- observe the current cell plus a configurable neighbor radius.

For workers:

- ghost entities within a configurable distance of the shared boundary;
- remove ghosts after they leave the overlap band and are not required by an active handoff.

This is deliberately a policy behind an interface. The prototype does not implement dynamic authority domains, hotspot splitting, or load balancing.

## 10. Basic Physics Plan

The authoritative worker runs dynamic physics for its entities. A worker ghost is kinematic and follows replicated transform/velocity.

Demonstrate one bounded cross-worker case:

```text
authoritative projectile on A
  -> overlap with kinematic ghost of target
  -> authority RPC to target
  -> validation and damage on B
```

Known limitations to display or document:

- overlap observations can arrive late;
- both sides may observe the same event, so requests need an interaction/event ID for deduplication;
- a ghost is unsuitable for resolving long-lived contact forces;
- tightly coupled bodies should eventually migrate into one authority group.

Do not attempt cross-process PhysX determinism in this prototype.

## 11. Tick and Message Ordering Plan

Use a configurable fixed network tick, for example 20 or 30 Hz. The coordinator supplies the local tick source.

Every message envelope records:

- message type;
- target entity when applicable;
- produced tick;
- authority epoch when applicable;
- sender session or worker;
- delivery mode;
- payload length and correlation/event ID.

Within one reliable route, preserve ordering. Across routes, use tick, epoch, and event ID to validate meaning rather than assuming global arrival order.

## 12. Multiplayer Play Mode Workflow

The target development workflow is one-click or few-click local startup through Unity Multiplayer Play Mode.

Suggested profiles:

### Quick API profile

- coordinator + two logical workers hosted locally;
- one editor client and one virtual player;
- no network impairment;
- fastest iteration.

### Handoff validation profile

- two isolated worker worlds/processes where practical;
- two clients;
- visible boundary and automatic traversal loop;
- RPC and epoch diagnostics enabled.

### Adverse message profile

- two clients and two workers;
- configurable delay/reorder around the handoff window;
- button to release a deliberately held stale message;
- duplicate-message injection for the cross-worker hit.

If Multiplayer Play Mode cannot represent server workers exactly as desired, use it for clients while launching workers through a small editor bootstrap. The important requirement is a documented, repeatable local workflow.

## 13. Debugging and Inspection Deliverables

### Entity inspector

Show:

- entity and prefab IDs;
- local role: authority, ghost, or client observer;
- input session;
- authoritative worker and epoch;
- cell/domain;
- current tick;
- replicated variables and last-change tick.

### World overview

Show:

- worker domains and boundary;
- authoritative and ghost copies;
- client interest regions;
- handoff state and direction.

### Event timeline

Record:

- spawn/despawn;
- interest enter/exit;
- RPC send, route, execute, reject, and reroute;
- handoff prepare, snapshot, commit, revoke, and abort;
- connection/session changes;
- stale epoch rejection;
- duplicate interaction rejection.

Filters by entity, worker, session, event type, and tick are desirable. A bounded in-memory event list is sufficient.

## 14. Verification Strategy

### Edit Mode tests

- identifier and envelope serialization round trips;
- authority directory lookup and epoch increment;
- stale epoch rejection;
- RPC permission checks;
- prefab registry validation;
- interest-cell calculations;
- session reconnect mapping;
- interaction ID deduplication.

### Play Mode tests

- spawn replicates to expected observers;
- non-authority cannot mutate a replicated variable;
- handoff preserves entity ID and replicated values;
- old worker stops canonical simulation;
- destination starts at the committed tick/epoch;
- RPC routes correctly before and after handoff;
- ghost is promoted without a duplicate Unity object in that worker world;
- interest exit is not treated as despawn;
- reconnect retains session/entity identity.

### Manual demo checklist

- run the six scenarios in Section 5;
- capture the event timeline for each;
- repeat handoff in both directions;
- repeat while sending inputs and damage RPCs;
- verify no duplicate health change or entity spawn;
- verify inspector values agree across all logical nodes.

## 15. Suggested Build Order

### Milestone 1: identity and local roles

- core value types;
- `NetworkObject` and `NetworkBehaviour` lifecycle;
- prefab registry;
- local node identities;
- debug inspector shell.

Exit: one entity exists as distinct logical copies with the same stable ID.

### Milestone 2: messaging and replication

- common envelope and serializer;
- local message bus;
- spawn/despawn;
- one `NetworkVariable<T>` path;
- observer sets and grid interest;
- network tick.

Exit: two clients observe authoritative state from Worker A.

### Milestone 3: RPCs and sessions

- `AuthorityRpc`, `ObserversRpc`, and `TargetRpc` dispatch;
- input-authority validation;
- session/connection separation;
- simple reconnect.

Exit: all RPC families and reconnect work without worker handoff.

### Milestone 4: ghosts and handoff

- worker ghost replication;
- prepare/final snapshot/commit flow;
- epoch fencing;
- promotion/demotion;
- bounded RPC reroute;
- handoff timeline.

Exit: the player crosses the boundary continuously with stable identity.

### Milestone 5: boundary interaction and demo polish

- one cross-worker physics interaction;
- event-ID deduplication;
- Multiplayer Play Mode profiles;
- impairment controls;
- complete debug views and scripted scenarios.

Exit: every first-pass success criterion is demonstrable and repeatable.

## 16. Cut Order if the Prototype Is Too Large

Cut or defer in this order while preserving the core proof:

1. advanced impairment controls beyond one delayed stale message;
2. separate processes if isolated in-process worlds prove the same contracts;
3. generalized physics helpers beyond the single boundary-hit scenario;
4. automatic reconnect timing in favor of a manual reconnect button;
5. extensible interest policies beyond the fixed grid;
6. unreliable delivery options;
7. polished world-map visualization, retaining the entity inspector and event log.

Do not cut stable IDs, distinct authorities, epochs, real message serialization, worker ghosts, authority-directed routing, or the handoff scenario. Those are the prototype's reason to exist.

## 17. Deferred Production Concerns

Keep these documented but outside implementation scope:

- distributed consensus and durable authority directory;
- authority leases and worker failure recovery;
- process discovery, orchestration, autoscaling, and deployment;
- dynamic domain assignment and load balancing;
- persistent storage and world recovery;
- authentication and account services;
- encryption, abuse prevention, and anti-cheat;
- large-scale performance and bandwidth optimization;
- generated RPC/serializer code;
- schema migration and version compatibility;
- rollback, prediction, reconciliation, and lag compensation;
- production metrics, tracing, alerting, and operational dashboards.

The local backend should make these replaceable concerns—not pretend to solve them.

## 18. Prototype Completion Definition

The prototype is complete when the demo reliably shows a player moving from Worker A to Worker B while:

- retaining the same entity and session identity;
- keeping client input authority;
- changing only simulation authority;
- incrementing and enforcing the authority epoch;
- maintaining replicated state and RPC behavior;
- promoting/demoting worker ghosts correctly;
- completing one cross-worker authoritative interaction;
- reconnecting through a new connection;
- exposing enough diagnostics to explain every transition.

At that point, pause implementation and review the API and feature list. The next decision should be based on what the prototype taught, not on adding production-scale infrastructure prematurely.
