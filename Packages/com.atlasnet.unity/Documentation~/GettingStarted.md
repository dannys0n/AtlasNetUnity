# AtlasNet Unity: local backend demos

This package targets Unity **6000.6.2f1** for the current example project. The project uses **Multiplayer Play Mode 3.0.0**. The networking package itself does not depend on NGO, FishNet, PurrNet, Netick, Unity online services, or a game input package.

## Install and import

The source project treats `Packages/com.atlasnet.unity` as an embedded UPM package. In a separate Unity project, use Package Manager > **Add package from disk** and select this package's `package.json`. The package has normal `Runtime/`, `Samples~/`, and `Documentation~/` directories so the same folder can later be published as a Git package.

In Package Manager, select **AtlasNet Unity**, import **00 Shared** first, then import any of the eight individual scenes. Start with **01 QuickStart**. The shared import supplies the scripts, prefabs, primitive animation clips, and URP materials; each scene import references those assets. The sample's scripts, prefabs, and scenes are editable project assets after import. The runtime package stays in `Packages`, not in the project's `Assets` directory. It uses Unity's legacy `Input` API to keep the controller readable; set **Active Input Handling** to **Both** or **Input Manager** in Player Settings. The package API itself does not require that input system.

## Logging

Set **NetworkManager > Network Settings > Log Level** to **Full**, **Warnings**, **Errors**, or **None**. The shared prefab defaults to Warnings. Warnings includes errors; Errors includes explicit AtlasNet exception logs. Filtering is per manager and also covers the sample launcher, RPC feedback, and dummy-backend handoff messages. Suppressed verbose messages are checked before formatting. This does not mute Unity engine/package diagnostics or suppress thrown exceptions. Change the level in the Inspector during Play Mode, or set NetworkManager.LogLevel in code.

## Client boundary visualization

After joining as a remote client, **Show boundary walls (client debug)** starts enabled and can be switched off in the sample panel. Translucent blue vertical planes show the owned player's current backend region and update when its authority changes. This is a local-only diagnostic, enabled by default. Its shared manager prefab component exposes the viewing radius (80 units), wall height (12 units), and material. Walls have no colliders or shadows; disabling the toggle hides them and unsubscribes from region updates. No boundary subscription is active while it is off.

## Sample scenes

- **01 QuickStart**: minimal host/join, owner movement, spawn/despawn, a persistent counter, and RPC feedback. Start Host in one view and Join as Client in another. WASD moves, F increments the counter, and P/O spawn/despawn an extra object on the host.
- **02 ClientAuthority**: owner-written transforms with development worker handoff.
- **03 ServerAuthority**: client input and server-simulated movement with development worker handoff.
- **04 Physics**: authoritative rigidbodies and impulses.
- **05 InterestAndScale**: interest boundaries and worker distribution, with a live 0Ã¢â‚¬â€œ200 entity-count slider on the coordinator.
- **06 AnimatorSync**: a block figure with owner-written Speed and a Wave trigger. WASD changes locomotion; Space or Wave plays the trigger. Late joiners receive the current persistent Animator state. Its local camera follows the owning player horizontally at a fixed height; the camera is not networked. No imported character art is required.
- **07 ClientPhysics**: a copy of Physics with owner-simulated Rigidbody movement and kinematic server replicas.

- **08 Soccer**: server-authoritative Rigidbody players, a contact-kicked ball, balanced teams, scoring, automatic waves and team-colored goal confetti. The HUD includes a local clock, a circular touch stick and a plain HTTP join QR/address. Serve the browser build separately. Import TMP Essential Resources (Window > TextMeshPro > Import TMP Essential Resources) and use URP with Unity UI available. In Tags and Layers, create a `SoccerPlayer` layer, assign it to the imported SoccerPlayer prefab and all its children, and disable SoccerPlayer-versus-SoccerPlayer in Project Settings > Physics > Layer Collision Matrix. Keep Default-layer collisions enabled for the ball and arena. The prefab uses the built-in `Player` tag. Layer numbers are project-specific: reassign the prefab if layer 8 is already used. Open `08_Soccer`, then use Host/Start server only, Join as worker, and Join as client as needed. Only admitted players count toward teams; player departures rebalance team assignments. Interest radius is 53.33 m; its debug disc is always visible on servers, with Show On Clients off for Soccer. The ball and its score state follow normal interest culling. There is no CharacterController soccer variant.

