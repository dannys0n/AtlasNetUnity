using UnityEngine;

namespace AtlasNet
{
    /// <summary>Server-authoritative capsule movement with prediction for the controlling client.</summary>
    [AddComponentMenu("AtlasNet/Predicted Character Controller")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(CharacterController))]
    public sealed class PredictedCharacterController : PredictedBehaviour<PredictedCharacterController.Input, PredictedCharacterController.State>
    {
        [Header("Movement")]
        [SerializeField, Min(0)] private float walkSpeed = 5f;
        [SerializeField, Min(0)] private float sprintSpeed = 8f;
        [SerializeField, Min(0)] private float jumpHeight = 1.2f;
        [SerializeField, Min(.1f)] private float gravity = 24f;

        public struct Input : IPredictionData
        {
            public Vector2 Move;
            public float Yaw;
            public bool Sprint, Jump;

            public void Serialize(PredictionSerializer serializer)
            {
                serializer.Serialize(ref Move);
                serializer.Serialize(ref Yaw);
                byte buttons = (byte)((Sprint ? 1 : 0) | (Jump ? 2 : 0));
                serializer.Serialize(ref buttons);
                Sprint = (buttons & 1) != 0;
                Jump = (buttons & 2) != 0;
            }
        }

        public struct State : IPredictionData
        {
            public float VerticalSpeed;
            public bool Grounded;

            public void Serialize(PredictionSerializer serializer)
            {
                serializer.Serialize(ref VerticalSpeed);
                serializer.Serialize(ref Grounded);
            }
        }

        private CharacterController capsule;
        private State state;
        private Input currentInput;

        /// <summary>Call from the local input source. Yaw is an absolute world heading in degrees. Jump is a press.</summary>
        public void SetInput(Vector2 move, float yaw, bool sprint = false, bool jump = false)
        {
            if (!IsOwner) return;
            currentInput.Move = move;
            currentInput.Yaw = yaw;
            currentInput.Sprint = sprint;
            currentInput.Jump |= jump;
        }

        protected override void OnPredictionSpawn()
        {
            capsule = GetComponent<CharacterController>();
            currentInput.Yaw = transform.eulerAngles.y;
        }

        protected override void OnPredictionDespawn()
        {
            currentInput = default;
            state = default;
        }

        protected override Input GetInputForTick()
        {
            var input = currentInput;
            currentInput.Jump = false;
            return input;
        }

        protected override Input GetMissingInput() => new() { Yaw = transform.eulerAngles.y };

        protected override Input SanitizeInput(Input input)
        {
            if (!float.IsFinite(input.Move.x) || !float.IsFinite(input.Move.y)) input.Move = default;
            input.Move = Vector2.ClampMagnitude(new Vector2(
                Mathf.Clamp(input.Move.x, -1, 1), Mathf.Clamp(input.Move.y, -1, 1)), 1);
            input.Yaw = float.IsFinite(input.Yaw) ? input.Yaw % 360 : 0;
            return input;
        }

        protected override void Simulate(Input input, float deltaTime)
        {
            transform.rotation = Quaternion.Euler(0, input.Yaw, 0);
            if (state.Grounded && state.VerticalSpeed < 0) state.VerticalSpeed = -2;
            if (state.Grounded && input.Jump) state.VerticalSpeed = Mathf.Sqrt(2 * gravity * jumpHeight);
            state.VerticalSpeed -= gravity * deltaTime;

            var horizontal = transform.rotation * new Vector3(input.Move.x, 0, input.Move.y);
            var velocity = horizontal * (input.Sprint ? sprintSpeed : walkSpeed);
            velocity.y = state.VerticalSpeed;
            var collisions = capsule.Move(velocity * deltaTime);
            state.Grounded = (collisions & CollisionFlags.Below) != 0;
            if ((collisions & CollisionFlags.Above) != 0 && state.VerticalSpeed > 0) state.VerticalSpeed = 0;
        }

        protected override State CaptureState() => state;
        protected override void RestoreState(State snapshot) => state = snapshot;
    }
}
