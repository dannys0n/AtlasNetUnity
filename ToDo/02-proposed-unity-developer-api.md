# AtlasNet — Proposed Unity Developer API

> **Status: API design sketches, not fixed implementation requirements.** Use [AGENTS.md](../AGENTS.md) for the current demo scope. The current package intentionally uses NGO-like `IsOwner`, `HasAuthority`, and `[Rpc(SendTo.X)]`; older `HasInputAuthority` / `HasSimulationAuthority` / separate RPC attribute sketches below are exploratory, not the current API. Names, signatures, authority checks, and handoff-facing examples must be validated against working Unity code before becoming public API.

## 1. API Design Goals

The public API should feel unsurprising to an NGO or FishNet developer while preserving AtlasNet's distributed authority model.

The examples in this document are design sketches, not locked signatures. Names and ergonomics can change after the prototype reveals what is awkward.

Principles:

- gameplay code addresses entities and sessions, not processes;
- simulation authority and input authority are explicit and separate;
- common cases are concise;
- distributed-system details are available for diagnostics, but not required for ordinary gameplay;
- dangerous operations fail loudly during development;
- attributes express RPC intent and variable policy;
- handoff is normally transparent, with optional advanced callbacks.

## 2. Core Value Types

Strong value types prevent accidental mixing of unrelated identifiers.

```csharp
public readonly record struct EntityId(ulong Value);
public readonly record struct SessionId(ulong Value);
public readonly record struct ConnectionId(ulong Value);
public readonly record struct WorkerId(ushort Value);
public readonly record struct NetworkTick(uint Value);
public readonly record struct AuthorityEpoch(uint Value);
public readonly record struct PrefabId(uint Value);
```

`WorkerId` should rarely appear in gameplay scripts. It primarily exists for backend adapters, diagnostics, and test tooling.

## 3. `NetworkObject`

`NetworkObject` identifies a replicated Unity object and exposes its local role.

```csharp
public sealed class NetworkObject : MonoBehaviour
{
    public EntityId EntityId { get; }
    public PrefabId PrefabId { get; }
    public bool IsSpawned { get; }

    public bool HasInputAuthority { get; }
    public bool HasSimulationAuthority { get; }
    public bool IsObservedLocally { get; }
    public bool IsWorkerGhost { get; }

    public SessionId? InputAuthoritySession { get; }
    public AuthorityEpoch AuthorityEpoch { get; }

    public void Despawn();
}
```

Normal code should ask `HasSimulationAuthority`; it should not compare itself to a current worker ID.

## 4. `NetworkBehaviour`

Networked gameplay components derive from `NetworkBehaviour`.

```csharp
public abstract class NetworkBehaviour : MonoBehaviour
{
    public NetworkObject NetworkObject { get; }

    public EntityId EntityId => NetworkObject.EntityId;
    public bool HasInputAuthority => NetworkObject.HasInputAuthority;
    public bool HasSimulationAuthority => NetworkObject.HasSimulationAuthority;
    public bool IsWorkerGhost => NetworkObject.IsWorkerGhost;

    public virtual void OnNetworkSpawn() { }
    public virtual void OnNetworkDespawn() { }

    public virtual void OnInputAuthorityGained() { }
    public virtual void OnInputAuthorityLost() { }

    public virtual void OnSimulationAuthorityGained(
        AuthorityChange change) { }

    public virtual void OnSimulationAuthorityLost(
        AuthorityChange change) { }
}
```

Most scripts should only need `OnNetworkSpawn`, `OnNetworkDespawn`, and authority booleans. Handoff callbacks are advanced hooks for caches, physics state, or worker-local resources.

## 5. A Simple Authoritative Component

```csharp
public sealed class Health : NetworkBehaviour
{
    public NetworkVariable<int> Current { get; } = new(100);

    [AuthorityRpc]
    public void ApplyDamage(int amount, AuthorityRpcContext context = default)
    {
        if (amount <= 0)
            return;

        Current.Value = Math.Max(0, Current.Value - amount);

        if (Current.Value == 0)
            PlayDeathEffect();
    }

    [ObserversRpc]
    private void PlayDeathEffect()
    {
        // Presentation only. Canonical death state is Current.Value == 0.
    }
}
```

