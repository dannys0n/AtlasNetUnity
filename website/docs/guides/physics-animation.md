---
title: Physics and animation
description: Configure server-authored Rigidbody simulation and reproduce Animator events without duplicating gameplay.
---

## Network a dynamic Rigidbody

On the same prefab root, add:

- `NetworkObject`
- A non-kinematic Unity `Rigidbody` and appropriate collider
- `NetworkTransform`, targeting that root, with position and rotation enabled
- `NetworkRigidbody`

Set **both transform writers to Server**. A root targeting a child or an owner-written rotation channel fails the Rigidbody component's setup checks.

`NetworkRigidbody` makes non-authoritative copies kinematic. Only the currently authoritative worker runs dynamic physics; clients and worker ghosts follow replicated transforms. It also transfers linear and angular velocity during local handoff. The authoritative body retains its prefab's Rigidbody interpolation setting; remote motion is handled by transform replication.

## Apply physics only on authority

This complete component assumes the above root setup:

```csharp title="AuthoritativeForce.cs"
using AtlasNet;
using UnityEngine;

public sealed class AuthoritativeForce : NetworkBehaviour
{
    [SerializeField] private Rigidbody body;
    [SerializeField] private Vector3 acceleration = new(0f, 0f, 2f);

    private void FixedUpdate()
    {
        if (!HasAuthority || body.isKinematic) return;

        body.AddForce(acceleration, ForceMode.Acceleration);
    }
}
```

Do not set velocity or apply forces on kinematic ghosts. A network tick can collect/send input while `FixedUpdate` applies the cached input to Unity physics, as the physics player sample does. That is separate from the CharacterController example's network-tick motor.

For a physics player, freeze root rotation axes as appropriate for your motor and put client-written mouse aim on a separate camera/weapon pivot. Avoid competing mouse yaw and Rigidbody root rotation writers.

:::warning This is not distributed PhysX
Forces and collision callbacks are not automatically replicated. Workers do not share one deterministic physics simulation. Cross-worker collision gameplay, ghost collider policy, handoff at contact, and lag compensation need explicit design/testing; adding `NetworkRigidbody` does not solve them.
:::

The separate `PhysicsDemo` is the starting place for observing these constraints.

## Network an Animator

Put `NetworkAnimator` on the network root and assign the intended Animator, even if it lives in a child. Add one networking component per independently synchronized Animator.

Choose **Authority Mode = Owner** for deliberately owner-authored presentation, or **Server** when authoritative gameplay drives animation. Bool/int/float parameters and changed layer states are synchronized by the component.

Before networking, a trigger might be:

```csharp
animator.SetTrigger("Reload");
```

For a networked transient trigger, use:

```csharp
networkAnimator.SetTrigger("Reload");
```

`networkAnimator` is your assigned `NetworkAnimator` reference. Only its configured writer should call it. Ordinary `Animator.SetTrigger` can be consumed before a networking sample catches it and is not enough to guarantee a replicated event.

## What animation replication does not cover

`NetworkAnimator` does not automatically synchronize procedural recoil, camera sway, aim IK, weapon selection hierarchies, bullet impacts, or shell particles. Replicate persistent selection/aim as state and transient cosmetics as events, then render them locally.

For ghost muzzle flashes, impacts, or shell ejection, use **that ghost's** current weapon/muzzle/ejection transform. Do not reuse the owning client's world transform on another machine. First-person overlay shaders should not be used for other players' world visuals.

Keep simulation and cosmetic effects separate. An observer RPC may play visuals on interested replicas, but those copies must not also apply damage or spawn a second authoritative projectile. Avoid Animator root motion writing the same transform that your motor and `NetworkTransform` already control.
