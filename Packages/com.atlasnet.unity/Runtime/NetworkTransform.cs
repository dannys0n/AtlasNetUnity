using UnityEngine;

namespace AtlasNet
{
    public enum TransformWriter { Server, Owner }

    /// <summary>Position and rotation have independent writers; owner authority need not cover an entire entity.</summary>
    [AddComponentMenu("AtlasNet/Network Transform")]
    public sealed class NetworkTransform : NetworkBehaviour
    {
        [SerializeField, Tooltip("Defaults to this object's Transform.")]
        private Transform target;
        [SerializeField, Tooltip("Sync relative to the parent. The target hierarchy must match on all peers.")]
        private bool inLocalSpace;
        [SerializeField] private bool syncPosition = true;
        [SerializeField] private bool syncRotation = true;
        [SerializeField] private TransformWriter positionWriter = TransformWriter.Server;
        [SerializeField] private TransformWriter rotationWriter = TransformWriter.Owner;
        [SerializeField, Range(1, 30), Tooltip("Check for changes once per this many network ticks.")]
        private int sendIntervalTicks = 1;
        [SerializeField, Min(0), Tooltip("Minimum position change since the last position sent, in units of the chosen space.")]
        private float positionThreshold = 0.001f;
        [SerializeField, Min(0), Tooltip("Minimum rotation change since the last rotation sent, in degrees.")]
        private float rotationThreshold = 0.1f;
        [SerializeField, Tooltip("Buffer pose channels received from another writer on joined clients.")]
        private bool interpolate = true;
        [SerializeField, Min(1), Tooltip("Display delay in network ticks. The effective delay is at least two send intervals.")]
        private int interpolationBufferTicks = 3;
        [SerializeField, Tooltip("Apply large position changes immediately instead of interpolating across them.")]
        private bool snapOnLargeMove;
        [SerializeField, Min(0), Tooltip("Distance between consecutive received positions, in units of the chosen space.")]
        private float snapDistance = 5f;

        private Vector3 lastSentPosition;
        private Quaternion lastSentRotation;
        private Vector3 lastReceivedPosition;
        private readonly BufferedInterpolator<Vector3> positions = new(Vector3.Lerp);
        private readonly BufferedInterpolator<Quaternion> rotations = new(Quaternion.Slerp);
        private bool positionBuffered, rotationBuffered;

        public int SendIntervalTicks => Mathf.Max(1, sendIntervalTicks);
        public TransformWriter PositionWriter => positionWriter;
        public TransformWriter RotationWriter => rotationWriter;
        internal bool SyncPosition => syncPosition;
        internal bool SyncRotation => syncRotation;
        internal Transform Target => target != null ? target : transform;

        private Vector3 Position
        {
            get => inLocalSpace ? Target.localPosition : Target.position;
            set
            {
                if (inLocalSpace) Target.localPosition = value;
                else Target.position = value;
            }
        }

        private Quaternion Rotation
        {
            get => inLocalSpace ? Target.localRotation : Target.rotation;
            set
            {
                if (inLocalSpace) Target.localRotation = value;
                else Target.rotation = value;
            }
        }

        public override void OnNetworkSpawn() => ResetPose();

        private void Update()
        {
            if (!IsSpawned || NetworkManager.IsServer || !interpolate) return;
            double displayTime = Time.unscaledTimeAsDouble -
                (double)Mathf.Max(interpolationBufferTicks, SendIntervalTicks * 2) / NetworkManager.TickRate;
            if (positionBuffered) Position = positions.Evaluate(displayTime);
            if (rotationBuffered) Rotation = rotations.Evaluate(displayTime);
        }

        public override void OnNetworkTick()
        {
            if (NetworkManager.Tick % (uint)SendIntervalTicks != 0) return;
            Vector3 position = Position;
            Quaternion rotation = Rotation;
            float distance = Mathf.Max(0, positionThreshold);
            byte flags = 0;
            if (syncPosition && CanWrite(positionWriter) && (position - lastSentPosition).sqrMagnitude > distance * distance)
            {
                flags |= 1;
                lastSentPosition = position;
            }
            if (syncRotation && CanWrite(rotationWriter) && Quaternion.Angle(rotation, lastSentRotation) > Mathf.Max(0, rotationThreshold))
            {
                flags |= 2;
                lastSentRotation = rotation;
            }
            if (flags != 0) NetworkObject.Manager.SendTransform(this, flags, position, rotation);
        }