QuickStart and AnimatorSync do not need a worker. The other scenes demonstrate the local dummy backend, not a connection to the native AtlasNet backend. Samples use URP materials. Transport selection is shared: the default host accepts UDP and WebSocket simultaneously; browsers use WebSocket.

## Run authority, physics, and scale samples with Multiplayer Play Mode

1. Open `02_ClientAuthority`, `03_ServerAuthority`, or `04_Physics` from the imported sample in every instance. Install **Multiplayer Play Mode 3.0.0** and start the main Editor Player plus at least two additional editor instances. This project has that package in its manifest. For the larger multi-worker `05_InterestAndScale` flow, see `LocalWorkers.md`.
2. In the main Game view, click **Start Host**. In a second Game view, click **Join as Worker**. In each further Game view, click **Join as Client**. Alternatively, use **Start Server Only** in one view, a worker in another, and clients in the remaining views. All instances use `127.0.0.1:7777`; no gameplay script edits are needed.
   The demo keeps every instance running when its window loses focus, so the host can accept joins while you interact with a client window. Keyboard and mouse input still go only to the focused Game view.
3. In an owning player view, use WASD, mouse look, Space to jump, and F to request the RPC example. Esc releases the cursor. On the server/host, P spawns an extra visible object and O despawns it. The extra object has no controlling session.
4. A newly joined client should receive existing visible entities and the latest persistent value. It receives every entity authored by its player's current server, plus nearby entities authored by other servers when its player has `NetworkInterestSource`. `04_Physics` now uses that same local multi-worker interest and handoff path. Disconnecting a client despawns its owned player canonically. `HideFrom` only removes a particular client's observer copy; it is not canonical deletion.

For a standalone smoke test, the sample launcher also accepts `-atlas-host`, `-atlas-server`, `-atlas-worker`, or `-atlas-client`. Add `-atlas-scene=02_ClientAuthority`, `-atlas-scene=03_ServerAuthority`, `-atlas-scene=05_InterestAndScale`, or `-atlas-scene=04_Physics` to load that built scene before starting the role. `-atlas-test-rpc` makes the local player request the sample action once, and `-atlas-metrics` writes a server metric line every five seconds. These switches belong only to the sample launcher, not to the framework API.

The `ClientCrossServerPlayer` prefab has owner-written position and rotation. In `ServerCrossServerPlayer`, only yaw/pitch are owner-written; the client sends movement input to the current simulation authority, which moves its CharacterController on the network tick. Both scenes enable automatic local handoff and player-radius cross-worker interest. There is deliberately **no movement prediction** in this demo, so server-authoritative movement has visible input delay. Aim remains immediate for the local owner. No artificial latency is configured.

The `05_InterestAndScale` scene spawns 50 moving cubes and no stationary networked cubes. A joined client controls a green top-down player marker with WASD; a translucent green disk highlights its interest entry radius. Set the radius and exit padding on its `NetworkInterestSource` component in the `ScaleInterestPlayer` prefab. The disk deliberately excludes the exit padding. The coordinator sends the client every entity on its player's current authoring server. For entities authored by other servers, entering the radius spawns an observer copy and leaving the radius plus exit padding hides it. Network updates and RPCs follow the same observer set. Each server requests a translucent debug overlay for its simulation region; authoritative cubes are blue and interested ghosts are amber. Worker ghost delivery follows the player radius, not a fixed region-border band, while the first server retains the full canonical directory. Its metrics report server tick time, managed allocations during each tick when the runtime counter is available, aggregate bytes sent during the last tick, entity count, and remote observer copies. These are local measurements, not a capacity promise.

