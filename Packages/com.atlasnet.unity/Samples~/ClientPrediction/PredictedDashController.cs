using UnityEngine;

namespace AtlasNet.Samples
{
    [RequireComponent(typeof(CharacterController))]
    public sealed class PredictedDashController : PredictedBehaviour<PredictedDashController.Input, PredictedDashController.State>
    {
        public struct Input : IPredictionData
        {
            public Vector2 Move;
            public bool Dash;

            public void Serialize(PredictionSerializer serializer)
            {
                serializer.Serialize(ref Move);
                serializer.Serialize(ref Dash);
            }
        }

        public struct State : IPredictionData
        {
            public float VerticalSpeed;
            public float DashCooldown;
            public bool Grounded;

            public void Serialize(PredictionSerializer serializer)
            {
                serializer.Serialize(ref VerticalSpeed);
                serializer.Serialize(ref DashCooldown);
                serializer.Serialize(ref Grounded);
            }
        }

        [Header("Dash Movement")]
        [SerializeField, Min(0)] private float moveSpeed = 5f;
        [SerializeField, Min(0)] private float dashDistance = 2.5f;
        [SerializeField, Min(0)] private float dashCooldown = .8f;

        private CharacterController capsule;
        private Input input;
        private State state;

        public float CooldownRemaining => state.DashCooldown;

        public void SetInput(Vector2 move, bool dashPressed)
        {
            if (!IsOwner) return;
            input.Move = move;
            input.Dash |= dashPressed;
        }

        protected override void OnPredictionSpawn() => capsule = GetComponent<CharacterController>();

        protected override void OnPredictionDespawn()
        {
            input = default;
            state = default;
        }

        protected override Input ReadInput()
        {
            var next = input;
            input.Dash = false;
            return next;
        }

        protected override Input SanitizeInput(Input next)
        {
            if (!float.IsFinite(next.Move.x) || !float.IsFinite(next.Move.y)) next.Move = default;
            next.Move = Vector2.ClampMagnitude(new Vector2(
                Mathf.Clamp(next.Move.x, -1, 1), Mathf.Clamp(next.Move.y, -1, 1)), 1);
            return next;
        }

        protected override void SimulateTick(Input next, float deltaTime)
        {
            state.DashCooldown = Mathf.Max(0, state.DashCooldown - deltaTime);
            var direction = new Vector3(next.Move.x, 0, next.Move.y);
            Vector3 movement = direction * (moveSpeed * deltaTime);

            if (next.Dash && state.DashCooldown == 0)
            {
                movement += (direction == Vector3.zero ? transform.forward : direction.normalized) * dashDistance;
                state.DashCooldown = dashCooldown;
            }

            if (state.Grounded && state.VerticalSpeed < 0) state.VerticalSpeed = -2f;
            state.VerticalSpeed -= 24f * deltaTime;
            movement.y = state.VerticalSpeed * deltaTime;
            state.Grounded = (capsule.Move(movement) & CollisionFlags.Below) != 0;
        }

        protected override State CaptureReconcileState() => state;
        protected override void ApplyReconcileState(State confirmed) => state = confirmed;
    }
}
