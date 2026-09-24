using UnityEngine;

namespace AtlasNet
{
    public enum TransformWriter { Server, Owner }

    /// <summary>Position and rotation have independent writers; owner authority need not cover an entire entity.</summary>
    public sealed class NetworkTransform : NetworkBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private TransformWriter positionWriter = TransformWriter.Server;
        [SerializeField] private TransformWriter rotationWriter = TransformWriter.Owner;
        private Vector3 lastPosition;
        private Quaternion lastRotation;

        public TransformWriter PositionWriter => positionWriter;
        public TransformWriter RotationWriter => rotationWriter;

        public override void OnNetworkSpawn()
        {
            if (target == null) target = transform;
            lastPosition = target.position;
            lastRotation = target.rotation;
        }

        public override void OnNetworkTick()
        {
            bool writePosition = syncPosition && (HasSimulationAuthority
                ? positionWriter == TransformWriter.Server || (positionWriter == TransformWriter.Owner && IsOwner)
                : positionWriter == TransformWriter.Owner && IsOwner);
            bool writeRotation = syncRotation && (HasSimulationAuthority
                ? rotationWriter == TransformWriter.Server || (rotationWriter == TransformWriter.Owner && IsOwner)
                : rotationWriter == TransformWriter.Owner && IsOwner);
            byte flags = 0;
            if (writePosition && (target.position - lastPosition).sqrMagnitude > 0.000001f) flags |= 1;
            if (writeRotation && Quaternion.Angle(target.rotation, lastRotation) > 0.1f) flags |= 2;
            if (flags == 0) return;
            lastPosition = target.position;
            lastRotation = target.rotation;
            NetworkObject.Manager.SendTransform(this, flags, target.position, target.rotation);
        }

        internal bool AcceptOwnerState(SessionId sender, byte flags, Vector3 position, Quaternion rotation)
        {
            if (sender != OwnerSession || sender.Value == 0 || (flags & ~3) != 0) return false;
            if ((flags & 1) != 0 && positionWriter != TransformWriter.Owner) return false;
            if ((flags & 2) != 0 && rotationWriter != TransformWriter.Owner) return false;
            if ((flags & 1) != 0 && !syncPosition) return false;
            if ((flags & 2) != 0 && !syncRotation) return false;
            if ((flags & 1) != 0) target.position = position;
            if ((flags & 2) != 0) target.rotation = rotation;
            return true;
        }

        internal void AcceptServerState(byte flags, Vector3 position, Quaternion rotation)
        {
            if ((flags & 1) != 0 && !(IsOwner && positionWriter == TransformWriter.Owner))
                target.position = position;
            if ((flags & 2) != 0 && !(IsOwner && rotationWriter == TransformWriter.Owner))
                target.rotation = rotation;
        }

        protected override void WriteExtraSnapshot(NetWriter writer)
        {
            writer.Write(target.position);
            writer.Write(target.rotation);
        }

        protected override void ReadExtraSnapshot(NetReader reader)
        {
            target.position = reader.ReadVector3();
            target.rotation = reader.ReadQuaternion();
            lastPosition = target.position;
            lastRotation = target.rotation;
        }
    }
}
