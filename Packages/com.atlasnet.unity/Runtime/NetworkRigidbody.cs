using System;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>Simulates 3D physics on the server; remote copies follow NetworkTransform.</summary>
    [AddComponentMenu("AtlasNet/Network Rigidbody")]
    [DisallowMultipleComponent]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        private Rigidbody body;
        private bool initialKinematic;
        private RigidbodyInterpolation initialInterpolation;
        private Vector3 handoffVelocity;
        private Vector3 handoffAngularVelocity;

        public override void OnNetworkSpawn()
        {
            body = GetComponent<Rigidbody>();
            var networkTransform = GetComponent<NetworkTransform>();
            if (body == null || networkTransform == null)
                throw new InvalidOperationException("NetworkRigidbody requires Rigidbody and NetworkTransform on the same GameObject");
            if (!networkTransform.SyncPosition || !networkTransform.SyncRotation ||
                networkTransform.PositionWriter != TransformWriter.Server ||
                networkTransform.RotationWriter != TransformWriter.Server ||
                networkTransform.Target != transform)
                throw new InvalidOperationException("NetworkRigidbody requires NetworkTransform to synchronize this object's position and rotation with Server writers");

            initialKinematic = body.isKinematic;
            initialInterpolation = body.interpolation;
            ApplyAuthority();
        }

        public override void OnSimulationAuthorityChanged() => ApplyAuthority();

        private void ApplyAuthority()
        {
            if (body == null) return;
            body.isKinematic = !HasAuthority || initialKinematic;
            body.interpolation = HasAuthority ? initialInterpolation : RigidbodyInterpolation.None;
            if (HasAuthority && !body.isKinematic)
            {
                body.linearVelocity = handoffVelocity;
                body.angularVelocity = handoffAngularVelocity;
            }
        }

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
