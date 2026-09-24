using UnityEngine;

namespace AtlasNet
{
    public enum TransformWriter { Server, Owner }

    /// <summary>Position and rotation have independent writers; owner authority need not cover an entire entity.</summary>
    [AddComponentMenu("AtlasNet/Network Transform")]
    public sealed class NetworkTransform : NetworkBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private TransformWriter positionWriter = TransformWriter.Server;
        [SerializeField] private TransformWriter rotationWriter = TransformWriter.Owner;
        [SerializeField, Tooltip("Interpolate remote client copies over one network tick. The owner and server never smooth their simulation transform.")]
        private bool interpolate = true;
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        private Vector3 positionFrom;
        private Vector3 positionTo;
        private Quaternion rotationFrom;
        private Quaternion rotationTo;
        private float positionBlend = 1f;
        private float rotationBlend = 1f;

        public TransformWriter PositionWriter => positionWriter;
        public TransformWriter RotationWriter => rotationWriter;

        public override void OnNetworkSpawn()
        {
            if (target == null) target = transform;
            lastPosition = target.position;
            lastRotation = target.rotation;
            positionFrom = positionTo = lastPosition;
            rotationFrom = rotationTo = lastRotation;
        }

        private void Update()
        {
            if (!IsSpawned || NetworkManager.IsServer || !interpolate) return;
            float step = Time.deltaTime * NetworkManager.TickRate;
            if (positionBlend < 1f)
            {
                positionBlend = Mathf.Min(1f, positionBlend + step);
                target.position = Vector3.Lerp(positionFrom, positionTo, positionBlend);
            }
            if (rotationBlend < 1f)
            {
                rotationBlend = Mathf.Min(1f, rotationBlend + step);
                target.rotation = Quaternion.Slerp(rotationFrom, rotationTo, rotationBlend);
            }
        }

        public override void OnNetworkTick()
        {
            bool writePosition = syncPosition && (HasAuthority
                ? positionWriter == TransformWriter.Server || (positionWriter == TransformWriter.Owner && IsOwner)
                : positionWriter == TransformWriter.Owner && IsOwner);
            bool writeRotation = syncRotation && (HasAuthority
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
            {
                if (interpolate && !NetworkManager.IsServer)
                {
                    positionFrom = target.position;
                    positionTo = position;
                    positionBlend = 0f;
                }
                else target.position = position;
            }
            if ((flags & 2) != 0 && !(IsOwner && rotationWriter == TransformWriter.Owner))
            {
                if (interpolate && !NetworkManager.IsServer)
                {
                    rotationFrom = target.rotation;
                    rotationTo = rotation;
                    rotationBlend = 0f;
                }
                else target.rotation = rotation;
            }
        }

        protected override void WriteExtraSnapshot(NetWriter writer)
        {
            writer.Write(target.position);
            writer.Write(target.rotation);
        }

        protected override void ReadExtraSnapshot(NetReader reader)
        {
            if (target == null) target = transform;
            target.position = reader.ReadVector3();
            target.rotation = reader.ReadQuaternion();
            lastPosition = target.position;
            lastRotation = target.rotation;
            positionFrom = positionTo = lastPosition;
            rotationFrom = rotationTo = lastRotation;
            positionBlend = rotationBlend = 1f;
        }
    }
}
