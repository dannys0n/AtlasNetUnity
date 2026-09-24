using AtlasNet;
using UnityEngine;

/// <summary>Client writes its movement channel; NetworkTransform sends the changed transform on network ticks.</summary>
public sealed class SimpleClientMovement : NetworkBehaviour
{
    [SerializeField] private CharacterController controller;
    [SerializeField] private float speed = 5f;
    [SerializeField] private float jumpSpeed = 5f;
    private float verticalSpeed;

    private void Update()
    {
        if (!IsOwner) return;
        Vector2 input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        input = Vector2.ClampMagnitude(input, 1);
        if (controller.isGrounded && verticalSpeed < 0) verticalSpeed = -1f;
        if (controller.isGrounded && Input.GetButtonDown("Jump")) verticalSpeed = jumpSpeed;
        verticalSpeed += Physics.gravity.y * Time.deltaTime;
        Vector3 movement = (transform.right * input.x + transform.forward * input.y) * speed;
        movement.y = verticalSpeed;
        controller.Move(movement * Time.deltaTime);
    }
}
