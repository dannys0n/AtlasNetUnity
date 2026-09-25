# AtlasNetUnity agent guide

## Outcome and scope

Build an installable Unity networking package whose developer-facing code feels familiar to users of NGO, FishNet, PurrNet, and Netick. The first demo must *work* with one local Unity server and clients. It must let developers author gameplay now while keeping backend routing replaceable later. This is an implementation brief, not a demand to implement every idea in `ToDo/`.

Milestones 1–3 below describe the original single-server first demo. A subsequent experimental local multi-worker pass is now in progress; see `Packages/com.atlasnet.unity/Documentation~/LocalWorkers.md` for its mechanism and runtime validation checklist. Do not claim handoffs work until they have been exercised in multiple Unity instances. Prediction/reconciliation and real AtlasNet backend integration are still later work. Unity servers will eventually be co-deployed with AtlasNet nodes; gameplay code must not depend on that deployment detail.

For the future backend division, use `Packages/com.atlasnet.unity/Documentation~/AuthorityBackendContract.md`: AtlasNet is expected to own simulation assignment, entity-directed routing, replica interest, and handoff decisions/commit. The current C++ branch does not yet implement that contract. The localhost implementation stands in for it with player-radius worker interest: regions find candidate owners, then entity distance decides replicas. The first server still holds the full canonical directory. Region geometry is optional debug data, never gameplay authority. Do not confuse worker interest with client visibility or line of sight.

The initial repository may contain only documentation. Inspect its current state before creating a Unity project or package. Choose and record one installed Unity version and compatible Multiplayer Play Mode version; do not silently assume a version from older examples.

## Context to consult when relevant

- `ToDo/01-framework-feature-scope.md` explains stable `EntityId`, input versus simulation authority, sessions, observers, worker ghosts, epochs, and the longer-term architecture. Its production-oriented features still exceed the local prototype.
- `ToDo/02-proposed-unity-developer-api.md` contains *sketches*, not fixed signatures. Keep only the smallest coherent surface proven by working examples.
- `ToDo/03-local-dummy-backend-prototype-plan.md` is design context; use the narrower `LocalWorkers.md` checklist for the current experimental pass.
- If available, `../UnityNetworkingBasics/NGO`, `../UnityNetworkingBasics/Fishnet`, `../UnityNetworkingBasics/Purrnet`, and `../UnityNetworkingBasics/Netick` are read-only comparison projects. Check each framework's official documentation before relying on its behavior or copying an API pattern.
- Use Unity's official documentation for UPM package layout and Multiplayer Play Mode integration. Do not introduce dependencies on NGO, FishNet, PurrNet, Netick, or Unity online services merely to run the local demo.

## Working agreements

- Favor a small, legible Unity API: `NetworkObject`, `NetworkBehaviour`, a prefab registry, network time/tick, transform/state replication, spawn/despawn, and explicit RPC destinations. Avoid builder scripts, generated demo hierarchies, large manager stacks, and speculative abstractions.
- Keep framework runtime and editor tooling in a Unity Package Manager package (`package.json`, `Runtime/`, `Editor/`, assembly definitions, optional `Tests/`, `Samples~/`, and `Documentation~/`). Developers' own scripts, prefabs, and editable project settings belong to their project; package internals must not be copied into its `Assets` tree. Samples enter `Assets` only through an explicit sample import.
- Make the local adapter exercise real identity, permission, serialization, and message-dispatch paths. Gameplay APIs must address entities or sessions, not worker addresses or concrete backend classes. Local worker handoff does not by itself prove future C++ backend correctness.
- Separate the *controlling session* from the *writer of a particular replicated channel*. Client authority for the first demo covers movement/aim only; shared gameplay state such as health, damage, and canonical spawning stays server-authoritative. Do not give a client blanket authority over an entire entity merely to simplify movement.
- Never use `IsServer` alone as the long-term permission test for an entity. Keep `EntityId` stable and independent of the Unity instance, prefab, connection, and eventual worker. Keep prefab identity distinct from runtime entity identity.
- Explain and implement the real mechanism behind any convenient attributed RPC call. An attribute by itself does not intercept an ordinary C# method call; use a working dispatch strategy rather than an API-shaped stub.
- Prefer editing Unity assets and scripts directly. Keep samples deliberately simple (capsules and basic movement/look/jump are enough), preserve user assets and the comparison projects, and avoid unrelated README files. No artificial latency is required for this demo.
- Work through the requested milestones in one continuous implementation pass when feasible. Verify each exit gate, fix failures caused by the work, and continue without pausing merely for a routine design preference. Ask only when a material choice or missing access would change the intended result. If Unity cannot be run in this environment, finish checks that are possible and report runtime validation as unverified, never as passed.

