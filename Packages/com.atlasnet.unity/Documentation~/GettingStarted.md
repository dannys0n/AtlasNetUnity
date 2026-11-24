# Get started with AtlasNet Unity

Install `com.atlasnet.unity` from the package Git URL in the repository README. Import **00 Shared** before importing the gameplay samples.

## Run locally in Play Mode

Open **01 Client Authority** and choose **Start Local Host**. The sample NetworkManager prefab enables **Editor Loopback**. It runs one in-process host for gameplay, spawning, and RPC iteration without WSL, Docker, or a native process. Disable it before building a player. Use AtlasNet to test remote clients, spatial interest, and handoffs.

## Connect to an AtlasNet world

1. Disable **Editor Loopback** and leave **Backend Override** empty to use the native adapter.
2. Register the same network prefabs on the clients and the shard player builds. A Unity shard launched by AtlasNet starts as a server using the IPC arguments supplied by the shard module.
3. Set the manager's **Local Address** and **Port** to the AtlasNet client endpoint, then call `StartClient()`. The bundled Windows client uses UDP. Browser builds use a WebSocket endpoint. Configure listeners in the AtlasNet deployment.

The AtlasNet adapter currently supports the Unity shard connection, entity lifecycle, transform and Animator replication, variables, and routed entity RPCs. Named messages, client-requested spawning, and manual per-client visibility overrides remain unavailable with this adapter.

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

`NetworkTransform` can buffer and interpolate received position and rotation. Server-written channels can also interpolate on the owning client. `NetworkAnimator` can reference a child `Animator` and synchronizes changed bool, int, and float parameters plus current layer states, with a full state snapshot for late join. Choose Server or Owner as its writer. Use `NetworkAnimator.SetTrigger(name)` for transient triggers; plain `Animator.SetTrigger` cannot be sampled reliably. This first animator helper has not yet been tested against the imported shooter animations or complex transitions.

For a server-simulated 3D physics object, place `NetworkObject`, `Rigidbody`, `NetworkTransform`, and `NetworkRigidbody` on the same GameObject (plus an appropriate Collider). Leave the prefab Rigidbody non-kinematic for dynamic physics. Set `NetworkTransform` to synchronize both position and rotation, with **Server** as both writers, and leave its target on the root. The server keeps the prefab's physics mode; client copies are kinematic and follow the replicated pose. Apply forces or impulses only on the server in `FixedUpdate` (or send client input intent to the server first). `NetworkRigidbody` does not replicate forces, velocity, or collision callbacks, and it does not add prediction. Remote collision responses are not authoritative; send gameplay effects from server collision events when needed. `04_Physics` is the first interactive validation scene, but its behavior still needs a Multiplayer Play Mode pass.

The IL post-processor currently supports instance `void` RPC methods on non-generic `NetworkBehaviour` types, with no `try`/`catch` in the RPC body. Unsupported signatures produce a compile error. The receive dispatcher still uses reflection and has not yet been validated in an IL2CPP build; generated dispatch or explicit preservation rules may be needed before claiming AOT/player-build support. RPC weaving does not add prediction; local handoff and worker routing are separate runtime paths.

`UnityEngine` also defines an `EntityId` in this editor version, so qualify this package's type as `AtlasNet.EntityId` in code that imports both namespaces.