        private bool CanWrite(TransformWriter writer) => writer == TransformWriter.Owner ? IsOwner : HasAuthority;

        internal bool AcceptOwnerState(SessionId sender, byte flags, Vector3 position, Quaternion rotation)
        {
            if (sender != OwnerSession || sender.Value == 0 || (flags & ~3) != 0) return false;
            if ((flags & 1) != 0 && positionWriter != TransformWriter.Owner) return false;
            if ((flags & 2) != 0 && rotationWriter != TransformWriter.Owner) return false;
            if ((flags & 1) != 0 && !syncPosition) return false;
            if ((flags & 2) != 0 && !syncRotation) return false;
            if ((flags & 1) != 0) Position = position;
            if ((flags & 2) != 0) Rotation = rotation;
            return true;
        }

        internal void AcceptServerState(byte flags, Vector3 position, Quaternion rotation)
        {
            bool applyPosition = (flags & 1) != 0 && !(IsOwner && positionWriter == TransformWriter.Owner);
            bool applyRotation = (flags & 2) != 0 && !(IsOwner && rotationWriter == TransformWriter.Owner);
            bool buffer = interpolate && !NetworkManager.IsServer;
            float distance = Mathf.Max(0, snapDistance);
            bool snap = buffer && applyPosition && snapOnLargeMove &&
                (position - lastReceivedPosition).sqrMagnitude > distance * distance;
            double now = Time.unscaledTimeAsDouble;

            if (applyPosition)
            {
                lastReceivedPosition = position;
                positionBuffered = buffer && !snap;
                if (positionBuffered) positions.Add(now, position);
                else
                {
                    Position = position;
                    positions.Reset(now, position);
                }
            }
            if (applyRotation)
            {
                rotationBuffered = buffer && !snap;
                if (rotationBuffered) rotations.Add(now, rotation);
                else
                {
                    Rotation = rotation;
                    rotations.Reset(now, rotation);
                }
            }
        }

        protected override void WriteExtraSnapshot(NetWriter writer)
        {
            writer.Write(Position);
            writer.Write(Rotation);
        }

        protected override void ReadExtraSnapshot(NetReader reader)
        {
            Position = reader.ReadVector3();
            Rotation = reader.ReadQuaternion();
            ResetPose();
        }

        private void ResetPose()
        {
            lastSentPosition = lastReceivedPosition = Position;
            lastSentRotation = Rotation;
            double now = Time.unscaledTimeAsDouble;
            positions.Reset(now, lastSentPosition);
            rotations.Reset(now, lastSentRotation);
            positionBuffered = rotationBuffered = false;
        }

        internal override void WriteExtraOwnerState(NetWriter writer)
        {
            byte flags = 0;
            if (syncPosition && positionWriter == TransformWriter.Owner) flags |= 1;
            if (syncRotation && rotationWriter == TransformWriter.Owner) flags |= 2;
            writer.Write(flags);
            if ((flags & 1) != 0) writer.Write(Position);
            if ((flags & 2) != 0) writer.Write(Rotation);
        }

        internal override void ReadExtraOwnerState(NetReader reader)
        {
            byte flags = reader.ReadByte();
            byte expected = 0;
            if (syncPosition && positionWriter == TransformWriter.Owner) expected |= 1;
            if (syncRotation && rotationWriter == TransformWriter.Owner) expected |= 2;
            if (flags != expected) throw new System.InvalidOperationException("Owner transform channels differ across workers");
            if ((flags & 1) != 0)
            {
                Position = reader.ReadVector3();
                lastSentPosition = lastReceivedPosition = Position;
                positions.Reset(Time.unscaledTimeAsDouble, lastSentPosition);
                positionBuffered = false;
            }
            if ((flags & 2) != 0)
            {
                Rotation = reader.ReadQuaternion();
                lastSentRotation = Rotation;
                rotations.Reset(Time.unscaledTimeAsDouble, lastSentRotation);
                rotationBuffered = false;
            }
        }
    }
}
