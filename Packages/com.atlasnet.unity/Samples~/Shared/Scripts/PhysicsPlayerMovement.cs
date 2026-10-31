using AtlasNet;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Rigidbody movement: owner simulates locally or sends intent to its server, matching NetworkTransform.</summary>
public sealed class PhysicsPlayerMovement : NetworkBehaviour
{
    [SerializeField] private Rigidbody body;
    [SerializeField] private Transform aimPivot;
    [SerializeField] private float speed = 5f;
    [SerializeField] private float jumpSpeed = 5f;

    private bool ownerSimulation;
    public override void OnNetworkSpawn() => ownerSimulation = GetComponent<NetworkTransform>().PositionWriter == TransformWriter.Owner;
    private Vector2 input;
    private float yaw;
    private bool ownerJumpQueued;
    private bool jumpQueued;

    private void Update()
    {
        if (!IsOwner) return;
        input = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
        if (Input.GetButtonDown("Jump"))
        {
            if (ownerSimulation) jumpQueued = true;
            else ownerJumpQueued = true;
        }
        if (ownerSimulation) yaw = aimPivot.eulerAngles.y;
    }

    public override void OnNetworkTick()
    {
        if (!IsOwner || ownerSimulation) return;
        bool jump = ownerJumpQueued;
        ownerJumpQueued = false;
        ReceiveInputRpc(input, aimPivot.eulerAngles.y, jump);
    }

    [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
    private void ReceiveInputRpc(Vector2 move, float facing, bool jump)
    {
        if (ownerSimulation) return;
        input = Vector2.ClampMagnitude(move, 1f);
        yaw = facing;
        jumpQueued |= jump;
    }

    private void FixedUpdate()
    {
        if (!IsSpawned || !(ownerSimulation ? IsOwner : HasAuthority) || body.isKinematic) return;

        Vector3 direction = Quaternion.Euler(0f, yaw, 0f) * new Vector3(input.x, 0f, input.y);
        Vector3 velocity = body.linearVelocity;
        body.linearVelocity = new Vector3(direction.x * speed, velocity.y, direction.z * speed);

        if (jumpQueued && body.linearVelocity.y <= 0.1f &&
            gameObject.scene.GetPhysicsScene().Raycast(body.position, Vector3.down, out _, 1.1f, ~0, QueryTriggerInteraction.Ignore))
            body.AddForce(Vector3.up * jumpSpeed, ForceMode.VelocityChange);
        jumpQueued = false;
    }

    protected override void WriteHandoffState(NetWriter writer)
    {
        writer.Write(input.x);
        writer.Write(input.y);
        writer.Write(yaw);
        writer.Write(jumpQueued);
    }

    protected override void ReadHandoffState(NetReader reader)
    {
        input = new Vector2(reader.ReadFloat(), reader.ReadFloat());
        yaw = reader.ReadFloat();
        jumpQueued = reader.ReadBool();
    }
}