The `04_Physics` scene uses a separate server-authoritative `PhysicsPlayer` with a dynamic Rigidbody and CapsuleCollider. The owner sends movement intent on network ticks; its simulation worker sets horizontal velocity and jumps in `FixedUpdate`. Mouse aim rotates the owner's camera/aim pivot immediately without rotating the physics body. The scene spawns three registered Rigidbody cubes in different parts of the world. Only each body's current simulation worker runs dynamic physics; other copies are kinematic. Press **R in a server/worker Game view** to kick only cubes authoritative in that window. Both players and cubes can hand off at region boundaries, carrying linear and angular velocity. The top-down server camera, region overlay, and player interest disk help inspect this. Remote ghosts remain kinematic. Cross a Voronoi boundary to exercise ordinary spatial handoffs. Independent workers do not share a collision solver. Movement prediction is not included. Use Multiplayer Play Mode for interactive movement checks; automated cross-worker handoff checks are in `CrossWorkerPhysicsTests`.

## What gameplay code looks like

`NetworkObject` holds a stable runtime `AtlasNet.EntityId`, a separate prefab ID, and a controlling `SessionId`. Attach `NetworkBehaviour` components on the same GameObject as `NetworkObject`. Create a **Network Prefabs List** asset through **Create > AtlasNet > Network Prefabs List**, add the root `NetworkObject` prefabs to it, and assign one or more of these assets to **Network Prefabs Lists** on `NetworkManager`. A list can be shared by multiple managers. Assign **Player Prefab** separately to spawn one player for each joined session; that prefab must also appear in an assigned list. The prefab ID is generated from the saved asset rather than entered by hand; gameplay code can spawn a registered prefab by reference. If no Player Prefab is assigned, automatic player creation is disabled. By default, players spawn at the prefab's position; the sample sets `PlayerSpawnPosition` to spread them apart.

```csharp
public sealed class ExampleAction : NetworkBehaviour
{
    private readonly NetworkVariable<int> count = new NetworkVariable<int>(0);

    public void RequestAction()
    {
        if (IsOwner) ApplyActionRpc();
    }

    [Rpc(SendTo.Authority)]
    private void ApplyActionRpc()
    {
        count.Value++; // Persistent authority-written state.
        ShowEffectRpc();
        AcknowledgeRpc(count.Value);
    }

    [Rpc(SendTo.Observers)] private void ShowEffectRpc() { /* transient visual */ }
    [Rpc(SendTo.Owner)] private void AcknowledgeRpc(int count) { /* controlling client */ }
}
```

The `[Rpc(SendTo.X)]` attribute identifies the destination. Its method name must end in `Rpc`, as in NGO. Call an attributed method directly; the package's Editor IL post-processor changes that call into a send during compilation. The method body runs on local delivery or when the RPC is received. AtlasNet discovers handlers when the object spawns, derives stable method IDs from their signatures, serializes supported arguments, checks the destination and sender, then invokes the handler on the receiving instance. `SendTo.Authority` goes from the controlling client to the entity's current simulation authority (the server in this demo); local authority may invoke it too. `SendTo.Observers` goes from the server to observing clients; `SendTo.Owner` goes to the controlling client. `SendTo.Everyone` executes locally and reaches the server and observing clients. A client may invoke it if `InvokePermission` is `Owner` (for its own object) or `Everyone` (for an observed object); it defaults to `Everyone`, as in NGO. The server relays client-originated calls without echoing them to the sender. Use `InvokePermission = RpcInvokePermission.Owner` for owner-only visual events. For an arbitrary observing session, use `[Rpc(SendTo.SpecifiedInParams)] private void NotifyRpc(SessionId target, int value)` and call `NotifyRpc(session, value)`. An authority handler may take a final `SessionId` populated with the sender; callers must supply a placeholder value for that parameter. Avoid overloading RPC handler names.

