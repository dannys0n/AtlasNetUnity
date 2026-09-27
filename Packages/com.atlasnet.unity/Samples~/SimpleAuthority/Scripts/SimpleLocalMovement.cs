using UnityEngine;

/// <summary>Single-player baseline for comparing the networked movement samples.</summary>
public sealed class SimpleLocalMovement : MonoBehaviour
{
    [SerializeField] private CharacterController controller;
    [SerializeField] private float speed = 5f;
    [SerializeField] private float jumpSpeed = 5f;
    private float verticalSpeed;

    private void Update()
    {
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