Invocation means “execute on the current simulation authority,” whether the caller is local, a client, or another worker.

## 6. `NetworkVariable<T>`

A proposed minimal shape:

```csharp
public sealed class NetworkVariable<T>
{
    public NetworkVariable(T initialValue = default!);

    public T Value { get; set; }

    public event Action<T, T> Changed;
}
```

Example with a change handler:

```csharp
public sealed class Door : NetworkBehaviour
{
    public NetworkVariable<bool> IsOpen { get; } = new(false);

    public override void OnNetworkSpawn()
    {
        IsOpen.Changed += OnOpenChanged;
        OnOpenChanged(IsOpen.Value, IsOpen.Value);
    }

    public override void OnNetworkDespawn()
    {
        IsOpen.Changed -= OnOpenChanged;
    }

    [AuthorityRpc(RequireInputAuthority = false)]
    public void SetOpen(bool open, AuthorityRpcContext context = default)
    {
        if (!CanUseDoor(context.SenderSession))
            return;

        IsOpen.Value = open;
    }

    private void OnOpenChanged(bool previous, bool current)
    {
        animator.SetBool("Open", current);
    }
}
```

Prototype write rule: assigning `Value` without simulation authority throws or logs a clear development error.

Potential later policies—not required immediately—include reliability, send frequency, priority, permissions, interpolation, and custom equality.

```csharp
[Networked(Reliability = Reliability.Unreliable, SendRate = 10)]
public NetworkVariable<Vector3> AimDirection { get; } = new();
```

The prototype should choose either wrapper-based declarations or field attributes and use that approach consistently. It does not need both.

## 7. `AuthorityRpc`

```csharp
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuthorityRpcAttribute : Attribute
{
    public bool RequireInputAuthority { get; init; } = true;
    public RpcDelivery Delivery { get; init; } = RpcDelivery.Reliable;
}
```

Example: client-controlled movement input.

```csharp
public sealed class PlayerMotor : NetworkBehaviour
{
    [AuthorityRpc(RequireInputAuthority = true,
                  Delivery = RpcDelivery.Unreliable)]
    private void SubmitInput(
        PlayerInput input,
        NetworkTick inputTick,
        AuthorityRpcContext context = default)
    {
        pendingInputs.Enqueue(inputTick, input);
    }
}
```

Example: worker-to-worker damage where the caller does not own input for the target.

```csharp
public sealed class Projectile : NetworkBehaviour
{
    private void OnAuthoritativeHit(EntityId target, int damage)
    {
        AtlasNet.Entities
            .Get<Health>(target)
            .ApplyDamageFromServer(damage, EntityId);
    }
}

public sealed class Health : NetworkBehaviour
{
    [AuthorityRpc(RequireInputAuthority = false)]
    public void ApplyDamageFromServer(
        int amount,
        EntityId source,
        AuthorityRpcContext context = default)
    {
        if (!context.IsTrustedWorker)
            return;

        Current.Value -= amount;
    }
}
```

Suggested context data:

```csharp
public readonly struct AuthorityRpcContext
{
    public SessionId? SenderSession { get; init; }
    public WorkerId? SenderWorker { get; init; }
    public NetworkTick SentTick { get; init; }
    public AuthorityEpoch RoutedEpoch { get; init; }
    public bool IsTrustedWorker { get; init; }
}
```

The runtime, not gameplay code, resolves the current authority worker. If the route changed during transit, the prototype may perform one bounded reroute using the newer epoch.

## 8. `ObserversRpc`

Use observer RPCs for transient events that should not become durable state.

```csharp
public sealed class WeaponEffects : NetworkBehaviour
{
    [ObserversRpc(Delivery = RpcDelivery.Unreliable)]
    private void PlayMuzzleFlash(Vector3 position, Vector3 forward)
    {
        effects.SpawnMuzzleFlash(position, forward);
    }
}
```

An entity's current interest set determines recipients. A late-joining observer does not replay this event; it receives replicated state instead.

## 9. `TargetRpc`

