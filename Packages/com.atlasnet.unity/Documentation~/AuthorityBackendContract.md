# Expected AtlasNet–Unity backend contract

This is the **semantic contract Unity needs from a future AtlasNet backend**, not a claim that the active C++ branch implements it and not a frozen P/Invoke ABI. It combines the architect's current ingress/shard model with the WIP [Network](https://landmark.youtrack.cloud/articles/AN-A-3/Network) and [Commands And Signals](https://landmark.youtrack.cloud/articles/AN-A-1/Commands-And-Signals) notes. Where those sources do not settle behavior, this document says so explicitly. In the active branch, `include/AtlasNet/Node/Module/Providers.hpp` has an empty `ShardProvider`; the packaged localhost mode remains a stand-in.

## Expected topology, not a Unity gameplay API

- A client keeps a connection to an ingress node. The architect expects ingress to retarget its proxy to the player's current authoring shard after handoff without making the client choose a shard. The WIP Network note defines an `IngressOfClient` intent but does not yet specify this retarget protocol.
- A Unity simulation worker is co-deployed with an AtlasNet node. AtlasNet nodes, not Unity gameplay scripts, establish node-to-node connections for cross-shard reads and commands. The Network note's intent layer addresses logical recipients (for example, authority/shard of an entity) rather than node addresses. A receiving node may re-resolve and forward an intent that became stale during handoff.
- A worker may receive subscribed ghost entities from arbitrary other shards when gameplay interest requires them. A ghost is readable local context, not an additional simulation authority. Which entity set is matched, who filters it, and how subscriptions are represented remain open design choices.
- AtlasNet's internal C++ RPC/intent layer is **not** the same API as a Unity gameplay `[Rpc]`. The bridge maps gameplay destinations and commands onto backend routing without exposing MethodIDs, node IDs, or transport channels to gameplay code.

## Responsibility boundary

- **Expected AtlasNet backend:** decides which worker simulates an entity, resolves entity-directed traffic, coordinates handoff decisions and commits, tracks the authority epoch, and supplies replica residency changes. The current WIP notes establish logical routing and cross-shard commands, but do **not** yet define the complete assignment, handoff, or interest API. A real backend may use a different policy from the local Voronoi example.
- **Unity framework:** creates the registered prefab when assigned or subscribed, simulates only when granted authority, exports/imports gameplay state, applies authority/replica notifications, and sends entity/session-directed state and RPCs. It never chooses a destination worker from a region polygon.
- **Unity gameplay scripts:** keep using `NetworkObject`, `NetworkBehaviour`, replicated values, and RPC destinations. They do not address workers, ingress nodes, or region geometry. `IsOwner` (controlling session) and `HasAuthority` (simulation on this worker) remain distinct.

## Required exchange

The eventual bridge must carry these meanings, whether its implementation uses callbacks, queues, or messages. These are **Unity requirements for the backend team to resolve**, not claims of current C++ support:

1. **Entity lifecycle:** an authorized worker requests spawn/despawn; the backend assigns or acknowledges a stable runtime `EntityId` and routes a registered prefab key, controlling session, initial pose, tick, and state to workers/clients that should hold the entity. The ID persists for the entity's lifetime across handoffs and is distinct from prefab identity. An individual worker cannot assume it has seen an ID before.
2. **Assignment and residency:** the backend notifies Unity when an entity becomes locally authoritative, loses that role, enters as a ghost, or leaves the worker's interest set. Authority assignment carries a monotonically increasing epoch. Ghost residency and client observation are separate from simulation authority. Unity applies a snapshot on entry, then deltas/events, and removes a replica without canonically despawning the entity.
3. **Handoff:** the backend requests source simulation state, prepares the destination, and reports commit or abort. Unity freezes/resumes simulation accordingly, rejects stale epochs, and never changes the entity ID or controlling session merely because authority moves. The exact handshake, retries, and failure recovery remain to be agreed.
4. **Entity-directed commands:** client input and gameplay `[Rpc(SendTo.Authority)]` target an entity, not a worker. An authoritative projectile may send a command to a subscribed ghost's owning entity; the *target* authority validates a hit/damage decision and publishes resulting state/signals. Source identity/epoch and caller permissions must survive routing. Whether a pending-handoff command is queued, forwarded, retried, or rejected must be specified so it is neither lost nor applied twice. The backend's C++ RPC MethodID need not equal the Unity gameplay RPC ID.
5. **Client path:** a persistent client session enters through ingress, reaches its current authoring worker, and receives authorized state/events back through ingress. A handoff may change the authoring worker without changing the logical session or requiring gameplay code to reconnect. The precise mapping among Unity `SessionId`, AtlasNet client ID, and transport connection is open.
6. **Optional diagnostics:** a tool may request the local authority region outline and be notified when it changes. The answer may be unavailable or non-polygonal. It is for visualization only—not a substitute for assignment/replica events or a permission check. It must not be queried every simulation tick.

## Local development mode versus the expected bridge

The current package uses `LocalVoronoi` for placement and `LocalWorldPolicy` for local handoff/interest decisions. `NetworkManager.LocalWorkers` performs export, prepare, commit, routing, and packet forwarding through the **first Unity server** as coordinator and ingress. Other Unity workers connect to it rather than to one another. This is deliberately not the intended AtlasNet node-to-node packet path. It remains useful for exercising stable IDs, permissions, epochs, snapshots, ghost lifecycle, and developer-facing gameplay code.

In the local demo, an owned player carrying `NetworkInterestSource` supplies an X/Z radius. The coordinator identifies potentially overlapping owner regions, checks individual entities, and sends only matching cross-worker replicas; a larger exit radius reduces churn. Clients still receive all entities from their player's authoring worker. This is a sample policy, not a backend guarantee or a general client visibility/security rule. Production interest could be based on other gameplay criteria; its query, filtering, and subscription/delta mechanisms are still undecided. Entity-directed commands should reach one current authority rather than return another shard's whole entity list.

The Unity runtime should eventually consume assignment, residency, routing-result, and handoff notifications through an internal bridge. This refactor puts local interest/handoff choices in `LocalWorldPolicy` and makes client/worker spawn packets use one replica creation path. The remaining local coordinator and packet dispatch are still in `NetworkManager`; merely replacing `LocalTcpTransport` with another byte transport would **not** be a complete native integration. Do not infer a P/Invoke ABI, client wire format, or C++ feature availability from local packet numbers, `NetworkInterestSource`, or debug polygons.

## Decisions needed before native integration, not before local refactoring

- Backend ownership event and handoff state machine, including timeout, idempotency, and failure behavior.
- Replica subscription and filtering contract, including who discovers target entities and when snapshots/deltas are emitted.
- Unity session-to-AtlasNet client identity mapping and ingress retarget notification/ordering.
- Command authentication, delivery/ordering, result/error reporting, and duplicate suppression across handoffs.
- Snapshot framing/versioning and the thread/queue boundary between a co-deployed AtlasNet node and Unity's main thread.

Keep the current gameplay API while refactoring local policy. Revisit the public cross-shard interaction helper only when a concrete gameplay test shows it cannot address the needed entity or preserve caller context.
