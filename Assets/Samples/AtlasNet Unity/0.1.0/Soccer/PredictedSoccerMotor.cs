using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    [RequireComponent(typeof(CharacterController), typeof(SoccerPlayer))]
    public sealed class PredictedSoccerMotor : PredictedBehaviour<PredictedSoccerMotor.Input, PredictedSoccerMotor.State>
    {
        public struct Input : IPredictionData
        {
            public Vector2 Move;
            public void Serialize(PredictionSerializer serializer) => serializer.Serialize(ref Move);
        }

        // The motor is planar and has no velocity to restore. AtlasNet snapshots its pose.
        public struct State : IPredictionData
        {
            public void Serialize(PredictionSerializer serializer) { }
        }

        private CharacterController capsule;
        private SoccerPlayer player;
        private Vector2 moveInput;
        private SoccerBall touchedBall, previousBall;

        public void SetInput(Vector2 move)
        {
            if (IsOwner) moveInput = move;
        }

        protected override void OnPredictionSpawn()
        {
            capsule = GetComponent<CharacterController>();
            player = GetComponent<SoccerPlayer>();
        }

        protected override void OnPredictionDespawn()
        {
            moveInput = default;
            touchedBall = previousBall = null;
        }

        protected override Input GetInputForTick() => new() { Move = moveInput };

        protected override Input SanitizeInput(Input input)
        {
            if (!float.IsFinite(input.Move.x) || !float.IsFinite(input.Move.y)) return default;
            input.Move = Vector2.ClampMagnitude(new Vector2(
                Mathf.Clamp(input.Move.x, -1f, 1f), Mathf.Clamp(input.Move.y, -1f, 1f)), 1f);
            return input;
        }

        protected override void Simulate(Input input, float deltaTime)
        {
            Vector2 move = player.AllowMovement ? input.Move : Vector2.zero;
            Vector3 position = transform.position;
            Vector3 target = position + new Vector3(move.x, 0f, move.y) * (SoccerPlayer.Speed * deltaTime);
            target.x = Mathf.Clamp(target.x, -50.5f, 50.5f);
            target.z = Mathf.Clamp(target.z, -32.3f, 32.3f);
            Vector3 displacement = target - position;

            touchedBall = null;
            if (displacement.sqrMagnitude > 0.000001f)
            {
                transform.rotation = Quaternion.LookRotation(displacement);
                capsule.Move(displacement);
            }

            if (!IsReplaying) player.SetPredictedMovement(move);
            if (!HasAuthority || IsReplaying) return;
            if (previousBall != touchedBall && previousBall != null) player.EndBallContact(previousBall);
            if (touchedBall != null) player.TryKickBall(touchedBall, transform.position, move);
            previousBall = touchedBall;
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            if (!HasAuthority || IsReplaying || !hit.collider.TryGetComponent<SoccerBall>(out var ball)) return;
            touchedBall = ball;
        }

        protected override State CaptureState() => default;
        protected override void RestoreState(State state) { }
    }
}
