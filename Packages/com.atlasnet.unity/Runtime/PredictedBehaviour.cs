using System;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>Server-authoritative prediction for a custom motor on a NetworkObject.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public abstract class PredictedBehaviour<TInput, TState> : NetworkBehaviour
        where TInput : unmanaged, IPredictionData
        where TState : unmanaged, IPredictionData
    {
        [Header("Local Presentation")]
        [SerializeField, Tooltip("Optional direct child containing the mesh or camera rig.")]
        private Transform visualRoot;
        [SerializeField, Tooltip("Smooth between simulation ticks and ease out corrections on the visual child.")]
        private bool smoothLocalVisuals = true;
        [SerializeField, Min(0), Tooltip("Corrections larger than this distance snap the local visual child.")]
        private float correctionSnapDistance = 2f;

        [Header("Network Tuning")]
        [SerializeField, Range(1, 30), Tooltip("Ticks between server snapshots. Input and simulation still run every network tick.")]
        private int snapshotIntervalTicks = 1;
        [SerializeField, Tooltip("Smooth remote copies between received server poses.")]
        private bool interpolateSpectators = true;
        [SerializeField, Min(1), Tooltip("Spectator display delay in ticks. The effective delay is at least two snapshot intervals.")]
        private int spectatorBufferTicks = 2;
        [SerializeField, Tooltip("Apply large jumps between spectator snapshots immediately.")]
        private bool snapSpectatorsOnLargeMove;
        [SerializeField, Min(0), Tooltip("Distance between consecutive server positions that triggers a spectator snap.")]
        private float spectatorSnapDistance = 5f;

        private readonly PredictionTimeline<TInput> timeline = new();
        private readonly BufferedInterpolator<Vector3> remotePositions = new(Vector3.Lerp);
        private readonly BufferedInterpolator<Quaternion> remoteRotations = new(Quaternion.Slerp);
        private Pose receivedPose;
        private Pose previousTickPose;
        private Pose visualLocalPose;
        private Vector3 visualError;
        private CharacterController capsule;
        private Action<TInput> replayInput;
        private uint inputRpc, snapshotRpc;
        private uint lastSnapshotTick, lastSnapshotEpoch, lastPublishedTick;
        private bool initialized, receivedSnapshot, publishNextTick;

        private int SnapshotIntervalTicks => Mathf.Clamp(snapshotIntervalTicks, 1, 30);
        private Pose RootPose => new(transform.position, transform.rotation);

        /// <summary>The optional smoothed child. Camera rigs can follow it after LateUpdate.</summary>
        public Transform VisualRoot => visualRoot;
        internal sealed override bool SupportsPredictedPose => true;

        /// <summary>True while the motor reruns previously predicted inputs after a correction.</summary>
        protected bool IsReplaying { get; private set; }

        /// <summary>Read local input once per new tick. Latch button presses in Update until this consumes them.</summary>
        protected abstract TInput GetInputForTick();

        /// <summary>Run a fixed simulation step. This can run again during reconciliation.</summary>
        protected abstract void Simulate(TInput input, float deltaTime);

        /// <summary>Capture gameplay state needed by the next tick. AtlasNet captures the root pose separately.</summary>
        protected abstract TState CaptureState();

        /// <summary>Restore gameplay state for a snapshot, which can arrive before OnPredictionSpawn. AtlasNet restores the root pose separately.</summary>
        protected abstract void RestoreState(TState state);

        /// <summary>Clamp or validate input consistently on the owner and the server.</summary>
        protected virtual TInput SanitizeInput(TInput input) => input;

        /// <summary>Input used by the server when the next input has not arrived.</summary>
        protected virtual TInput GetMissingInput() => default;

        protected virtual void OnPredictionSpawn() { }
        protected virtual void OnPredictionDespawn() { }

        public sealed override void OnNetworkSpawn()
        {
            capsule = GetComponent<CharacterController>();
            if (visualRoot != null && visualRoot.parent != transform)
            {
                NetworkManager.Log(LogType.Warning, "The predicted visual root must be a direct child.");
                visualRoot = null;
            }

            if (visualRoot != null) visualLocalPose = new Pose(visualRoot.localPosition, visualRoot.localRotation);

            // Typed method references also keep the handlers reachable in AOT builds.
            inputRpc = RpcMethods.IdOf(((Action<byte[], SessionId>)SubmitPredictionInputRpc).Method);
            snapshotRpc = RpcMethods.IdOf(((Action<byte[]>)PublishPredictionSnapshotRpc).Method);
            replayInput ??= RunStep;
            timeline.InitializeLocalSequence();
            receivedPose = previousTickPose = RootPose;
            ResetInterpolation(receivedPose);
            publishNextTick = initialized = true;
            RefreshSimulationRole();
            OnPredictionSpawn();
        }

        public sealed override void OnNetworkDespawn()
        {
            RestoreVisual();
            initialized = receivedSnapshot = false;
            timeline.Reset();
            OnPredictionDespawn();
        }

        public sealed override void OnSimulationAuthorityChanged()
        {
            if (initialized) RefreshSimulationRole();
        }

        private void RefreshSimulationRole()
        {
            bool simulates = IsOwner || HasAuthority;
            if (capsule != null) capsule.enabled = simulates;
            previousTickPose = RootPose;
            if (!simulates)
            {
                RestoreVisual();
                receivedPose = previousTickPose;
                ResetInterpolation(receivedPose);
            }
            if (HasAuthority) publishNextTick = true;
        }

        public sealed override void OnNetworkTick()
        {
            if (!initialized || !(IsOwner || HasAuthority)) return;
            previousTickPose = RootPose;

            if (IsOwner)
            {
                var input = SanitizeInput(GetInputForTick());
                uint sequence = timeline.RecordLocal(input, authoritative: HasAuthority);
                RunStep(input);
                if (!HasAuthority)
                {
                    SendInput(sequence, input);
                    return;
                }
            }
            else
            {
                RunStep(timeline.TryConsume(out var input) ? input : GetMissingInput());
            }

            PublishSnapshot();
        }

        private void RunStep(TInput input) => Simulate(input, 1f / NetworkManager.TickRate);

        private void SendInput(uint sequence, TInput input)
        {
            NetworkManager.SendRpc(this, RpcDestination.Authority, default, inputRpc, writer =>
            {
                using var payload = new NetWriter();
                payload.Write(sequence);
                WriteData(payload, input);
                WritePreviousInput(payload, unchecked(sequence - 1));
                WritePreviousInput(payload, unchecked(sequence - 2));
                writer.WriteBytes(payload.ToArray());
            });
        }

        private void WritePreviousInput(NetWriter writer, uint sequence)
        {
            bool exists = timeline.TryGetLocal(sequence, out var input);
            writer.Write(exists);
            if (exists) WriteData(writer, input);
        }

        [Rpc(SendTo.Authority, InvokePermission = RpcInvokePermission.Owner, Delivery = RpcDelivery.UnreliableSequenced)]
        protected void SubmitPredictionInputRpc(byte[] payload, SessionId sender)
        {
            if (!initialized || !HasAuthority || IsOwner || sender != OwnerSession) return;
            using var reader = new NetReader(payload);
            uint sequence = reader.ReadUInt();
            var input = ReadData<TInput>(reader);
            bool hasPrevious = reader.ReadBool();
            var previous = hasPrevious ? ReadData<TInput>(reader) : default;
            bool hasOlder = reader.ReadBool();
            var older = hasOlder ? ReadData<TInput>(reader) : default;
            if (reader.HasRemaining) throw new InvalidOperationException("Unexpected data after prediction inputs.");

            if (hasOlder) timeline.Queue(unchecked(sequence - 2), SanitizeInput(older));
            if (hasPrevious) timeline.Queue(unchecked(sequence - 1), SanitizeInput(previous));
            timeline.Queue(sequence, SanitizeInput(input));
        }

        private void PublishSnapshot()
        {
            uint tick = NetworkManager.Tick;
            if (!publishNextTick && unchecked(tick - lastPublishedTick) < (uint)SnapshotIntervalTicks) return;

            NetworkManager.SendRpc(this, RpcDestination.Observers, default, snapshotRpc, writer =>
            {
                using var payload = new NetWriter();
                payload.Write(NetworkObject.AuthorityEpoch);
                payload.Write(tick);
                WriteExtraSnapshot(payload);
                writer.WriteBytes(payload.ToArray());
            });
            lastPublishedTick = tick;
            publishNextTick = false;
        }

        [Rpc(SendTo.Observers, InvokePermission = RpcInvokePermission.Server, Delivery = RpcDelivery.UnreliableSequenced)]
        protected void PublishPredictionSnapshotRpc(byte[] payload)
        {
            if (!initialized || HasAuthority) return;
            using var reader = new NetReader(payload);
            uint epoch = reader.ReadUInt();
            uint tick = reader.ReadUInt();
            if (epoch != NetworkObject.AuthorityEpoch ||
                (receivedSnapshot && epoch == lastSnapshotEpoch && PredictionTimeline<TInput>.Difference(tick, lastSnapshotTick) <= 0)) return;

            uint sequence = reader.ReadUInt();
            var pose = new Pose(reader.ReadVector3(), reader.ReadQuaternion());
            var state = ReadData<TState>(reader);
            if (reader.HasRemaining) throw new InvalidOperationException("Unexpected data after a prediction snapshot.");

            if (!IsOwner && receivedSnapshot && epoch != lastSnapshotEpoch) ResetInterpolation(receivedPose);
            receivedSnapshot = true;
            lastSnapshotEpoch = epoch;
            lastSnapshotTick = tick;
            if (IsOwner) Reconcile(sequence, pose, state);
            else
            {
                timeline.RestoreAcknowledgement(sequence);
                RestoreState(state);
                UpdateSpectatorPose(pose);
            }
        }

        private void Reconcile(uint sequence, Pose pose, TState state)
        {
            Pose before = RootPose;
            RestoreSnapshot(pose, state);
            IsReplaying = true;
            try { timeline.ReplayFrom(sequence, replayInput); }
            finally { IsReplaying = false; }

            Vector3 correction = before.position - transform.position;
            float snapDistance = Mathf.Max(0, correctionSnapDistance);
            if (!smoothLocalVisuals || correction.sqrMagnitude > snapDistance * snapDistance)
            {
                previousTickPose = RootPose;
                visualError = Vector3.zero;
            }
            else
            {
                previousTickPose.position -= correction;
                previousTickPose.rotation = transform.rotation * Quaternion.Inverse(before.rotation) * previousTickPose.rotation;
                visualError = Vector3.ClampMagnitude(visualError + correction, snapDistance);
            }
        }

        private void UpdateSpectatorPose(Pose pose)
        {
            float distance = Mathf.Max(0, spectatorSnapDistance);
            bool snap = snapSpectatorsOnLargeMove && (pose.position - receivedPose.position).sqrMagnitude > distance * distance;
            receivedPose = pose;
            if (!interpolateSpectators || snap)
            {
                transform.SetPositionAndRotation(pose.position, pose.rotation);
                ResetInterpolation(pose);
                return;
            }

            double now = Time.unscaledTimeAsDouble;
            remotePositions.Add(now, pose.position);
            remoteRotations.Add(now, pose.rotation);
        }

        private void LateUpdate()
        {
            if (!initialized) return;
            if (IsOwner || HasAuthority)
            {
                if (visualRoot == null) return;
                if (!smoothLocalVisuals)
                {
                    RestoreVisual();
                    return;
                }

                float alpha = NetworkManager.TickInterpolation;
                Vector3 position = Vector3.Lerp(previousTickPose.position, transform.position, alpha);
                Quaternion rotation = Quaternion.Slerp(previousTickPose.rotation, transform.rotation, alpha);
                visualError *= Mathf.Exp(-18 * Time.unscaledDeltaTime);
                visualRoot.SetPositionAndRotation(
                    position + visualError + rotation * Vector3.Scale(visualLocalPose.position, transform.lossyScale),
                    rotation * visualLocalPose.rotation);
            }
            else if (interpolateSpectators)
            {
                int bufferTicks = Mathf.Max(spectatorBufferTicks, SnapshotIntervalTicks * 2);
                double time = Time.unscaledTimeAsDouble - (double)bufferTicks / NetworkManager.TickRate;
                transform.SetPositionAndRotation(remotePositions.Evaluate(time), remoteRotations.Evaluate(time));
            }
        }

        private void RestoreVisual()
        {
            if (visualRoot != null) visualRoot.SetLocalPositionAndRotation(visualLocalPose.position, visualLocalPose.rotation);
            visualError = Vector3.zero;
        }

        private void RestoreSnapshot(Pose pose, TState state)
        {
            if (capsule == null) capsule = GetComponent<CharacterController>();
            bool wasEnabled = capsule != null && capsule.enabled;
            if (wasEnabled) capsule.enabled = false;
            try
            {
                transform.SetPositionAndRotation(pose.position, pose.rotation);
                RestoreState(state);
            }
            finally
            {
                if (wasEnabled) capsule.enabled = true;
            }
        }

        private void ResetInterpolation(Pose pose)
        {
            double now = Time.unscaledTimeAsDouble;
            remotePositions.Reset(now, pose.position);
            remoteRotations.Reset(now, pose.rotation);
        }

        protected sealed override void WriteExtraSnapshot(NetWriter writer)
        {
            Pose pose = initialized && !IsOwner && !HasAuthority ? receivedPose : RootPose;
            writer.Write(timeline.Acknowledged);
            writer.Write(pose.position);
            writer.Write(pose.rotation);
            WriteData(writer, CaptureState());
        }

        protected sealed override void ReadExtraSnapshot(NetReader reader)
        {
            uint sequence = reader.ReadUInt();
            var pose = new Pose(reader.ReadVector3(), reader.ReadQuaternion());
            var state = ReadData<TState>(reader);
            if (initialized && IsOwner && !IsServer) return;
            timeline.RestoreAcknowledgement(sequence);
            RestoreSnapshot(pose, state);
            receivedPose = pose;
            if (initialized) ResetInterpolation(pose);
        }

        protected sealed override void WriteHandoffState(NetWriter writer) => timeline.WriteHandoff(writer, WriteData<TInput>);
        protected sealed override void ReadHandoffState(NetReader reader) => timeline.ReadHandoff(reader, ReadData<TInput>);

        private static void WriteData<T>(NetWriter writer, T value) where T : unmanaged, IPredictionData =>
            value.Serialize(new PredictionSerializer(writer));

        private static T ReadData<T>(NetReader reader) where T : unmanaged, IPredictionData
        {
            T value = default;
            value.Serialize(new PredictionSerializer(reader));
            return value;
        }
    }
}