## Milestone 1 — Installable package and local networking foundation

Create a UPM package that can be added to a separate clean Unity project via Package Manager (a local path is acceptable during development; retain a Git-URL-ready package layout). Put the demo/consumer project outside the package's runtime source. Keep the public API in its own assembly and implementation details internal where practical.

Implement the minimum coherent local path: startup as server or client, stable entity and session identifiers, registered spawnable prefabs, `NetworkObject`/`NetworkBehaviour` lifecycle, a network tick, message serialization/dispatch, and canonical spawn/despawn. Define what local observer removal means separately from canonical deletion. Validate missing or mismatched prefab registrations with actionable errors. Use narrow internal adapter interfaces so the later AtlasNet bridge can replace the local implementation without leaking backend types into gameplay scripts.

**Exit gate:** a clean consumer project installs the package, compiles, starts a server and a non-host client, spawns the same registered object on both, and despawns it consistently. A package that merely compiles inside its source project does not satisfy this gate.

## Milestone 2 — Familiar gameplay API and both authority modes

Provide the smallest usable replicated-value API, transform synchronization, and working authority/observers/target message paths needed by the sample. Define sender permission, recipient, delivery behavior, and local execution clearly; use current state for late join rather than relying on an old transient RPC. Make unauthorized writes fail clearly during development.

Supply two easy-to-compare sample scenes or configurations with dedicated simple player scripts/prefabs: (1) client-authored movement and aim, and (2) server-authoritative movement from client input intent with immediate local mouse look. Both should support basic movement, looking, and jumping. Keep input collection distinct from server simulation and use a network tick for transmitted state/intent; do not implement prediction yet. The package should not require a particular game input system just because a sample uses one.

Basic `Rigidbody` replication is desirable **after** both non-predicted movement modes work. If included, only the authoritative copy simulates dynamic physics; remote copies follow replicated state. Do not imply shared physics or predicted Rigidbody collision behavior.

**Exit gate:** in Multiplayer Play Mode, a remote client (not merely the host player) can control its avatar in both sample modes, while another client sees appropriate movement and aim. Spawn/despawn, one persistent replicated value, and each implemented RPC destination work across processes. Record any deferred optional Rigidbody support explicitly.

## Milestone 3 — Demo reliability, scale, and handoff-ready seam

Make Multiplayer Play Mode a normal, documented way to launch the demo with local roles and connections configured without editing gameplay scripts. Include a small runtime inspector or diagnostic view for entity ID, prefab ID, local role, input session, writer/authority mode, current tick, and useful errors. Keep it focused; no worker-map UI is needed.

Exercise hundreds of registered entities with a mix of idle and moving objects. Track server tick time, allocations, bytes sent per client, and observer counts; do not assume every object must send a per-object RPC each tick. Implement only the minimal changed-state batching or interest filtering needed to keep the demo understandable and avoid obvious scaling pathologies. Do not assert a universal entity capacity from one local machine.

Add proportionate automated checks for identifier/serialization round trips, prefab registry validation, permission enforcement, state for a late observer, and RPC targeting. Run the clean-project package install and Multiplayer Play Mode smoke test when Unity is available; keep a concise manual checklist for anything that cannot be automated. Review public names and inspector settings against the simple samples, removing redundant configuration rather than layering more options on top.

**Exit gate:** the package installs in a clean project, the two authority examples work with non-host clients, the hundreds-of-entities scene runs with recorded measurements, and the public gameplay code contains no worker address or AtlasNet backend dependency. Report exact tests performed, failures, optional features omitted, and remaining risks.

## Deliberately later

Do not expand the original first-demo milestones into prediction, reconciliation, lag compensation, distributed PhysX, reconnect guarantees, persistence, accounts, or production transport tuning. The separate local multi-worker pass may exercise worker ghosts and authority handoff, but it does not claim production recovery, equal-load balancing, or C++ integration. Preserve the semantic seams: stable `EntityId`, session-versus-connection distinction, entity-directed calls, single-writer state rules, and a replaceable local adapter.
