using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Temporary local handoff feedback; normal materials and team colors remain unchanged.
    [RequireComponent(typeof(NetworkObject))]
    public sealed class SoccerNetworkIndicator : MonoBehaviour
    {
        public Renderer bodyRenderer;
        NetworkObject entity;
        MaterialPropertyBlock original, flash;
        bool captured, flashing, initialized;
        ulong previousWorker;
        uint previousEpoch;
        float greenUntil;
        HandoffDebugState previousState;
        Color shown;
        void Awake()=>entity=GetComponent<NetworkObject>();
        void LateUpdate()
        {
            if(!entity || !entity.IsSpawned || !bodyRenderer){Restore();return;}
            if(!captured){original=new MaterialPropertyBlock();bodyRenderer.GetPropertyBlock(original);captured=true;}
            ulong worker=entity.SimulationWorker;
            var state=entity.Manager.GetHandoffStateForDebug(entity);
            bool committed=initialized && worker!=previousWorker && entity.AuthorityEpoch!=previousEpoch;
            // New transitions replace the previous flash immediately, including an aborted departure.
            if(committed || (state!=previousState && state!=HandoffDebugState.None))greenUntil=0;
            if(committed && (!entity.Manager.IsServer || worker==entity.Manager.LocalWorkerId))greenUntil=Time.unscaledTime+.5f;
            initialized=true;previousWorker=worker;previousEpoch=entity.AuthorityEpoch;previousState=state;
            if(state==HandoffDebugState.Exiting)
            {greenUntil=0;Show(new Color(1f,.12f,.08f));}
            else if(Time.unscaledTime<greenUntil)Show(new Color(.15f,1f,.2f));
            else Restore();
        }
        public void SetRestingColor(Color color)
        {
            if (!bodyRenderer) return;
            original ??= new MaterialPropertyBlock();
            if (!captured) bodyRenderer.GetPropertyBlock(original);
            original.SetColor("_Color", color); original.SetColor("_BaseColor", color);
            captured = true;
            bodyRenderer.SetPropertyBlock(flashing ? flash : original);
        }
        void Show(Color color)
        {
            if(flashing && shown==color)return;
            flash??=new MaterialPropertyBlock();bodyRenderer.GetPropertyBlock(flash);
            flash.SetColor("_Color",color);flash.SetColor("_BaseColor",color);
            bodyRenderer.SetPropertyBlock(flash);shown=color;flashing=true;
        }
        void Restore()
        {
            if(!flashing)return;
            if(bodyRenderer && captured)bodyRenderer.SetPropertyBlock(original.isEmpty?null:original);
            flashing=false;
        }
        void OnDisable(){Restore();greenUntil=0;initialized=false;previousState=HandoffDebugState.None;}
    }
}
