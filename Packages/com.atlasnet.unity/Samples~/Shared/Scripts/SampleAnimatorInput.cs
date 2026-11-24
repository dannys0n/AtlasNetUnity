using AtlasNet;
using UnityEngine;

/// <summary>Only the owner supplies Animator input. NetworkAnimator replicates parameters and triggers.</summary>
public sealed class SampleAnimatorInput : NetworkBehaviour
{
    [SerializeField] private Animator animator;
    [SerializeField] private NetworkAnimator networkAnimator;
    [SerializeField] private SamplePlanarMovement movement;
    SampleWaveLayer wave;

    public override void OnNetworkSpawn()
    {
        wave=GetComponent<SampleWaveLayer>();
        var viewer=NetworkManager;
        bool localOwner=viewer.IsClient && viewer.LocalSession==OwnerSession;
        var tint=new MaterialPropertyBlock();
        Color color=localOwner?new Color(1f,.4f,.07f):new Color(.12f,.48f,1f);
        tint.SetColor("_Color",color);tint.SetColor("_BaseColor",color);
        foreach(var renderer in GetComponentsInChildren<Renderer>())renderer.SetPropertyBlock(tint);
    }

    private void LateUpdate()
    {
        if (!IsSpawned || !IsOwner) return;
        animator.SetFloat("Speed", movement.MoveAmount);
        if (Input.GetKeyDown(KeyCode.Space)) Wave();
    }

    public void Wave()
    {
        if (IsSpawned && IsOwner) wave.TryWave(networkAnimator);
    }
}
