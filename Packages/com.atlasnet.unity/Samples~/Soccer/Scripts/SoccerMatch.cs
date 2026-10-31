using AtlasNet;

namespace AtlasNet.Samples.Soccer
{
    public sealed class SoccerMatch : NetworkBehaviour
    {
        public readonly NetworkVariable<int> BlueScore = new(0);
        public readonly NetworkVariable<int> OrangeScore = new(0);
        public readonly NetworkVariable<int> ScoringTeam = new(-1);
        public readonly NetworkVariable<int> GoalSequence = new(0);
        bool pendingCelebration;
        public override void OnNetworkSpawn() => GoalSequence.Changed += CelebrateGoal;
        public override void OnNetworkDespawn() => GoalSequence.Changed -= CelebrateGoal;
        public void RecordGoal(int team)
        {
            if (!HasAuthority || team < 0 || team > 1) return;
            if (team == 0) BlueScore.Value++; else OrangeScore.Value++;
            ScoringTeam.Value = team;
            GoalSequence.Value++;
        }
        void CelebrateGoal(int previous, int current)
        {
            pendingCelebration = true;
        }
        public override void OnNetworkTick()
        {
            if (!pendingCelebration) return;
            pendingCelebration = false;
            // Process after all snapshot fields, including ScoringTeam, have been applied.
            foreach (var obj in NetworkManager.SpawnedObjects)
                if (obj.HasAuthority && obj.TryGetComponent(out SoccerPlayer player) && player.Team == ScoringTeam.Value)
                    player.Celebrate();
        }
    }
}
