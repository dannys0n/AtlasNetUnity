using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AtlasNet
{
    /// <summary>Simulates 3D physics on the NetworkTransform writer; other copies are kinematic.</summary>
    [AddComponentMenu("AtlasNet/Network Rigidbody")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject), typeof(Rigidbody), typeof(NetworkTransform))]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        private Rigidbody body;
        private TransformWriter writer;
        private bool initialized, simulating;
        private bool initialKinematic;
        private RigidbodyInterpolation initialInterpolation;
        [SerializeField, Min(0), Tooltip("Maximum requested push speed across server workers, in units per second.")]
        private float maxRemotePushSpeed = 3f;
        [SerializeField, Min(0), Tooltip("Maximum force contributed by an individual push across server workers.")]
        private float maxRemotePushForce = 30f;
        private readonly Dictionary<NetworkRigidbody, (uint Epoch, Vector3 Velocity)> pushes = new();
        private readonly List<(Collider Local, Collider Remote)> ignoredPushers = new();
        private readonly List<Collider> bodyColliders = new();
        internal bool ServerPushBody => body != null && writer == TransformWriter.Server && !initialKinematic;
        internal float ContactRadius { get; private set; }
        private Vector3 handoffVelocity;
        private Vector3 handoffAngularVelocity;

        public override void OnNetworkSpawn()
        {
            body = GetComponent<Rigidbody>();
            var networkTransform = GetComponent<NetworkTransform>();
            if (body == null || networkTransform == null)
                throw new InvalidOperationException("NetworkRigidbody requires Rigidbody and NetworkTransform on the same GameObject");
            if (!networkTransform.SyncPosition || !networkTransform.SyncRotation ||
                networkTransform.PositionWriter != networkTransform.RotationWriter ||
                networkTransform.Target != transform)
                throw new InvalidOperationException("NetworkRigidbody requires NetworkTransform to synchronize this object's position and rotation with matching writers");

            writer = networkTransform.PositionWriter;
            initialKinematic = body.isKinematic;
            initialInterpolation = body.interpolation;
            GetComponentsInChildren(false, bodyColliders);
            foreach (var collider in bodyColliders)
                if (collider.enabled && !collider.isTrigger && collider.attachedRigidbody == body)
                    ContactRadius = Mathf.Max(ContactRadius, (collider.bounds.center - transform.position).magnitude + collider.bounds.extents.magnitude);
            ApplyAuthority();
        }

        public override void OnSimulationAuthorityChanged() => ApplyAuthority();

        private void ApplyAuthority()
        {
            if (body == null) return;
            bool simulate = writer == TransformWriter.Owner ? IsOwner : HasAuthority;
            body.isKinematic = !simulate || initialKinematic;
            body.interpolation = simulate ? initialInterpolation : RigidbodyInterpolation.None;
            if (simulate && !body.isKinematic && writer == TransformWriter.Server && (!initialized || !simulating))
            {
                body.linearVelocity = handoffVelocity;
                body.angularVelocity = handoffAngularVelocity;
            }
            // Drop buffered interactions across an authority transition.
            pushes.Clear(); receivedPushes.Clear();
            // A server-worker handoff must never reset the still-controlling client's velocity.
            simulating = simulate;
            initialized = true;
        }

        private void OnCollisionEnter(Collision collision) => CollectPush(collision);
        private void OnCollisionStay(Collision collision) => CollectPush(collision);
        private void OnCollisionExit(Collision collision)
        {
            if (TryRemotePushTarget(collision, out var target))
                pushes[target] = (target.NetworkObject.AuthorityEpoch, Vector3.zero);
        }

        private bool TryRemotePushTarget(Collision collision, out NetworkRigidbody target)
        {
            target = null;
            return IsSpawned && ServerPushBody && HasAuthority && OwnerSession.Value != 0 &&
                collision.rigidbody != null && collision.rigidbody.TryGetComponent(out target) &&
                target.IsSpawned && target.NetworkManager == NetworkManager && target.ServerPushBody &&
                !target.HasAuthority && target.OwnerSession.Value == 0;
        }

        private void CollectPush(Collision collision)
        {
            if (!TryRemotePushTarget(collision, out var target)) return;
            // Ask for motion into the contact, never forward a solver correction.
            // Grounded pushes follow the gravity plane so overlap cannot turn a
            // walking player's request into an artificial upward launch.
            Vector3 desired = Vector3.zero;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 direction = -collision.GetContact(i).normal;
                if (Physics.gravity.sqrMagnitude > .0001f)
                    direction = Vector3.ProjectOnPlane(direction, Physics.gravity);
                if (direction.sqrMagnitude < .0001f) continue;
                direction.Normalize();
                float closing = -Vector3.Dot(collision.relativeVelocity, direction);
                if (closing <= 0) continue;
                // Convert relative closing speed to a world-space target speed.
                float speed = Mathf.Clamp(closing + Vector3.Dot(collision.rigidbody.linearVelocity, direction), 0, maxRemotePushSpeed);
                if (speed * speed > desired.sqrMagnitude) desired = direction * speed;
            }
            pushes[target] = (target.NetworkObject.AuthorityEpoch, desired);
        }

        public override void OnNetworkTick()
        {
            foreach (var pair in pushes)
                if (HasAuthority && pair.Key != null && pair.Key.IsSpawned && !pair.Key.HasAuthority &&
                    pair.Key.NetworkObject.AuthorityEpoch == pair.Value.Epoch)
                    pair.Key.SendAuthorityFrom(NetworkObject, nameof(ReceiveRemotePushRpc),
                        NetworkObject.EntityId.Value, NetworkObject.AuthorityEpoch, pair.Value.Epoch, pair.Value.Velocity);
            pushes.Clear();
        }

        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Server)]
        private void ReceiveRemotePushRpc(ulong sourceValue, uint sourceEpoch, uint targetEpoch, Vector3 velocity)
        {
            var sourceId = new EntityId(sourceValue);
            if (!HasAuthority || !ServerPushBody || OwnerSession.Value != 0 ||
                NetworkObject.AuthorityEpoch != targetEpoch || !Finite(velocity) ||
                !NetworkManager.TryGet(sourceId, out var source) || source.AuthorityEpoch != sourceEpoch ||
                source.SimulationWorker == NetworkObject.SimulationWorker || source.OwnerSession.Value == 0 ||
                !source.TryGetComponent<NetworkRigidbody>(out var pusher) || !pusher.ServerPushBody) return;
            float reach = ContactRadius + pusher.ContactRadius + 1f;
            if ((source.transform.position - transform.position).sqrMagnitude > reach * reach) return;
            if (Physics.gravity.sqrMagnitude > .0001f) velocity = Vector3.ProjectOnPlane(velocity, Physics.gravity);
            velocity = Vector3.ClampMagnitude(velocity, maxRemotePushSpeed);
            if (velocity.sqrMagnitude < .000001f) { receivedPushes.Remove(sourceId); return; }
            // Latest intent replaces previous intent, including duplicate packets.
            receivedPushes[sourceId] = (sourceEpoch, velocity, Time.time + Mathf.Max(.1f, 2f / NetworkManager.TickRate));
        }
        private readonly Dictionary<EntityId, (uint Epoch, Vector3 Velocity, float Until)> receivedPushes = new();
        private readonly List<EntityId> expiredPushes = new();
        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        private void FixedUpdate()
        {
            if (!IsSpawned || !ServerPushBody || !HasAuthority || OwnerSession.Value != 0)
            { RestoreIgnoredPushers(); receivedPushes.Clear(); return; }
            for (int i = ignoredPushers.Count - 1; i >= 0; i--)
            {
                var pair = ignoredPushers[i];
                var remoteBody = pair.Remote != null ? pair.Remote.attachedRigidbody : null;
                if (pair.Local != null && remoteBody != null &&
                    remoteBody.TryGetComponent<NetworkRigidbody>(out var pusher) && pusher.IsSpawned &&
                    pusher.ServerPushBody && pusher.OwnerSession.Value != 0 &&
                    pusher.NetworkObject.SimulationWorker != NetworkObject.SimulationWorker) continue;
                if (SamePhysicsWorld(pair.Local, pair.Remote)) Physics.IgnoreCollision(pair.Local, pair.Remote, false);
                ignoredPushers.RemoveAt(i);
            }
            // Ignore just the remote player proxy on the box's authority. Its own
            // worker detects the contact and sends the one bounded impulse instead.
            foreach (var obj in NetworkManager.SpawnedObjects)
                if (obj.OwnerSession.Value != 0 && obj.SimulationWorker != NetworkObject.SimulationWorker &&
                    obj.TryGetComponent<NetworkRigidbody>(out var pusher) && pusher.ServerPushBody)
                    foreach (var local in bodyColliders)
                        foreach (var remote in pusher.bodyColliders)
                            if (SamePhysicsWorld(local, remote) && local.attachedRigidbody == body &&
                                remote.attachedRigidbody == pusher.body && !Physics.GetIgnoreCollision(local, remote))
                            { Physics.IgnoreCollision(local, remote, true); ignoredPushers.Add((local, remote)); }
            Vector3 force = Vector3.zero;
            float requestedSpeed = 0;
            expiredPushes.Clear();
            foreach (var push in receivedPushes)
            {
                if (Time.time > push.Value.Until || !NetworkManager.TryGet(push.Key, out var source) || !source.IsSpawned ||
                    source.AuthorityEpoch != push.Value.Epoch || source.SimulationWorker == NetworkObject.SimulationWorker)
                { expiredPushes.Add(push.Key); continue; }
                float speed = push.Value.Velocity.magnitude;
                Vector3 direction = push.Value.Velocity / speed;
                float error = speed - Vector3.Dot(body.linearVelocity, direction);
                if (error <= 0) continue;
                force += direction * Mathf.Min(maxRemotePushForce, body.mass * error / .15f);
                requestedSpeed = Mathf.Max(requestedSpeed, speed);
            }
            foreach (var id in expiredPushes) receivedPushes.Remove(id);
            if (force.sqrMagnitude > .000001f)
            {
                // Bound only the added push along its direction. Existing gravity,
                // impacts and tangential motion retain ordinary Rigidbody behavior.
                float available = Mathf.Max(0, requestedSpeed - Vector3.Dot(body.linearVelocity, force.normalized));
                force = Vector3.ClampMagnitude(force, body.mass * available / Time.fixedDeltaTime);
                body.AddForce(force, ForceMode.Force);
            }
        }

        private static bool SamePhysicsWorld(Collider a, Collider b) => a != null && b != null &&
            a.gameObject.scene.GetPhysicsScene().Equals(b.gameObject.scene.GetPhysicsScene());

        private void RestoreIgnoredPushers()
        {
            foreach (var pair in ignoredPushers)
                if (SamePhysicsWorld(pair.Local, pair.Remote)) Physics.IgnoreCollision(pair.Local, pair.Remote, false);
            ignoredPushers.Clear();
        }
        private void OnDisable() { RestoreIgnoredPushers(); pushes.Clear(); receivedPushes.Clear(); }
        public override void OnNetworkDespawn() => OnDisable();

        protected override void WriteHandoffState(NetWriter writer)
        {
            handoffVelocity = body.linearVelocity;
            handoffAngularVelocity = body.angularVelocity;
            writer.Write(handoffVelocity);
            writer.Write(handoffAngularVelocity);
        }

        protected override void ReadHandoffState(NetReader reader)
        {
            handoffVelocity = reader.ReadVector3();
            handoffAngularVelocity = reader.ReadVector3();
        }
    }
}