Target a stable session, not a connection ID.

```csharp
public sealed class InteractableChest : NetworkBehaviour
{
    [TargetRpc]
    private void ShowOpenResult(
        SessionId target,
        ChestOpenResult result)
    {
        ui.ShowChestResult(result);
    }
}
```

If a reconnect replaces the transport connection while the session remains valid, session routing can follow the new connection.

## 10. Spawning and Despawning

Proposed spawning service:

```csharp
public interface INetworkSpawner
{
    NetworkObject Spawn(
        NetworkPrefabReference prefab,
        Vector3 position,
        Quaternion rotation,
        SpawnOptions options = default);

    void Despawn(EntityId entity);
}
```

```csharp
if (HasSimulationAuthority)
{
    NetworkObject enemy = AtlasNet.Spawner.Spawn(
        enemyPrefab,
        spawnPoint.position,
        spawnPoint.rotation,
        new SpawnOptions
        {
            InputAuthority = null,
            InitialAuthorityDomain = currentDomain
        });
}
```

Only authorized server-side code should create canonical entities. Observers reconstruct them from spawn messages using the prefab registry.

```csharp
NetworkObject.Despawn();
```

This means canonical deletion. It must not be used for interest exit or authority handoff.

## 11. Player Sessions and Reconnect

Proposed public session lifecycle:

```csharp
public interface ISessionService
{
    event Action<SessionInfo> SessionJoined;
    event Action<SessionInfo> SessionReconnected;
    event Action<SessionId> SessionDisconnected;
    event Action<SessionId> SessionExpired;

    SessionId LocalSession { get; }
}
```

Example player component:

```csharp
public sealed class PlayerAvatar : NetworkBehaviour
{
    public override void OnInputAuthorityGained()
    {
        input.Enable();
    }

    public override void OnInputAuthorityLost()
    {
        input.Disable();
    }
}
```

Disconnect should remove input authority or place it in a suspended state without automatically despawning the avatar. Session expiry policy decides what happens later.

## 12. Authority Handoff Callbacks

```csharp
public readonly struct AuthorityChange
{
    public AuthorityEpoch PreviousEpoch { get; init; }
    public AuthorityEpoch CurrentEpoch { get; init; }
    public NetworkTick EffectiveTick { get; init; }
}
```

Example physics role transition:

```csharp
public sealed class NetworkRigidbody : NetworkBehaviour
{
    [SerializeField] private Rigidbody body;

    public override void OnSimulationAuthorityGained(AuthorityChange change)
    {
        body.isKinematic = false;
        body.linearVelocity = replicatedVelocity.Value;
    }

    public override void OnSimulationAuthorityLost(AuthorityChange change)
    {
        body.isKinematic = true;
    }
}
```

The networking component should own this standard transition so most gameplay scripts never implement it themselves.

## 13. Network Tick API

```csharp
public interface INetworkTime
{
    NetworkTick Tick { get; }
    double Time { get; }
    float TickDeltaTime { get; }

    event Action<NetworkTick> TickStarted;
}
```

Authoritative simulation example:

```csharp
public sealed class EnemyBrain : NetworkBehaviour
{
    private void OnEnable()
    {
        AtlasNet.Time.TickStarted += OnNetworkTick;
    }

    private void OnDisable()
    {
        AtlasNet.Time.TickStarted -= OnNetworkTick;
    }

    private void OnNetworkTick(NetworkTick tick)
    {
        if (!HasSimulationAuthority)
            return;

        SimulateAI(AtlasNet.Time.TickDeltaTime);
    }
}
```

The first prototype does not promise rollback or deterministic resimulation.

## 14. Interest Management API

Keep the initial contract small:

```csharp
public interface IInterestPolicy
{
    InterestSet Evaluate(in InterestQuery query);
}
```

The default implementation can map positions to grid cells and include a neighbor radius. Gameplay code may supply hints later, but it should not manually spawn and remove observer copies.

Possible diagnostic access:

```csharp
InterestCell cell = AtlasNet.Diagnostics.GetInterestCell(EntityId);
int observerCount = AtlasNet.Diagnostics.GetObserverCount(EntityId);
```

