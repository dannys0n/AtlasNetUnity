using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    public sealed class SoccerBall : NetworkBehaviour
    {
        public const float Radius = 1f;
        public const float GoalHeight = 5.4f;
        public const float GoalLineX = 51.7f;
        [SerializeField, Min(0)] float contactSpeed = 18f;
        [SerializeField, Min(0)] float maxSpeed = 28f;
        [SerializeField, Min(0)] float upwardSpeed = 4f;
        [SerializeField, Min(0)] float maxUpwardSpeed = 12f;
        Rigidbody body;
        double resetUntil;
        Vector3 pendingImpulse;
        SoccerMatch match;
        public override void OnNetworkSpawn() { body = GetComponent<Rigidbody>(); match = GetComponent<SoccerMatch>(); }
        // The player's simulation worker reports contact once. Only the ball's worker applies it.
        // This also works when the contacting player and ball straddle a worker boundary.
        public void ContactFrom(NetworkObject player, Vector3 playerPosition, bool liftOnly = false) =>
            SendAuthorityFrom(player, nameof(ContactRpc), player.EntityId.Value, playerPosition, liftOnly);
        readonly System.Collections.Generic.Dictionary<ulong, double> contacts = new();
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Server)]
        public void ContactRpc(ulong player, Vector3 playerPosition, bool liftOnly)
        {
            if (!HasAuthority || Time.timeAsDouble < resetUntil) return;
            if (!float.IsFinite(playerPosition.x) || !float.IsFinite(playerPosition.y) || !float.IsFinite(playerPosition.z)) return;
            if ((body.position - playerPosition).sqrMagnitude > 16) return;
            if (contacts.TryGetValue(player, out double previous) && Time.timeAsDouble - previous < .35) return;
            contacts[player] = Time.timeAsDouble;
            Vector3 direction = body.position - playerPosition; direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return;
            // Persistent contact only adds lift when horizontal motion is blocked.
            if(liftOnly && Vector3.ProjectOnPlane(body.linearVelocity,Vector3.up).sqrMagnitude>4f)return;
            Vector3 impulse=Vector3.up*upwardSpeed;
            if(!liftOnly)impulse+=direction.normalized*contactSpeed;
            pendingImpulse+=impulse;
        }
        void FixedUpdate()
        {
            if (!IsSpawned || !HasAuthority) return;
            if (Time.timeAsDouble < resetUntil) { body.linearVelocity = Vector3.zero; pendingImpulse=Vector3.zero; return; }
            Vector3 velocity=Vector3.ClampMagnitude(body.linearVelocity,maxSpeed);
            velocity.y=Mathf.Min(velocity.y,maxUpwardSpeed);body.linearVelocity=velocity;
            // Apply contacts once in the physics step, after the existing-velocity limits.
            // A velocity assignment must not overwrite an impulse queued by a collision/RPC.
            if(pendingImpulse.sqrMagnitude>0)
            {
                Vector3 next=Vector3.ClampMagnitude(body.linearVelocity+pendingImpulse,maxSpeed);
                next.y=Mathf.Min(next.y,maxUpwardSpeed);
                body.AddForce(next-body.linearVelocity,ForceMode.VelocityChange);pendingImpulse=Vector3.zero;
            }
            // Use the painted line: the solid goal face stops the ball before the old threshold.
            if (Mathf.Abs(body.position.x) < GoalLineX - Radius || Mathf.Abs(body.position.z) > 6f - Radius || body.position.y > GoalHeight - Radius) return;
            match.RecordGoal(body.position.x > 0 ? 0 : 1);
            body.position = new Vector3(0, Radius, 0);
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            resetUntil = Time.timeAsDouble + 2;pendingImpulse=Vector3.zero;
        }
        protected override void WriteHandoffState(NetWriter writer) { writer.Write((float)System.Math.Max(0, resetUntil - Time.timeAsDouble));writer.Write(pendingImpulse); }
        protected override void ReadHandoffState(NetReader reader) { resetUntil = Time.timeAsDouble + reader.ReadFloat();pendingImpulse=reader.ReadVector3(); }
    }
}
