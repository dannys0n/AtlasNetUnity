using AtlasNet;
using UnityEngine;

/// <summary>Only the owner supplies Animator input. NetworkAnimator replicates parameters and triggers.</summary>
public sealed class SampleAnimatorInput : NetworkBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private NetworkAnimator networkAnimator;
    [SerializeField] private SamplePlanarMovement movement;

    private void LateUpdate()
    {
        if (!IsSpawned || !IsOwner) return;
        animator.SetFloat("Speed", movement.MoveAmount);
        if (Input.GetKeyDown(KeyCode.Space)) networkAnimator.SetTrigger("Wave");
    }

    public void Wave()
    {
        if (IsSpawned && IsOwner) networkAnimator.SetTrigger("Wave");
    }
}
