using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;

namespace AtlasNet.M2
{
    public sealed class M2Actor : NetworkBehaviour
    {
        SoccerPlayer player;
        public override void OnNetworkSpawn() { player=GetComponent<SoccerPlayer>(); DontDestroyOnLoad(gameObject); }
        void Update()
        {
            if (player && M2Presentation.Instance) player.enabled=!M2Presentation.Instance.Loading;
            if (IsOwner && M2Presentation.Instance && M2Presentation.Instance.ActiveIndex == 1 && Input.GetKeyDown(KeyCode.Space)) WaveRpc();
        }
        public void Wave() => WaveRpc();
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
        public void WaveRpc()
        {
            if (HasAuthority && TryGetComponent(out SoccerPlayer player)) player.Celebrate();
        }
    }
}
