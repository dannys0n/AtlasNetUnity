using AtlasNet;
using UnityEngine;

/// <summary>Client sends only input intent. The server moves the controller once per network tick; no prediction.</summary>
public sealed class SimpleServerMovement : NetworkBehaviour
{
    private const ushort InputMethod = 1;
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
        input = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1);
        yaw = transform.eulerAngles.y;
        if (Input.GetButtonDown("Jump")) jumpQueued = true;
    }

    public override void OnNetworkTick()
    {
        if (IsOwner)
        {
            Vector2 current = input;
            float currentYaw = yaw;
            bool jump = jumpQueued;
            jumpQueued = false;
            AuthorityRpc(InputMethod, writer =>
            {
                writer.Write(current.x);
                writer.Write(current.y);
                writer.Write(currentYaw);
                writer.Write(jump);
            });
        }
        if (HasSimulationAuthority) Simulate();
    }

    protected override void OnRpc(ushort method, NetReader reader, SessionId sender)
    {
        if (method != InputMethod || !HasSimulationAuthority) return;
        input = Vector2.ClampMagnitude(new Vector2(reader.ReadFloat(), reader.ReadFloat()), 1);
        yaw = reader.ReadFloat();
        jumpQueued |= reader.ReadBool();
    }

    private void Simulate()
    {
        float delta = 1f / NetworkObject.Manager.TickRate;
        if (controller.isGrounded && verticalSpeed < 0) verticalSpeed = -1f;
        if (controller.isGrounded && jumpQueued) verticalSpeed = jumpSpeed;
        jumpQueued = false;
        verticalSpeed += Physics.gravity.y * delta;
        Vector3 direction = Quaternion.Euler(0, yaw, 0) * new Vector3(input.x, 0, input.y);
        Vector3 movement = direction * speed;
        movement.y = verticalSpeed;
        controller.Move(movement * delta);
    }
}
