# First-demo validation record

Checked with Unity 6000.6.2f1 on Windows on 2026-09-23. The checks below exercise this local implementation, not the future AtlasNet backend.

| Check | Result |
| --- | --- |
| Runtime/sample C# compile, codec round trip, local TCP round trip | Passed |
| Unity EditMode tests (IDs, scene/prefab wiring, registry error, spawn permission) | 7 passed, 0 failed |
| Windows standalone build | Succeeded |
| Separate clean Unity project with local-path UPM dependency | Package registered and consumer code compiled |
| Client-authority host plus non-host client | Both sessions joined and the client's player spawned in both processes |
| Server-authority host plus non-host client | Client player spawned; authority request, persistent value, observers event, and targeted response arrived |
| Late join after the persistent value changed | A third client received the current value (`1`) in its spawn snapshot |
| Scale scene, host plus non-host client | 300 cubes plus two players, 302 remote observer copies; sampled server ticks about 1.0–1.3 ms and 1,350 aggregate bytes sent on moving ticks |

The standalone run's managed-allocation counter was unavailable, so the overlay reports `unavailable` rather than a misleading zero. These scale readings are a single local smoke test, not a benchmark or entity-capacity guarantee.

Still needs an interactive Multiplayer Play Mode pass: move, look, and jump as a non-host client in **both** authority scenes; watch movement and aim from another client; test the P/O spawn/despawn controls; and verify the camera/cursor feel. Headless processes cannot validate visual smoothness or live input. Optional dynamic Rigidbody synchronization and prediction are not in this demo.
