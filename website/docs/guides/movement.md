---
title: Movement and mouse look
description: Compare a local controller, owner-written movement, and server simulation from input intent.
---

## What changes from single-player?

The supplied `SimpleLocalMovement` is a comparison baseline, not a network component. It reads legacy input, maintains vertical speed, and calls `CharacterController.Move` each rendered frame.

Its core movement calculation is:

```csharp
Vector3 movement = (transform.right * input.x + transform.forward * input.y) * speed;
movement.y = verticalSpeed;

controller.Move(movement * Time.deltaTime);
```

Networking does not require inventing a new character motor. It requires choosing **who runs it**, **who writes the resulting position**, and **how everyone else receives that state**.

## Client-authoritative movement

For owner-written movement, the local controller changes from `MonoBehaviour` to `NetworkBehaviour` and gates input/simulation on `IsOwner`. A `NetworkTransform` with **Position Writer = Owner** and **Rotation Writer = Owner** transmits its pose on network ticks.

This complete minimal example keeps the baseline movement calculation:

```csharp title="ClientMovement.cs"
using AtlasNet;
using UnityEngine;

public sealed class ClientMovement : NetworkBehaviour
{
    [SerializeField] private CharacterController controller;
    [SerializeField] private float speed = 5f;
    [SerializeField] private float jumpSpeed = 5f;
    private float verticalSpeed;

    private void Update()
    {
        if (!IsOwner) return;

        Vector2 input = new(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        input = Vector2.ClampMagnitude(input, 1);

        if (controller.isGrounded && verticalSpeed < 0)
            verticalSpeed = -1f;

        if (controller.isGrounded && Input.GetButtonDown("Jump"))
            verticalSpeed = jumpSpeed;

        verticalSpeed += Physics.gravity.y * Time.deltaTime;

        Vector3 movement = (transform.right * input.x + transform.forward * input.y) * speed;
        movement.y = verticalSpeed;

        controller.Move(movement * Time.deltaTime);
    }
}
```

Assign `controller` in the prefab Inspector. Remote copies do not read local input or run this motor; they follow replicated pose. The server accepts this client's movement channel, so this is not cheat-resistant server movement.

## Server-authoritative movement

The owner reads input in `Update`, captures short button presses, and sends input intent in `OnNetworkTick`. The current simulation worker runs the motor with a fixed network-tick delta. It does **not** accept a client-submitted position.

The following follows the current `SimpleServerMovement` pattern, including its handoff state:

```csharp title="ServerMovement.cs"
using AtlasNet;
using UnityEngine;

public sealed class ServerMovement : NetworkBehaviour
{
    [SerializeField] private CharacterController controller;
    [SerializeField] private float speed = 5f;
    [SerializeField] private float jumpSpeed = 5f;
    private Vector2 input;
    private float yaw;
    private bool jumpQueued;
    private float verticalSpeed;

    private void Update()
    {
        if (!IsOwner) return;

        input = Vector2.ClampMagnitude(
            new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1);
        yaw = transform.eulerAngles.y;

        if (Input.GetButtonDown("Jump"))
            jumpQueued = true;
    }

    public override void OnNetworkTick()
    {
        if (IsOwner)
        {
            Vector2 move = input;
            float facing = yaw;
            bool jump = jumpQueued;
            jumpQueued = false;

            ReceiveInputRpc(move, facing, jump);
        }

        if (HasAuthority)
            Simulate();
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
    private void ReceiveInputRpc(Vector2 move, float facing, bool jump)
    {
        input = Vector2.ClampMagnitude(move, 1);
        yaw = facing;
        jumpQueued |= jump;
    }

    private void Simulate()
    {
        float delta = 1f / NetworkManager.TickRate;

        if (controller.isGrounded && verticalSpeed < 0)
            verticalSpeed = -1f;

        if (controller.isGrounded && jumpQueued)
            verticalSpeed = jumpSpeed;

        jumpQueued = false;
        verticalSpeed += Physics.gravity.y * delta;

        Vector3 direction = Quaternion.Euler(0, yaw, 0) * new Vector3(input.x, 0, input.y);
        Vector3 movement = direction * speed;
        movement.y = verticalSpeed;

        controller.Move(movement * delta);
    }

    protected override void WriteHandoffState(NetWriter writer)
    {
        writer.Write(input.x);
        writer.Write(input.y);
        writer.Write(yaw);
        writer.Write(jumpQueued);
        writer.Write(verticalSpeed);
    }

    protected override void ReadHandoffState(NetReader reader)
    {
        input = new Vector2(reader.ReadFloat(), reader.ReadFloat());
        yaw = reader.ReadFloat();
        jumpQueued = reader.ReadBool();
        verticalSpeed = reader.ReadFloat();
    }
}
```

Set the root `NetworkTransform` to **Position Writer = Server**, **Rotation Writer = Owner**. Do not attach this and the client movement motor to the same player.

Yaw travels with intent to define which direction “forward” meant for that input. Reading a separately arriving ghost rotation could pair movement with a different aim update. Including yaw does not make mouse look server-authoritative; this motor uses yaw for direction, not to overwrite the owner's view.

This minimal example is **not prediction** and is not a complete hostile-client validation layer. A joined client waits for received server movement. For a production motor, also validate input ranges/finiteness, rates, stale input, and gameplay rules.

## Keep mouse look local

Owner mouse look should run in rendered-frame `Update`, not wait for a round trip or move a physics root to match a received server yaw. `SimpleLook` applies immediate local yaw/pitch, then publishes owner-written state on network ticks.

The key owner-input calculation is:

```csharp
if (!IsOwner) return;

transform.Rotate(0f, Input.GetAxisRaw("Mouse X") * sensitivity, 0f);
pitch = Mathf.Clamp(pitch - Input.GetAxisRaw("Mouse Y") * sensitivity, -85f, 85f);
pitchPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
```

This is a fragment of the look controller; `pitchPivot`, `pitch`, and `sensitivity` are its configured/state fields. Root transform replication does not include an independently rotating child pitch pivot. Use the sample's owner-written pitch variable or a separately targeted rotation channel to reproduce pitch for remote visuals.

Preserve the original pivot and weapon hierarchy. Replicating pitch on the wrong parent can make the gun orbit around the player, and deriving a shot from an unrelated server camera can offset impacts.

## Which update loop?

| Loop | Use in these examples |
| --- | --- |
| Unity `Update` | Read input, capture button-down edges, apply raw local mouse look |
| `OnNetworkTick` | Send intent/state; simulate the sample's server CharacterController using `1 / TickRate` |
| Unity `FixedUpdate` | Apply server-authoritative Rigidbody physics in the separate physics sample |

Do not run the same motor in both network ticks and `FixedUpdate`. Network ticks are driven by the manager and are not a replacement for Unity's entire physics clock. See [physics](./physics-animation.md) for the Rigidbody configuration.
