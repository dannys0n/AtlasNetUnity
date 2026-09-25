# First-demo validation record

Checked with Unity 6000.6.2f1 on Windows on 2026-09-23. The checks below exercise this local implementation, not the future AtlasNet backend. They predate the direct-call RPC IL post-processor.

| Check | Result |
| --- | --- |
| Runtime/sample C# compile, codec round trip, local TCP round trip | Passed |
| Unity EditMode tests (IDs, scene/prefab wiring, registry error, spawn permission) | 7 passed, 0 failed |
| Windows standalone build | Succeeded |
| Separate clean Unity project with local-path UPM dependency | Package registered and consumer code compiled |
| Client-authority host plus non-host client | Both sessions joined and the client's player spawned in both processes |
| Server-authority host plus non-host client | Client player spawned; authority request, persistent value, observers event, and targeted response arrived |
| Late join after the persistent value changed | A third client received the current value (`1`) in its spawn snapshot |
| Earlier scale scene, host plus non-host client (before local workers/checkerboard) | 300 cubes plus two players, 302 remote observer copies; sampled server ticks about 1.0–1.3 ms and 1,350 aggregate bytes sent on moving ticks. These are historical measurements, not results for the current ScaleDemo. |

The standalone run's managed-allocation counter was unavailable, so the overlay reports `unavailable` rather than a misleading zero. These scale readings are a single local smoke test, not a benchmark or entity-capacity guarantee.

On 2026-09-24, the updated runtime and imported sample C# compiled, and an isolated IL post-processing pass rewrote all four sample RPC methods with no diagnostics. The generated send and receive-guard method references resolved against the runtime assembly. This is a compile/IL check, **not** a rerun of the gameplay checks above. Recheck host/client RPC delivery and a clean package import after Unity recompiles the new Editor assembly.

The subsequent NGO-style Player Prefab and spawn-snapshot ordering changes compiled the runtime, packaged and imported sample scripts, and EditMode test assembly. The EditMode and Play Mode checks above have **not** been rerun against these changes. Prefab registration was later migrated from inline manager entries to `NetworkPrefabsList` assets; those older scene-wiring results do not validate the migrated assets.

Still needs an interactive Multiplayer Play Mode pass: move, look, and jump as a non-host client in **both** authority scenes; watch movement and aim from another client; test the P/O spawn/despawn controls; and verify the camera/cursor feel. Headless processes cannot validate visual smoothness or live input. `PhysicsDemo` is the dedicated Rigidbody scene with its own `PhysicsPlayer`: verify the cubes fall, react to host R, and can be pushed by both host and joined-client movement while both views agree. Rigidbody prediction is not in this demo.

## Current framework pass (2026-09-24)

Added owner-written `NetworkVariable` permissions and per-recipient snapshots, owner-invokable `SendTo.Everyone` RPCs, remote-client transform interpolation, and a first `NetworkAnimator` for parameters, states, and explicit triggers. The simple imported and packaged sample scripts now exercise owner-written pitch and an owner-invoked visual RPC. Runtime, code-generation, sample, and EditMode test assemblies compile with `dotnet build` (zero warnings/errors); this does not execute Unity tests or prove RPC weaving at runtime. The main project was already open in Unity, so a second batch instance could not run its EditMode tests. A separate consumer batch launch stalled in Unity licensing before package compilation, and was stopped. **No new Multiplayer Play Mode or imported-shooter visual validation has passed for this change.**

## Prefab-list migration (2026-09-24)

`NetworkManager` now reads one or more `NetworkPrefabsList` ScriptableObject assets instead of inline prefab entries. The packaged and imported ClientAuthority, ServerAuthority, and ScaleDemo scenes reference list assets. ScaleDemo now registers its own `ScaleInterestPlayer` alongside `ScaleCube`; it no longer shares the server-player prefab. The player prefab must be in an assigned list. Earlier runtime, sample, and EditMode test assemblies compiled, and the standalone codec/transport check passed. Those earlier checks do **not** prove that Unity imported the current assets or that the scenes run. Run the EditMode asset tests and a Multiplayer Play Mode spawn/late-join smoke test once the Unity editor can validate this change.

## Player-radius worker interest (2026-09-25)

The scale scene now uses a server-moved top-down player with an editable `NetworkInterestSource` radius. Worker ghosts are selected from that player's radius rather than a fixed boundary band; region overlap is only a candidate lookup. The coordinator still owns the full canonical directory and performs the local cull. The C# runtime and sample compile plus the standalone codec, TCP, and Voronoi checks passed using an alternate build-output directory after OneDrive denied access to the prior validation output files. The updated Unity EditMode asset tests and interactive multi-window movement, handoff, region-overlay, and ghost-residency tests have **not** been run.

The client-only translucent interest disk was added afterward. Its sample script compiles and its prefab/material references were checked statically, but its appearance and alignment in Unity Play Mode remain unverified.

Client observer delivery now keeps all entities authored by the player's current worker, while `NetworkInterestSource` radius filters entities authored by other workers; ordinary demos retain full-world observers. The standalone runtime/sample compile and codec, TCP, and Voronoi checks pass. Unity Play Mode still needs a late-join and boundary-crossing check to confirm same-worker entities remain subscribed at any distance, cross-worker spawn/hide behavior, and observer updates after re-entry or player handoff.

Owner-written root positions are now eligible for automatic worker handoff. At commit, the coordinator restores its latest owner-written transform, variable, and Animator state over the worker snapshot and refreshes the destination worker. The local protocol is version 6. Static compile/protocol checks do not prove movement through a handoff: a cloned client-authority multi-worker scene still needs two-client Play Mode validation while moving/looking across the boundary, checking stable `EntityId`, changed epoch/worker, nearby player visibility, and F-key flash delivery afterward.

## Rigidbody first pass (2026-09-24)

Added a server-authoritative `NetworkRigidbody` companion to `NetworkTransform` and an EditMode test for its setup guard and kinematic client-copy behavior. A dedicated `PhysicsDemo` scene now registers and spawns three Rigidbody cubes; host R applies another impulse. The runtime, sample-script, and EditMode test assemblies compile with zero warnings/errors (the not-yet-regenerated sample-script IDE project needed a temporary source entry, which was removed afterward). Serialized prefab and scene component references were checked for duplicate or missing local file IDs. The Unity editor is open, so the EditMode tests and an interactive host/client physics test have **not** been run.
