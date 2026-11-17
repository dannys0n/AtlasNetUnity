using AtlasNet;
using UnityEngine;

namespace AtlasNet.Samples.Soccer
{
    public sealed class SoccerBall : NetworkBehaviour
    {
        public const float Radius = 1f;
        public const float GoalHeight = 5.4f;
        public const float GoalHalfWidth = 8f;
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
        internal float ContactRadius
        {
            get
            {
                var sphere = GetComponent<SphereCollider>();
                Vector3 scale = sphere.transform.lossyScale;
                return sphere.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Max(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            }
        }

        internal static bool TryGetContactCapsule(CapsuleCollider capsule, out Vector3 center,
            out float halfSegment, out float radius)
        {
            center = capsule.transform.TransformPoint(capsule.center);
            Vector3 scale = capsule.transform.lossyScale;
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            halfSegment = Mathf.Max(0, capsule.height * Mathf.Abs(scale.y) * .5f - radius);
            return capsule.direction == 1;
        }

        internal bool IsWithinContactReach(Vector3 center, float halfSegment, float radius, float tolerance)
        {
            Vector3 ballPosition = transform.position;
            center.y = Mathf.Clamp(ballPosition.y, center.y - halfSegment, center.y + halfSegment);
            float reach = ContactRadius + radius + tolerance;
            return (ballPosition - center).sqrMagnitude <= reach * reach;
        }

        internal bool TryRemoteContact(ulong player, Vector3 playerPosition, Vector2 move,
            Vector3 capsuleCenter, float capsuleHalfSegment, float capsuleRadius)
        {
            if (!HasAuthority || move.sqrMagnitude < .01f) return false;
            Vector3 direction = body.position - playerPosition;
            direction.y = 0;
            Vector3 heading = new Vector3(move.x, 0, move.y).normalized;
            if (Vector3.Dot(heading, direction.normalized) <= .05f) return false;
            // Allow one short advance along the authoritative movement direction.
            // A larger allowance would let players kick before reaching the ball.
            float advance = Mathf.Clamp(Vector3.Dot(body.position - capsuleCenter, heading), 0, .45f);
            if (!IsWithinContactReach(capsuleCenter + heading * advance, capsuleHalfSegment,
                    capsuleRadius, .25f)) return false;
            return ApplyContact(player, playerPosition, false);
        }

        readonly System.Collections.Generic.Dictionary<ulong, double> contacts = new();
        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Server)]
        public void ContactRpc(ulong player, Vector3 playerPosition, bool liftOnly) =>
            ApplyContact(player, playerPosition, liftOnly);

        private bool ApplyContact(ulong player, Vector3 playerPosition, bool liftOnly)
        {
            if (!HasAuthority || Time.timeAsDouble < resetUntil) return false;
            if (!float.IsFinite(playerPosition.x) || !float.IsFinite(playerPosition.y) || !float.IsFinite(playerPosition.z)) return false;
            float maxReach = Mathf.Max(4f, ContactRadius + 2f);
            if ((body.position - playerPosition).sqrMagnitude > maxReach * maxReach) return false;
            if (contacts.TryGetValue(player, out double previous) && Time.timeAsDouble - previous < .35) return false;
            contacts[player] = Time.timeAsDouble;
            Vector3 direction = body.position - playerPosition; direction.y = 0;
            if (direction.sqrMagnitude < .0001f) return false;
            // Persistent contact only adds lift when horizontal motion is blocked.
            if(liftOnly && Vector3.ProjectOnPlane(body.linearVelocity,Vector3.up).sqrMagnitude>4f)return false;
            Vector3 impulse=Vector3.up*upwardSpeed;
            if(!liftOnly)impulse+=direction.normalized*contactSpeed;
            pendingImpulse+=impulse;
            return true;
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
            if (Mathf.Abs(body.position.x) < GoalLineX - Radius || Mathf.Abs(body.position.z) > GoalHalfWidth - Radius || body.position.y > GoalHeight - Radius) return;
            match.RecordGoal(body.position.x > 0 ? 0 : 1);
            body.position = new Vector3(0, ContactRadius, 0);
            body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
            resetUntil = Time.timeAsDouble + 2;pendingImpulse=Vector3.zero;
        }
        protected override void WriteHandoffState(NetWriter writer) { writer.Write((float)System.Math.Max(0, resetUntil - Time.timeAsDouble));writer.Write(pendingImpulse); }
        protected override void ReadHandoffState(NetReader reader) { resetUntil = Time.timeAsDouble + reader.ReadFloat();pendingImpulse=reader.ReadVector3(); }
    }
}
