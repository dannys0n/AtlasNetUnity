using System;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>Simulates 3D physics on the server; remote copies follow NetworkTransform.</summary>
    [AddComponentMenu("AtlasNet/Network Rigidbody")]
    [DisallowMultipleComponent]
    public sealed class NetworkRigidbody : NetworkBehaviour
    {
        public override void OnNetworkSpawn()
        {
            var body = GetComponent<Rigidbody>();
            var networkTransform = GetComponent<NetworkTransform>();
            if (body == null || networkTransform == null)
                throw new InvalidOperationException("NetworkRigidbody requires Rigidbody and NetworkTransform on the same GameObject");
            if (!networkTransform.SyncPosition || !networkTransform.SyncRotation ||
                networkTransform.PositionWriter != TransformWriter.Server ||
                networkTransform.RotationWriter != TransformWriter.Server ||
                networkTransform.Target != transform)
                throw new InvalidOperationException("NetworkRigidbody requires NetworkTransform to synchronize this object's position and rotation with Server writers");

            if (HasAuthority) return;

            body.isKinematic = true;
            // NetworkTransform already interpolates remote poses; Rigidbody interpolation
            // would be a second, conflicting presentation path on the proxy.
            body.interpolation = RigidbodyInterpolation.None;
        }
    }
}