`IsOwner` tells client-side input and camera code whether it controls this object. `HasAuthority` tells simulation code whether it may write canonical gameplay state. These are different even in the local demo: a client can own its avatar while the server has authority over its persistent variables. `IsServer`, `IsClient`, and `IsHost` describe the process role, not the writer of a particular entity. `NetworkBehaviour` also exposes `NetworkManager` and `IsSpawned` for familiar access. The default local adapter accepts UDP and WebSocket through Unity Transport; reliable ordered is the default delivery, with unreliable delivery available for RPCs and named messages. A legacy TCP-only adapter is optional. RPC arguments currently support `int`, `uint`, `ulong`, `float`, `bool`, `string`, `Vector2`, `Vector3`, and `Quaternion`.

`NetworkVariable<T>` fields register automatically when the behaviour spawns; declare and initialize them on the behaviour. By default, the server writes and everyone reads. For owner-written aim or weapon selection, use `new NetworkVariable<Quaternion>(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner)`. The server validates the controlling session, retains the current value, and relays it to other observers. `ReadPermission.Owner` limits snapshots and later updates to the owner and server. Current values are included in spawn snapshots for permitted late observers before `OnNetworkSpawn`. Subscribe to `OnValueChanged(previous, current)` for later updates; `Changed` remains an alias. Supported value types are `int`, `float`, `bool`, `string`, `Vector3`, and `Quaternion`. `Set(...)` and the `Value` setter are equivalent. The sample scenes use prefab references in their spawners; prefab IDs remain internal registration keys.

`NetworkTransform` can interpolate received position and rotation on remote clients over one network tick; it never smooths the local owner or server simulation. `NetworkAnimator` can reference a child `Animator` and synchronizes changed bool, int, and float parameters plus current layer states, with a full state snapshot for late join. Choose Server or Owner as its writer. Use `NetworkAnimator.SetTrigger(name)` for transient triggers; plain `Animator.SetTrigger` cannot be sampled reliably. This first animator helper has not yet been tested against the imported shooter animations or complex transitions.

For a server-simulated 3D physics object, place `NetworkObject`, `Rigidbody`, `NetworkTransform`, and `NetworkRigidbody` on the same GameObject (plus an appropriate Collider). Leave the prefab Rigidbody non-kinematic for dynamic physics. Set `NetworkTransform` to synchronize both position and rotation, with **Server** as both writers, and leave its target on the root. The server keeps the prefab's physics mode; client copies are kinematic and follow the replicated pose. Apply forces or impulses only on the server in `FixedUpdate` (or send client input intent to the server first). `NetworkRigidbody` does not replicate forces, velocity, or collision callbacks, and it does not add prediction. Remote collision responses are not authoritative; send gameplay effects from server collision events when needed. `04_Physics` is the first interactive validation scene, but its behavior still needs a Multiplayer Play Mode pass.

The IL post-processor currently supports instance `void` RPC methods on non-generic `NetworkBehaviour` types, with no `try`/`catch` in the RPC body. Unsupported signatures produce a compile error. The receive dispatcher still uses reflection and has not yet been validated in an IL2CPP build; generated dispatch or explicit preservation rules may be needed before claiming AOT/player-build support. RPC weaving does not add prediction; local handoff and worker routing are separate runtime paths.

`UnityEngine` also defines an `EntityId` in this editor version, so qualify this package's type as `AtlasNet.EntityId` in code that imports both namespaces.

## Boundaries of this first pass

The active authority examples now use the local multi-worker backend to exercise worker ghosts, bounded worker-interest delivery, routing, and region handoffs. This path has not had a multi-instance Unity runtime validation pass; see `LocalWorkers.md`. The older single-server authority scenes and prefabs were retired. The local backend does not provide reconnect/resume, production transport tuning, encryption, prediction, rollback, lag compensation, distributed physics, or the C++ AtlasNet integration.

