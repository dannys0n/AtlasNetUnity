# AtlasNet Unity: first local demo

This package targets Unity **6000.6.2f1** for the current example project. The project uses **Multiplayer Play Mode 3.0.0**. The networking package itself does not depend on NGO, FishNet, PurrNet, Netick, Unity online services, or a game input package.

## Install and import

The source project treats `Packages/com.atlasnet.unity` as an embedded UPM package. In a separate Unity project, use Package Manager > **Add package from disk** and select this package's `package.json`. The package has normal `Runtime/`, `Samples~/`, and `Documentation~/` directories so the same folder can later be published as a Git package.

In Package Manager, select **AtlasNet Unity** and import **Simple Authority Demo**. The sample's scripts, prefabs, and scenes are editable project assets after import. The runtime package stays in `Packages`, not in the project's `Assets` directory. This sample uses Unity's legacy `Input` API to keep the controller readable; set **Active Input Handling** to **Both** or **Input Manager** in Player Settings. The package API itself does not require that input system.

## Run with Multiplayer Play Mode

1. Open `ClientAuthority`, `ServerAuthority`, or `ScaleDemo` from the imported sample. Install **Multiplayer Play Mode 3.0.0** and start the main Editor Player plus at least one additional editor instance. This project has that package in its manifest.
2. In the main Game view, click **Start Host**. In each additional Game view, click **Join as Client**. Alternatively, use **Start Server Only** in one view and **Join as Client** in every other view. All instances use `127.0.0.1:7777`; no gameplay script edits are needed.
   The demo keeps every instance running when its window loses focus, so the host can accept joins while you interact with a client window. Keyboard and mouse input still go only to the focused Game view.
3. In an owning player view, use WASD, mouse look, Space to jump, and F to request the RPC example. Esc releases the cursor. On the server/host, P spawns an extra visible object and O despawns it. The extra object has no controlling session.
4. A newly joined client should receive all existing visible entities and the latest persistent value. Disconnecting a client despawns its owned player canonically. `HideFrom` only removes a particular client's observer copy; it is not canonical deletion.

For a standalone smoke test, the sample launcher also accepts `-atlas-host`, `-atlas-server`, or `-atlas-client`. Add `-atlas-scene=ServerAuthority` or `-atlas-scene=ScaleDemo` to load that built scene before starting the role. `-atlas-test-rpc` makes the local player request the sample action once, and `-atlas-metrics` writes a server metric line every five seconds. These switches belong only to the sample launcher, not to the framework API.

The `ClientAuthority` prefab has an owner-written position and rotation. In `ServerAuthority`, only yaw/pitch are owner-written; the client sends movement input to the server, which moves its CharacterController on the network tick. There is deliberately **no movement prediction** in this demo, so server-authoritative movement has visible input delay. Aim remains immediate for the local owner. No artificial latency is configured.

The `ScaleDemo` scene adds 300 registered cubes, of which 50 move. Idle transforms send no tick update. Its overlay reports server tick time, managed allocations during each tick when the runtime counter is available, aggregate bytes sent during the last tick, entity count, and remote observer copies. These are local measurements, not a capacity promise.

## What gameplay code looks like

`NetworkObject` holds a stable runtime `AtlasNet.EntityId`, a separate prefab ID, and a controlling `SessionId`. Attach `NetworkBehaviour` components on the same GameObject as `NetworkObject`. Register spawnable prefabs with matching string IDs on each scene's `NetworkManager`.

```csharp
public sealed class ExampleAction : NetworkBehaviour
{
    private NetworkVariable<int> count;

    public override void OnNetworkSpawn() => count = RegisterVariable(1, 0);

    public void RequestAction() => AuthorityRpc(1);

    protected override void OnRpc(ushort method, NetReader payload, SessionId sender)
    {
        if (method != 1 || !HasSimulationAuthority) return;
        count.Set(count.Value + 1);  // Persistent server-written state.
        ObserversRpc(2);             // Transient event to observers.
        TargetRpc(sender, 3);        // Transient event to one session.
    }
}
```

RPC calls are explicit methods, not magic attributes. The manager serializes a method ID and payload, validates an authority call against the entity's controlling session, and dispatches on the destination instance. `AuthorityRpc` permits the controlling session, `ObserversRpc` sends from the server to observers, and `TargetRpc` sends from the server to one session. These use ordered reliable TCP in the local adapter. `NetworkVariable<T>` is server-written and included in the spawn snapshot for late observers. Supported demo value types are `int`, `float`, `bool`, `string`, `Vector3`, and `Quaternion`.

`UnityEngine` also defines an `EntityId` in this editor version, so qualify this package's type as `AtlasNet.EntityId` in code that imports both namespaces.

## Boundaries of this first pass

The local TCP adapter only supports one Unity server on localhost. It has no reconnect/resume, production transport tuning, encryption, prediction, rollback, lag compensation, distributed physics, cross-worker ghosts, worker routing, or authority handoff. The stable entity/session IDs, independent transform channel writers, and entity-directed RPCs are intentional seams for a future AtlasNet adapter. They do **not** prove that later handoff or multi-worker semantics are implemented.

Optional dynamic `Rigidbody` synchronization is deferred. The examples use `CharacterController`, and neither scene demonstrates distributed collision or predicted physics.

Before claiming the demo complete, exercise a non-host client in both authority scenes, the three RPC destinations, late join, spawn/despawn, the scale scene, and a package install in a separate clean project. EditMode asset checks alone cannot verify Play Mode behavior.
