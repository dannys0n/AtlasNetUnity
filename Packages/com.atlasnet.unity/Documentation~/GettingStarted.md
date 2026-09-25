# AtlasNet Unity: first local demo

This package targets Unity **6000.6.2f1** for the current example project. The project uses **Multiplayer Play Mode 3.0.0**. The networking package itself does not depend on NGO, FishNet, PurrNet, Netick, Unity online services, or a game input package.

## Install and import

The source project treats `Packages/com.atlasnet.unity` as an embedded UPM package. In a separate Unity project, use Package Manager > **Add package from disk** and select this package's `package.json`. The package has normal `Runtime/`, `Samples~/`, and `Documentation~/` directories so the same folder can later be published as a Git package.

In Package Manager, select **AtlasNet Unity** and import **Simple Authority Demo**. The sample's scripts, prefabs, and scenes are editable project assets after import. The runtime package stays in `Packages`, not in the project's `Assets` directory. This sample uses Unity's legacy `Input` API to keep the controller readable; set **Active Input Handling** to **Both** or **Input Manager** in Player Settings. The package API itself does not require that input system.

## Run with Multiplayer Play Mode

1. Open `ClientAuthority`, `ServerAuthority`, or `PhysicsDemo` from the imported sample. Install **Multiplayer Play Mode 3.0.0** and start the main Editor Player plus at least one additional editor instance. This project has that package in its manifest. For the separate multi-worker `ScaleDemo` flow, see `LocalWorkers.md`.
2. In the main Game view, click **Start Host**. In each additional Game view, click **Join as Client**. Alternatively, use **Start Server Only** in one view and **Join as Client** in every other view. All instances use `127.0.0.1:7777`; no gameplay script edits are needed.
   The demo keeps every instance running when its window loses focus, so the host can accept joins while you interact with a client window. Keyboard and mouse input still go only to the focused Game view.
3. In an owning player view, use WASD, mouse look, Space to jump, and F to request the RPC example. Esc releases the cursor. On the server/host, P spawns an extra visible object and O despawns it. The extra object has no controlling session.
4. A newly joined client should receive existing visible entities and the latest persistent value. In `ScaleDemo`, it receives every entity authored by its player's current server, plus nearby entities authored by other servers. The other demos retain full-world observers. Disconnecting a client despawns its owned player canonically. `HideFrom` only removes a particular client's observer copy; it is not canonical deletion.

For a standalone smoke test, the sample launcher also accepts `-atlas-host`, `-atlas-server`, `-atlas-worker`, or `-atlas-client`. Add `-atlas-scene=ServerAuthority`, `-atlas-scene=ScaleDemo`, or `-atlas-scene=PhysicsDemo` to load that built scene before starting the role. `-atlas-test-rpc` makes the local player request the sample action once, and `-atlas-metrics` writes a server metric line every five seconds. These switches belong only to the sample launcher, not to the framework API.

The `ClientAuthority` prefab has an owner-written position and rotation. In `ServerAuthority`, only yaw/pitch are owner-written; the client sends movement input to the server, which moves its CharacterController on the network tick. There is deliberately **no movement prediction** in this demo, so server-authoritative movement has visible input delay. Aim remains immediate for the local owner. No artificial latency is configured.

The `ScaleDemo` scene spawns 50 moving cubes and no stationary networked cubes. A joined client controls a green top-down player marker with WASD; a translucent green disk highlights its interest entry radius. Set the radius and exit padding on its `NetworkInterestSource` component in the `ScaleInterestPlayer` prefab. The disk deliberately excludes the exit padding. The coordinator sends the client every entity on its player's current authoring server. For entities authored by other servers, entering the radius spawns an observer copy and leaving the radius plus exit padding hides it. Network updates and RPCs follow the same observer set. Each server requests a translucent debug overlay for its simulation region; authoritative cubes are blue and interested ghosts are amber. Worker ghost delivery follows the player radius, not a fixed region-border band, while the first server retains the full canonical directory. Its metrics report server tick time, managed allocations during each tick when the runtime counter is available, aggregate bytes sent during the last tick, entity count, and remote observer copies. These are local measurements, not a capacity promise.

The `PhysicsDemo` scene uses a separate `PhysicsPlayer` prefab cloned from the server-authoritative player. Its small `PhysicsPlayerPush` component applies a horizontal force to dynamic Rigidbodies when the server-side CharacterController walks into them; the normal `ServerPlayer` is unchanged. The scene spawns three registered Rigidbody cubes above the ground. They should fall and collide on the server while a joined client sees kinematic copies follow their position and rotation. Walk into a cube as the host or a joined client to test pushing it; press **R in the host/server Game view** for an independent impulse test. Client copies do not apply their own push forces. This interaction still needs an interactive Multiplayer Play Mode check.

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