This belongs to diagnostics, not normal game logic.

## 15. Entity References

References transmitted over the network should use stable entity IDs rather than Unity instance IDs or direct object references.

```csharp
public readonly record struct NetworkEntityReference(EntityId EntityId);
```

```csharp
[AuthorityRpc]
private void BeginInteraction(NetworkEntityReference target)
{
    if (!AtlasNet.Entities.TryGet(target.EntityId, out NetworkObject obj))
        return;

    // Validate distance and gameplay rules on simulation authority.
}
```

Resolution may fail because the entity despawned or is not locally materialized. APIs should make that possibility explicit.

## 16. Serialization Extension Point

```csharp
public interface INetworkSerializable
{
    void Serialize<TWriter>(ref TWriter writer)
        where TWriter : INetworkWriter;

    void Deserialize<TReader>(ref TReader reader)
        where TReader : INetworkReader;
}
```

```csharp
public struct PlayerInput : INetworkSerializable
{
    public Vector2 Move;
    public bool Jump;

    public void Serialize<TWriter>(ref TWriter writer)
        where TWriter : INetworkWriter
    {
        writer.WriteVector2(Move);
        writer.WriteBool(Jump);
    }

    public void Deserialize<TReader>(ref TReader reader)
        where TReader : INetworkReader
    {
        Move = reader.ReadVector2();
        Jump = reader.ReadBool();
    }
}
```

Reflection fallback is acceptable in the prototype. This explicit path proves that generated serializers can be introduced later without changing gameplay meaning.

## 17. Backend-Facing Interfaces

These are runtime integration points, not everyday gameplay APIs.

```csharp
public interface IAuthorityResolver
{
    AuthorityLocation Resolve(EntityId entity);
    event Action<EntityId, AuthorityLocation> AuthorityChanged;
}

public readonly record struct AuthorityLocation(
    WorkerId Worker,
    AuthorityEpoch Epoch);
```

```csharp
public interface IWorldCoordinator
{
    EntityId AllocateEntityId();
    void RegisterEntity(EntityId entity, AuthorityLocation authority);
    HandoffTicket BeginHandoff(EntityId entity, WorkerId destination);
    void CommitHandoff(HandoffTicket ticket);
}
```

```csharp
public interface IMessageBus
{
    void SendToAuthority(EntityId entity, MessageEnvelope message);
    void SendToSession(SessionId session, MessageEnvelope message);
    void SendToObservers(EntityId entity, MessageEnvelope message);
    void SendToWorker(WorkerId worker, MessageEnvelope message);
}
```

The prototype can implement all three in memory while retaining real serialization and message envelopes.

## 18. Debug API and Inspector Model

```csharp
public readonly struct EntityDebugSnapshot
{
    public EntityId Entity { get; init; }
    public PrefabId Prefab { get; init; }
    public SessionId? InputAuthority { get; init; }
    public WorkerId SimulationAuthority { get; init; }
    public AuthorityEpoch Epoch { get; init; }
    public NetworkTick LastUpdatedTick { get; init; }
    public LocalEntityRole LocalRole { get; init; }
    public int ObserverCount { get; init; }
    public InterestCell Cell { get; init; }
}
```

Recommended editor/runtime views:

- selected `NetworkObject` inspector;
- entity directory table;
- session-to-connection table;
- worker map with authority domains;
- RPC route/event stream;
- stale epoch rejection counter;
- handoff timeline.

## 19. Naming Decisions to Revisit After the Demo

Questions best answered by using the prototype:

- Should an NGO-compatible alias such as `[ServerRpc]` map to `[AuthorityRpc]`, or would that hide too much?
- Should replicated state use `NetworkVariable<T>`, a field attribute, or generated properties?
- Should “input authority” be called “ownership” in convenience APIs?
- Which authority callbacks are public versus internal?
- Should RPC methods be invoked normally, or through generated proxies?
- How should async RPC failures and reroutes be surfaced?

The first pass should prefer semantic clarity over exact compatibility. `AuthorityRpc` and `HasSimulationAuthority` make the distributed model explicit and are good defaults until real usage suggests otherwise.
