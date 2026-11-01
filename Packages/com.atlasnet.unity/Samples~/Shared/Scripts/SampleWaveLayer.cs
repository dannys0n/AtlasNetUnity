using UnityEngine;

/// <summary>Let locomotion own the arm except during the masked wave gesture.</summary>
[RequireComponent(typeof(Animator))]
public sealed class SampleWaveLayer : MonoBehaviour
{
    Animator animator;
    int layer;
    static readonly int WaveState = Animator.StringToHash("Wave overlay.Wave");
    void Awake() { animator=GetComponent<Animator>(); layer=animator.GetLayerIndex("Wave overlay"); }
    void Update()
    {
        if (layer<0) return;
        bool waving=animator.GetCurrentAnimatorStateInfo(layer).fullPathHash==WaveState;
        float weight=waving?1f:0f;
        if (animator.IsInTransition(layer))
        {
            bool nextWave=animator.GetNextAnimatorStateInfo(layer).fullPathHash==WaveState;
            float progress=Mathf.Clamp01(animator.GetAnimatorTransitionInfo(layer).normalizedTime);
            weight=Mathf.Lerp(waving?1f:0f,nextWave?1f:0f,progress);
        }
        animator.SetLayerWeight(layer,weight);
    }
}
