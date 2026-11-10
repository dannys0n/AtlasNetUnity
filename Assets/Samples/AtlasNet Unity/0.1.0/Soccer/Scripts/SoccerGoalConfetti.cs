using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Local particles reuse the replicated goal event; individual confetti pieces are never networked.
    public sealed class SoccerGoalConfetti : MonoBehaviour
    {
        public NetworkManager manager;
        public ParticleSystem blueGoal;
        public ParticleSystem orangeGoal;

        public void Burst(int scoringTeam)
        {
            if (!manager || !manager.IsClient) return;
            var particles = scoringTeam == 0 ? blueGoal : scoringTeam == 1 ? orangeGoal : null;
            if (!particles) return;
            particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            particles.Play(true);
        }
    }
}
