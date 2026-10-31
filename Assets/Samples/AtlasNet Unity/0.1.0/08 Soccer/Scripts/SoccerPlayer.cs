using System.Collections.Generic;
using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    // Owners send input only. One simulation worker moves the body and drives the Animator.
    public sealed class SoccerPlayer : NetworkBehaviour
    {
        public const float Speed = 16f;
        public const float Size = 1.6f;
        public Vector3 AttackingGoal => new Vector3(Team == 0 ? 52 : -52, 0, 0);
        public Vector2 MoveInput { get; private set; }
        public readonly NetworkVariable<int> TeamIndex = new(-1);
        public int Team => Mathf.Max(0, TeamIndex.Value);
        Renderer[] renderers;
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
            if (HasAuthority) TeamIndex.Value = RosterIndex(NetworkManager, OwnerSession) & 1;
        }
        Rigidbody body;
        Animator animator;
        NetworkAnimator networkAnimator;
        float inputReceived, nextInput;
        Vector2 lastSent;
        readonly Dictionary<ulong,float> kickedBalls = new();
        public override void OnNetworkSpawn()
        {
            body = GetComponent<Rigidbody>();
            animator = GetComponentInChildren<Animator>();
            networkAnimator = GetComponent<NetworkAnimator>();
            renderers = GetComponentsInChildren<Renderer>();
            indicator = GetComponent<SoccerNetworkIndicator>();
            TeamIndex.Changed += TeamChanged;
            ApplyAuthority();
            if (HasAuthority) TeamIndex.Value = RosterIndex(NetworkManager, OwnerSession) & 1;
            ApplyTeamColor();
        }
        public override void OnNetworkDespawn() => TeamIndex.Changed -= TeamChanged;
        void TeamChanged(int previous, int current) => ApplyTeamColor();
        void ApplyTeamColor()
        {
            if (renderers == null) return;
            var tint = new MaterialPropertyBlock();
            Color color = Team == 0 ? SoccerSession.Blue : SoccerSession.Orange;
            tint.SetColor("_BaseColor", color); tint.SetColor("_Color", color);
            foreach (var renderer in renderers)
                if (!indicator || renderer != indicator.bodyRenderer) renderer.SetPropertyBlock(tint);
            if (indicator) indicator.SetRestingColor(color);
        }
        public override void OnSimulationAuthorityChanged() { ApplyAuthority(); kickedBalls.Clear(); }
        void ApplyAuthority()
        {
            if (!body) return;
            body.isKinematic = !HasAuthority;
            // NetworkTransform interpolates clients. Rigidbody interpolation is for local simulation only.
            body.interpolation = HasAuthority ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
            body.detectCollisions = NetworkManager.IsServer;
            if (HasAuthority) { body.position = transform.position; body.linearVelocity = new Vector3(MoveInput.x, 0, MoveInput.y) * Speed; }
        }
        void Update()
        {
            if (!IsSpawned || !IsOwner) return;
            Vector2 input = SoccerStick.Input;
            if (input.sqrMagnitude < .001f) input = new Vector2(UnityEngine.Input.GetAxisRaw("Horizontal"), UnityEngine.Input.GetAxisRaw("Vertical"));
            input = Vector2.ClampMagnitude(input, 1);
            if (Time.unscaledTime >= nextInput || input != lastSent)
            {
                SetMoveRpc(input); lastSent = input; nextInput = Time.unscaledTime + 1f / NetworkManager.TickRate;
            }
        }
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner)]
        public void SetMoveRpc(Vector2 input)
        {
            if (!HasAuthority || !float.IsFinite(input.x) || !float.IsFinite(input.y)) return;
            MoveInput = Vector2.ClampMagnitude(input, 1); inputReceived = Time.unscaledTime;
        }
        public void Celebrate()
        {
            if (HasAuthority) networkAnimator.SetTrigger("Wave");
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
            if (!IsSpawned || !HasAuthority || MoveInput.sqrMagnitude < .01f) return;
            if (!collision.collider.TryGetComponent(out SoccerBall ball) || !ball.IsSpawned || ball.NetworkManager != NetworkManager) return;
            Vector3 toward = ball.transform.position - body.position; toward.y = 0;
            if (Vector3.Dot(new Vector3(MoveInput.x,0,MoveInput.y), toward.normalized) <= .05f) return;
            ulong id=ball.NetworkObject.EntityId.Value;
            if (!kickedBalls.TryGetValue(id,out float nextLift))
            {
                kickedBalls[id]=Time.unscaledTime+.4f;
                ball.ContactFrom(NetworkObject,body.position);
            }
            else if(Time.unscaledTime>=nextLift)
            {
                kickedBalls[id]=Time.unscaledTime+.4f;
                ball.ContactFrom(NetworkObject,body.position,true);
            }
        }
        void FixedUpdate()
        {
            if (!IsSpawned || !HasAuthority) return;
            if (Time.unscaledTime - inputReceived > .25f) MoveInput = Vector2.zero;
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
