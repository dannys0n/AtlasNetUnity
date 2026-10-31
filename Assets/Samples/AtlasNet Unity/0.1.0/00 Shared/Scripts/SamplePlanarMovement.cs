using AtlasNet;
using UnityEngine;

/// <summary>Minimal owner-controlled movement on the flat sample floor.</summary>
public sealed class SamplePlanarMovement : NetworkBehaviour
{
    [SerializeField] private float speed = 4f;
    public float MoveAmount { get; private set; }

    private void Update()
    {
        if (!IsSpawned || !IsOwner) return;
        Vector2 input = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1f);
        MoveAmount = input.magnitude;
        Vector3 direction = new Vector3(input.x, 0, input.y);
        transform.position += direction * (speed * Time.deltaTime);
        if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
    }
}