The `04_Physics` player and cubes use server-simulated 3D Rigidbodies; the other player examples still use `CharacterController`. Rigidbody handoff and pushing are wired but await Play Mode validation. Distributed collision and predicted physics remain outside the demonstrated scope.

The familiar core components are `NetworkManager`, `NetworkObject`, `NetworkBehaviour`, `NetworkTransform`, a first `NetworkAnimator`, `NetworkRigidbody`, and `NetworkPrefabsList`. AtlasNet does not yet provide NGO's scene synchronization or prefab overrides. Canonical dynamic spawning currently uses `NetworkManager.Spawn(prefab, position, rotation, owner)` rather than `Instantiate(prefab)` followed by `NetworkObject.Spawn()`. `NetworkTransform` has separate position and rotation writers because input ownership is not the same as simulation authority; this difference is intentional.

Before claiming the demo complete, exercise a non-host client and a worker in both cross-server authority scenes, the three RPC destinations, late join, spawn/despawn, the scale scene, and a package install in a separate clean project. EditMode asset checks alone cannot verify Play Mode behavior.

## Client-authoritative Rigidbody sample

Open `07_ClientPhysics` in each instance. Start Host or Server in the main editor, join additional workers if desired, and join a client using UDP or WebSocket. WASD moves and Space jumps. The player uses the same `PhysicsPlayerMovement` as `04_Physics`, but its `NetworkTransform` writes both position and rotation from the owner. `NetworkRigidbody` follows that setting: only the owning client/host runs dynamic player physics. The original server-authoritative player remains unchanged. Its cube counterparts still use server authority.

Worker handoffs move the server's logical entity authority without taking the Rigidbody away from the owning client or resetting its velocity. Physics between different simulation authorities remains approximate because the other copies are kinematic.

## Connection diagnostics and send limits

`NetworkManager.DisconnectClient(session, reason)` provides a short readable reason (maximum 512 UTF-8 bytes). The framework queues the notice and flushes outgoing data before the deferred transport close, following NGO's disconnect ordering. `DisconnectReason` is available in the client's disconnect callback and remains available until the next session starts. Unexpected transport closure supplies a transport-specific reason when available. `OnTransportFailure` reports a fatal listener/transport failure; `LastTransportError` describes it, and an active session stops. An ordinary remote disconnection is not a fatal transport failure.

Named messages support at most `CustomMessagingManager.MaxPayloadSize` (60 KiB). Built-in UTP reliable sends use a bounded 4 MiB queue per peer; accepted reliable messages are retained until sent, and an overflowing new message is rejected. `TrySendNamedMessage(name, recipient, payload, out error, delivery)` returns false with `NetworkSendError.PayloadTooLarge`, `QueueFull`, or `NotConnected`. Retrying later or splitting application data is the caller's choice. Normal argument/routing errors still throw. A successful call means accepted locally, not acknowledged by the remote application. Strict sends retain their exception behavior; queue rejection uses `NetworkSendException`.

Initial readable NetworkVariable values are applied before `OnNetworkSpawn`, including late joins and visibility re-entry. Apply the initial door/equipment state in that callback, then subscribe to change notifications. Transient RPCs and Animator triggers are not an event history. The dummy coordinator retains the latest reliable field deltas from remote authorities and combines them with its cached full snapshot when creating a new observer. This preserves the same initial-state contract without keeping every entity's GameObject on the coordinator.

The implementation references NGO 2.13.3's `NetworkObject.SynchronizeNetworkBehaviours`, `NetworkBehaviour.WriteNetworkVariableData`, `NetworkRigidbodyBase.UpdateOwnershipAuthority`, and connection-manager/UnityTransport disconnect handling. AtlasNet uses its own wire format and dummy multi-worker directory. Protocol version 18 requires rebuilding older players. The current NetworkAnimator restores parameters, layer state hashes, and normalized times; it does not claim exact NGO transition-blend parity.
