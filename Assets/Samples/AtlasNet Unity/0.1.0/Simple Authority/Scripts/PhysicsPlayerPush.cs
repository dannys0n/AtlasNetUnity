using AtlasNet;
using UnityEngine;

/// <summary>Only the server's CharacterController pushes dynamic bodies.</summary>
public sealed class PhysicsPlayerPush : MonoBehaviour
{
    [SerializeField] private NetworkObject networkObject;
    [SerializeField] private float pushForce = 12f;

    private void OnControllerColliderHit(ControllerColliderHit hit)
    {
        if (!networkObject.HasAuthority || hit.moveDirection.y < -0.3f) return;

        Rigidbody body = hit.collider.attachedRigidbody;
        if (body == null || body.isKinematic) return;

        Vector3 direction = new Vector3(hit.moveDirection.x, 0, hit.moveDirection.z);
        if (direction.sqrMagnitude < 0.0001f) return;

        body.AddForce(direction.normalized * pushForce, ForceMode.Force);
    }
}
