# Expected AtlasNet authority contract

This is the **semantic contract Unity needs from a future AtlasNet backend**, not a claim that the active C++ branch implements it and not a frozen P/Invoke ABI. In the active branch, `include/AtlasNet/Node/Module/Providers.hpp` has an empty `ShardProvider`; older detach/export/acquire callbacks live under `include/prev`. The packaged localhost mode supplies a temporary authority policy and handoff coordinator.

## Responsibility boundary

- **AtlasNet backend:** decides which worker simulates an entity, resolves entity-directed traffic, initiates and commits handoffs, tracks the authority epoch, and decides which workers/clients receive replicas. A real backend may use a different spatial policy from the local Voronoi example.
- **Unity framework:** creates the registered prefab when assigned or subscribed, simulates only when granted authority, exports/imports gameplay state, applies authority/replica notifications, and sends entity/session-directed state and RPCs. It never chooses a destination worker from a region polygon.
- **Unity gameplay scripts:** keep using `NetworkObject`, `NetworkBehaviour`, replicated values, and RPC destinations. They do not address workers or depend on region geometry.

## Required exchange

The eventual bridge must carry these meanings, whether its implementation uses callbacks, queues, or messages:

1. Unity reports entity creation/destruction, stable `EntityId`, registered prefab key, controlling `SessionId`, current position/tick, and exported state. AtlasNet may assign IDs itself; the local allocator is not a required native behavior.
2. AtlasNet tells a Unity worker when an entity becomes authoritative or when a relevant ghost/observer replica should be added or removed. Authority assignment includes `EntityId`, worker identity, and a monotonically increasing epoch. Replica residency is independent of simulation authority.
3. For handoff, AtlasNet requests a frozen source snapshot, delivers it to the destination, and issues commit or abort. Unity acknowledges preparation, rejects stale epochs, and resumes simulation only after commit. The `EntityId` and controlling session remain unchanged.
4. Authority-directed RPCs and input are routed by entity ID, including while a handoff is pending. State and visual events reach only their authorized recipients. A worker need not know another worker's address.
5. **Optional diagnostics:** a tool may request the local authority region outline and be notified when it changes. The answer may be unavailable or non-polygonal. It is for visualization only—not a substitute for assignment/replica events or a permission check. It must not be queried every simulation tick.

The local package currently uses `LocalVoronoi` to choose transfers and `NetworkManager.LocalWorkers` to perform export/prepare/commit/abort. That code is the temporary backend implementation, not gameplay API. `IAuthorityRegionDebug` is an optional internal debug capability; `ScaleRegionOverlay` requests it only for the graphical sample. For worker interest, an owned player carrying `NetworkInterestSource` supplies an X/Z radius. The local coordinator identifies potentially overlapping owner regions, checks individual entities against that radius, and sends spawn/update/despawn only to the interested worker. An existing replica uses a slightly larger exit radius. Multiple players on one worker form a union of interest; the entity has only one resident copy there. The first server still holds the full canonical directory. The coordinator performs this culling locally because the separate C++ backend and source-side query protocol do not exist yet; it is not a claim that a production requested server already filters entities.

For a production design, worker replica interest should be a subscription/delta stream: send one entity snapshot when it enters a worker's relevant set, then only its updates and events, and remove it when it leaves. Entity-directed commands need an owner lookup and should reach the one authoritative worker; they do not require returning a whole server's entity list. A player radius is a deliberately narrow first policy, not a requirement that interest always be spatial or shard-adjacent. Candidate-region lookup should stay behind the backend contract so another index can replace it. Client visibility is separate and may later use distance, team, or game-specific line of sight. These are recommendations, not capabilities implemented by the current C++ `ShardProvider`.

Before connecting C++, settle its actual ownership events, snapshot framing, failure/retry rules, replica-interest events, and thread/queue boundary with the backend team. Do not infer a native ABI from the local TCP packet numbers or the debug polygon format.
