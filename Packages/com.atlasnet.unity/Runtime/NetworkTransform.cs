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
        [SerializeField, Tooltip("Buffer and interpolate remote client copies. The owner and server never smooth their simulation transform.")]
        private bool interpolate = true;
        [SerializeField, Range(1,30), Tooltip("Send changed transforms once per this many network ticks. Remote interpolation accounts for this interval.")]
        private int sendIntervalTicks = 1;
        public int SendIntervalTicks => Mathf.Max(1,sendIntervalTicks);
        private Vector3 lastPosition;
        private Quaternion lastRotation;
        [SerializeField, Min(1), Tooltip("Buffered display delay in network ticks for joined clients. Low send rates use at least two send intervals.")]
        private int interpolationBufferTicks = 3;
        private readonly BufferedInterpolator<Vector3> positions = new(Vector3.Lerp);
        private readonly BufferedInterpolator<Quaternion> rotations = new(Quaternion.Slerp);
        private bool positionBuffered, rotationBuffered;

        public TransformWriter PositionWriter => positionWriter;
        public TransformWriter RotationWriter => rotationWriter;
        internal bool SyncPosition => syncPosition;
        internal bool SyncRotation => syncRotation;
        internal Transform Target => target != null ? target : transform;

        public override void OnNetworkSpawn()
        {
            if (target == null) target = transform;
            lastPosition = target.position;
            lastRotation = target.rotation;
            positions.Reset(Time.unscaledTimeAsDouble, lastPosition);
            rotations.Reset(Time.unscaledTimeAsDouble, lastRotation);
            positionBuffered = rotationBuffered = false;
        }

        private void Update()
        {
            if (!IsSpawned || NetworkManager.IsServer || !interpolate) return;
            double displayTime = Time.unscaledTimeAsDouble -
                (double)Mathf.Max(interpolationBufferTicks, SendIntervalTicks * 2) / NetworkManager.TickRate;
            if (positionBuffered) target.position = positions.Evaluate(displayTime);
            if (rotationBuffered) target.rotation = rotations.Evaluate(displayTime);
        }

        public override void OnNetworkTick()
        {
            if(NetworkManager.Tick % (uint)SendIntervalTicks != 0)return;
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
                    positions.Add(Time.unscaledTimeAsDouble, position);
                    positionBuffered = true;
                }
                else target.position = position;
            }
            if ((flags & 2) != 0 && !(IsOwner && rotationWriter == TransformWriter.Owner))
            {
                if (interpolate && !NetworkManager.IsServer)
                {
                    rotations.Add(Time.unscaledTimeAsDouble, rotation);
                    rotationBuffered = true;
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
            positions.Reset(Time.unscaledTimeAsDouble, lastPosition);
            rotations.Reset(Time.unscaledTimeAsDouble, lastRotation);
            positionBuffered = rotationBuffered = false;
        }

        internal override void WriteExtraOwnerState(NetWriter writer)
        {
            byte flags = 0;
            if (syncPosition && positionWriter == TransformWriter.Owner) flags |= 1;
            if (syncRotation && rotationWriter == TransformWriter.Owner) flags |= 2;
            writer.Write(flags);
            if ((flags & 1) != 0) writer.Write(Target.position);
            if ((flags & 2) != 0) writer.Write(Target.rotation);
        }

        internal override void ReadExtraOwnerState(NetReader reader)
        {
            byte flags = reader.ReadByte();
            byte expected = 0;
            if (syncPosition && positionWriter == TransformWriter.Owner) expected |= 1;
            if (syncRotation && rotationWriter == TransformWriter.Owner) expected |= 2;
            if (flags != expected) throw new System.InvalidOperationException("Owner transform channels differ across workers");
            if (target == null) target = transform;
            if ((flags & 1) != 0)
            {
                target.position = reader.ReadVector3();
                lastPosition = target.position;
                positions.Reset(Time.unscaledTimeAsDouble, lastPosition);
                positionBuffered = false;
            }
            if ((flags & 2) != 0)
            {
                target.rotation = reader.ReadQuaternion();
                lastRotation = target.rotation;
                rotations.Reset(Time.unscaledTimeAsDouble, lastRotation);
                rotationBuffered = false;
            }
        }
    }
}
