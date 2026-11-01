using System.Collections.Generic;
using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Movement follows the NetworkTransform writer: server input simulation or immediate owner simulation.
    public sealed class SoccerPlayer : NetworkBehaviour
    {
        [SerializeField] bool allowMovement = true;
        public bool AllowMovement => allowMovement;
        [SerializeField] bool colorByOwnership;
        SessionId colorViewer;
        bool hasColorViewer;
        public void SetColorViewer(SessionId viewer)
        {
            colorViewer = viewer; hasColorViewer = true; ApplyTeamColor();
        }
        public const float Speed = 16f;
        public const float Size = 1.6f * 4f / 3f;
        public Vector3 AttackingGoal => new Vector3(Team == 0 ? 52 : -52, 0, 0);
        public Vector2 MoveInput { get; private set; }
        public readonly NetworkVariable<int> TeamIndex = new(-1);
        public int Team => Mathf.Max(0, TeamIndex.Value);
        Renderer[] renderers;
        SampleWaveLayer wave;
        SoccerNetworkIndicator indicator;
        public static int RosterIndex(NetworkManager manager, SessionId owner)
        {
            int index = 0;
            foreach (var session in manager.ConnectedClientsIds)
                if (session.Value < owner.Value) index++;
            return index;
        }
        public override void OnNetworkTick()
        {
            // The admitted roster is shared with workers. A departure rebalances both sides.
            if (HasAuthority && !colorByOwnership) TeamIndex.Value = RosterIndex(NetworkManager, OwnerSession) & 1;
        }
        Rigidbody body;
        bool ownerSimulation, wasSimulating;
        bool SimulatesMovement => ownerSimulation ? IsOwner : HasAuthority;
        Animator animator;
        NetworkAnimator networkAnimator;
        bool OwnerAnimation => networkAnimator.Writer == AnimatorWriter.Owner;
        float inputReceived, nextInput;
        // Applications may reserve arrow keys for menus/presentation without changing network movement.
        public bool WaveKeyEnabled { get; set; } = true;
        public System.Func<Vector2> KeyboardInput { private get; set; }
        Vector2 lastSent;
        readonly Dictionary<ulong,float> kickedBalls = new();
        public override void OnNetworkSpawn()
        {
            body = GetComponent<Rigidbody>();
            ownerSimulation = GetComponent<NetworkTransform>().PositionWriter == TransformWriter.Owner;
            animator = GetComponentInChildren<Animator>();
            networkAnimator = GetComponent<NetworkAnimator>();
            wave = GetComponent<SampleWaveLayer>();
            renderers = GetComponentsInChildren<Renderer>();
            indicator = GetComponent<SoccerNetworkIndicator>();
            TeamIndex.Changed += TeamChanged;
            ApplyAuthority();
            if (HasAuthority && !colorByOwnership) TeamIndex.Value = RosterIndex(NetworkManager, OwnerSession) & 1;
            ApplyTeamColor();
        }
        public override void OnNetworkDespawn() => TeamIndex.Changed -= TeamChanged;
        void TeamChanged(int previous, int current) => ApplyTeamColor();
        void ApplyTeamColor()
        {
            if (renderers == null) return;
            var tint = new MaterialPropertyBlock();
            bool localOwner = hasColorViewer ? colorViewer.Value != 0 && OwnerSession == colorViewer : IsOwner;
            Color color = colorByOwnership
                ? (localOwner ? SoccerSession.Orange : SoccerSession.Blue)
                : (Team == 0 ? SoccerSession.Blue : SoccerSession.Orange);
            tint.SetColor("_BaseColor", color); tint.SetColor("_Color", color);
            foreach (var renderer in renderers)
                if (!indicator || renderer != indicator.bodyRenderer) renderer.SetPropertyBlock(tint);
            if (indicator) indicator.SetRestingColor(color);
        }
        public override void OnSimulationAuthorityChanged() { ApplyAuthority(); kickedBalls.Clear(); }
        void ApplyAuthority()
        {
            if (!body) return;
            bool simulate = SimulatesMovement;
            body.isKinematic = !simulate;
            // Local physics interpolation is independent of the network snapshot buffer.
            body.interpolation = simulate ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            body.detectCollisions = allowMovement && (NetworkManager.IsServer || simulate);
            if (!allowMovement) body.constraints = RigidbodyConstraints.FreezeAll;
            // Logical worker changes must not reset a still-controlling owner's body.
            if (simulate && (!ownerSimulation || !wasSimulating))
            { body.position = transform.position; body.linearVelocity = new Vector3(MoveInput.x, 0, MoveInput.y) * Speed; }
            wasSimulating = simulate;
        }
        void Update()
        {
            if (!IsSpawned) return;
            if (!IsOwner) return;
            if (WaveKeyEnabled && UnityEngine.Input.GetKeyDown(KeyCode.Space)) RequestWave();
            if (!allowMovement) return;
            Vector2 input = SoccerStick.Input;
            if (input.sqrMagnitude < .001f) input = KeyboardInput != null ? KeyboardInput() : new Vector2(UnityEngine.Input.GetAxisRaw("Horizontal"), UnityEngine.Input.GetAxisRaw("Vertical"));
            input = Vector2.ClampMagnitude(input, 1);
            if (ownerSimulation)
            {
                MoveInput = input; inputReceived = Time.unscaledTime;
            }
            else if (Time.unscaledTime >= nextInput || input != lastSent)
            {
                SetMoveRpc(input); lastSent = input; nextInput = Time.unscaledTime + 1f / NetworkManager.TickRate;
            }
        }
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
        public void SetMoveRpc(Vector2 input)
        {
            if (ownerSimulation || !HasAuthority || !float.IsFinite(input.x) || !float.IsFinite(input.y)) return;
            MoveInput = allowMovement ? Vector2.ClampMagnitude(input, 1) : Vector2.zero; inputReceived = Time.unscaledTime;
        }
        public void RequestWave()
        {
            if (!IsSpawned || !IsOwner || wave.IsBusy) return;
            if (OwnerAnimation) wave.TryWave(networkAnimator); else RequestWaveRpc();
        }
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
        public void RequestWaveRpc() { Celebrate(); }

        public void Celebrate()
        {
            if (OwnerAnimation)
            {
                if (HasAuthority) CelebrateOwnerRpc();
            }
            else if (HasAuthority) wave.TryWave(networkAnimator);
        }
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        public void CelebrateOwnerRpc()
        {
            if (OwnerAnimation && IsOwner) wave.TryWave(networkAnimator);
        }
        void OnCollisionEnter(Collision collision) => TryKick(collision);
        void OnCollisionStay(Collision collision) => TryKick(collision);
        void OnCollisionExit(Collision collision)
        {
            if (collision.collider.TryGetComponent(out SoccerBall ball) && ball.IsSpawned)
                kickedBalls.Remove(ball.NetworkObject.EntityId.Value);
        }
        void TryKick(Collision collision)
        {
            if (!IsSpawned || !SimulatesMovement || MoveInput.sqrMagnitude < .01f) return;
            if (!collision.collider.TryGetComponent(out SoccerBall ball) || !ball.IsSpawned || ball.NetworkManager != NetworkManager) return;
            Vector3 toward = ball.transform.position - body.position; toward.y = 0;
            if (Vector3.Dot(new Vector3(MoveInput.x,0,MoveInput.y), toward.normalized) <= .05f) return;
            ulong id=ball.NetworkObject.EntityId.Value;
            if (!kickedBalls.TryGetValue(id,out float nextLift))
            {
                kickedBalls[id]=Time.unscaledTime+.4f;
                Kick(ball, false);
            }
            else if(Time.unscaledTime>=nextLift)
            {
                kickedBalls[id]=Time.unscaledTime+.4f;
                Kick(ball, true);
            }
        }
        void Kick(SoccerBall ball, bool liftOnly)
        {
            if (ownerSimulation) RequestKickRpc(ball.NetworkObject.EntityId.Value, body.position, liftOnly);
            else ball.ContactFrom(NetworkObject, body.position, liftOnly);
        }
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
        public void RequestKickRpc(ulong ballId, Vector3 position, bool liftOnly)
        {
            if (!ownerSimulation || !HasAuthority || !float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return;
            if ((position - transform.position).sqrMagnitude > 16) return;
            if (NetworkManager.TryGet(new EntityId(ballId), out var target) && target.TryGetComponent(out SoccerBall ball))
                ball.ContactFrom(NetworkObject, position, liftOnly);
        }
        void FixedUpdate()
        {
            if (!IsSpawned || !SimulatesMovement) return;
            if (Time.unscaledTime - inputReceived > .25f) MoveInput = Vector2.zero;
            if (!allowMovement) MoveInput = Vector2.zero;
            Vector3 desired = new Vector3(MoveInput.x, 0, MoveInput.y) * Speed;
            Vector3 pos = body.position;
            if ((pos.x <= -50.5f && desired.x < 0) || (pos.x >= 50.5f && desired.x > 0)) desired.x = 0;
            if ((pos.z <= -32.3f && desired.z < 0) || (pos.z >= 32.3f && desired.z > 0)) desired.z = 0;
            body.linearVelocity = desired;
            if (desired.sqrMagnitude > .001f) body.MoveRotation(Quaternion.LookRotation(desired));
            animator.SetFloat("Speed", MoveInput.magnitude);
        }
        protected override void WriteHandoffState(NetWriter writer)
        {
            writer.Write(MoveInput.x); writer.Write(MoveInput.y);
            writer.Write(Mathf.Max(0, .25f - (Time.unscaledTime - inputReceived)));
        }
        protected override void ReadHandoffState(NetReader reader)
        {
            MoveInput = new Vector2(reader.ReadFloat(), reader.ReadFloat());
            inputReceived = Time.unscaledTime - .25f + reader.ReadFloat();
        }
    }
}