`IsOwner` tells client-side input and camera code whether it controls this object. `HasAuthority` tells simulation code whether it may write canonical gameplay state. These are different even in the local demo: a client can own its avatar while the server has authority over its persistent variables. `IsServer`, `IsClient`, and `IsHost` describe the process role, not the writer of a particular entity. `NetworkBehaviour` also exposes `NetworkManager` and `IsSpawned` for familiar access. The local adapter uses ordered reliable TCP. RPC arguments currently support `int`, `float`, `bool`, `string`, `Vector2`, `Vector3`, and `Quaternion`.

`NetworkVariable<T>` fields register automatically when the behaviour spawns; declare and initialize them on the behaviour. By default, the server writes and everyone reads. For owner-written aim or weapon selection, use `new NetworkVariable<Quaternion>(Quaternion.identity, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner)`. The server validates the controlling session, retains the current value, and relays it to other observers. `ReadPermission.Owner` limits snapshots and later updates to the owner and server. Current values are included in spawn snapshots for permitted late observers before `OnNetworkSpawn`. Subscribe to `OnValueChanged(previous, current)` for later updates; `Changed` remains an alias. Supported value types are `int`, `float`, `bool`, `string`, `Vector3`, and `Quaternion`. `Set(...)` and the `Value` setter are equivalent. The sample scenes use prefab references in their spawners; prefab IDs remain internal registration keys.

`NetworkTransform` can interpolate received position and rotation on remote clients over one network tick; it never smooths the local owner or server simulation. `NetworkAnimator` can reference a child `Animator` and synchronizes changed bool, int, and float parameters plus current layer states, with a full state snapshot for late join. Choose Server or Owner as its writer. Use `NetworkAnimator.SetTrigger(name)` for transient triggers; plain `Animator.SetTrigger` cannot be sampled reliably. This first animator helper has not yet been tested against the imported shooter animations or complex transitions.

For a server-simulated 3D physics object, place `NetworkObject`, `Rigidbody`, `NetworkTransform`, and `NetworkRigidbody` on the same GameObject (plus an appropriate Collider). Leave the prefab Rigidbody non-kinematic for dynamic physics. Set `NetworkTransform` to synchronize both position and rotation, with **Server** as both writers, and leave its target on the root. The server keeps the prefab's physics mode; client copies are kinematic and follow the replicated pose. Apply forces or impulses only on the server in `FixedUpdate` (or send client input intent to the server first). `NetworkRigidbody` does not replicate forces, velocity, or collision callbacks, and it does not add prediction. Remote collision responses are not authoritative; send gameplay effects from server collision events when needed. `PhysicsDemo` is the first interactive validation scene, but its behavior still needs a Multiplayer Play Mode pass.

The IL post-processor currently supports instance `void` RPC methods on non-generic `NetworkBehaviour` types, with no `try`/`catch` in the RPC body. Unsupported signatures produce a compile error. The receive dispatcher still uses reflection and has not yet been validated in an IL2CPP build; generated dispatch or explicit preservation rules may be needed before claiming AOT/player-build support. This change does not add prediction, handoff, or worker routing.

`UnityEngine` also defines an `EntityId` in this editor version, so qualify this package's type as `AtlasNet.EntityId` in code that imports both namespaces.

## Boundaries of this first pass

The single-server examples remain the validated baseline. An experimental local multi-worker mode now exercises worker ghosts, bounded worker-interest delivery, routing, and region handoffs, but it has not had a multi-instance Unity runtime validation pass; see `LocalWorkers.md`. Neither mode provides reconnect/resume, production transport tuning, encryption, prediction, rollback, lag compensation, distributed physics, or the C++ AtlasNet integration.

The initial `NetworkRigidbody` component supports server-simulated 3D bodies, but the current examples still use `CharacterController`. Rigidbody Play Mode validation, distributed collision, and predicted physics remain outside the demonstrated scope.

The familiar core components are `NetworkManager`, `NetworkObject`, `NetworkBehaviour`, `NetworkTransform`, a first `NetworkAnimator`, `NetworkRigidbody`, and `NetworkPrefabsList`. AtlasNet does not yet provide NGO's scene synchronization or prefab overrides. Canonical dynamic spawning currently uses `NetworkManager.Spawn(prefab, position, rotation, owner)` rather than `Instantiate(prefab)` followed by `NetworkObject.Spawn()`. `NetworkTransform` has separate position and rotation writers because input ownership is not the same as simulation authority; this difference is intentional.

Before claiming the demo complete, exercise a non-host client in both authority scenes, the three RPC destinations, late join, spawn/despawn, the scale scene, and a package install in a separate clean project. EditMode asset checks alone cannot verify Play Mode behavior.
