using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Uses the same palette and handoff highlights as ScaleMotion, without adding motion or traffic.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SoccerNetworkIndicator : MonoBehaviour
    {
        public Renderer bodyRenderer;
        public Material authorityMaterial, ghostMaterial;
        MaterialPropertyBlock tint;
        Color shown;
        bool hasTint, initializedWorker;
        ulong previousWorker;
        float arrivalUntil;
        NetworkObject entity;
        void Awake() => entity=GetComponent<NetworkObject>();
        void LateUpdate() { if(entity && entity.IsSpawned) RefreshColor(); }
        void RefreshColor()
        {
            if (!bodyRenderer) return;
            ulong worker = entity.SimulationWorker;
            if (initializedWorker && previousWorker != worker && worker == entity.Manager.LocalWorkerId)
                arrivalUntil = Time.unscaledTime + .75f;
            initializedWorker = true; previousWorker = worker;
            var state = entity.Manager.GetHandoffStateForDebug(entity);
            if (state == HandoffDebugState.Entering) arrivalUntil = Time.unscaledTime + .75f;
            Color color;
            if (!entity.Manager.IsServer)
                color = Color.HSVToRGB((worker * .618034f + .55f) % 1f, .65f, .95f);
            else if (state == HandoffDebugState.Exiting) color = new Color(1f,.12f,.08f);
            else if (state == HandoffDebugState.Entering || (worker == entity.Manager.LocalWorkerId && Time.unscaledTime < arrivalUntil))
                color = new Color(.15f,1f,.2f);
            else color = entity.HasAuthority ? authorityMaterial.color : ghostMaterial.color;
            if (hasTint && color == shown) return;
            tint ??= new MaterialPropertyBlock(); tint.SetColor("_Color",color);tint.SetColor("_BaseColor",color);
            bodyRenderer.SetPropertyBlock(tint); shown=color;hasTint=true;
        }
    }
}
