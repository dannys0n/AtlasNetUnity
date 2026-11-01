using AtlasNet;
using AtlasNet.Samples.Soccer;
using UnityEngine;

namespace AtlasNet.M2
{
    public sealed class M2Actor : NetworkBehaviour
    {
        SoccerPlayer player;
        public override void OnNetworkSpawn()
        {
            player=GetComponent<SoccerPlayer>(); DontDestroyOnLoad(gameObject);
            // Embedded workers share this local presenter. Color relative to the
            // actual viewer, not to whichever replica currently simulates the actor.
            var p=M2Presentation.Instance;
            if (player) player.KeyboardInput = ReadMovement;
            if(player&&p)player.SetColorViewer(p.manager.IsClient?p.manager.LocalSession:default);
        }
        void Update()
        {
            if (player && M2Presentation.Instance) player.enabled=!M2Presentation.Instance.Loading;
        }
        static Vector2 ReadMovement() => new(
            (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0),
            (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0));
        public void Wave()
        {
            if (IsSpawned && IsOwner && player) player.RequestWave();
        }
    }
}
