using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    public enum AnimatorWriter { Server, Owner }

    /// <summary>Synchronizes one Animator's parameters and current states. Add one component per Animator.</summary>
    [AddComponentMenu("AtlasNet/Network Animator")]
    public sealed class NetworkAnimator : NetworkBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private AnimatorWriter writer = AnimatorWriter.Server;

        private readonly Dictionary<int, object> lastValues = new Dictionary<int, object>();
        private int[] lastStates;

        public AnimatorWriter Writer => writer;

        private void EnsureAnimator()
        {
            if (animator == null) animator = GetComponentInChildren<Animator>(true);
            if (animator == null)
                throw new InvalidOperationException($"NetworkAnimator on {name} needs an Animator reference");
            if (lastStates == null || lastStates.Length != animator.layerCount)
                lastStates = new int[animator.layerCount];
        }

        public override void OnNetworkSpawn()
        {
            EnsureAnimator();
            CacheCurrent();
        }

        public override void OnNetworkTick()
        {
            if (writer == AnimatorWriter.Server ? !HasAuthority : !IsOwner) return;
            EnsureAnimator();
            using (var payload = new NetWriter())
            {
                var changed = new List<AnimatorControllerParameter>();
                foreach (var parameter in animator.parameters)
                {
                    if (parameter.type == AnimatorControllerParameterType.Trigger) continue;
                    object current = ReadParameter(parameter);
                    if (!lastValues.TryGetValue(parameter.nameHash, out var previous) || !Equals(current, previous))
                    {
                        changed.Add(parameter);
                        lastValues[parameter.nameHash] = current;
                    }
                }

                payload.Write((ushort)changed.Count);
                foreach (var parameter in changed) WriteParameter(payload, parameter);

                var changedLayers = new List<int>();
                for (int layer = 0; layer < animator.layerCount; layer++)
                {
                    int state = animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
                    if (state == lastStates[layer]) continue;
                    changedLayers.Add(layer);
                    lastStates[layer] = state;
                }
                if (changedLayers.Count > byte.MaxValue)
                    throw new InvalidOperationException("NetworkAnimator supports at most 255 layers");
                payload.Write((byte)changedLayers.Count);
                foreach (int layer in changedLayers) WriteLayer(payload, layer);
                if (changed.Count > 0 || changedLayers.Count > 0)
                    NetworkManager.SendAnimator(this, payload.ToArray());
            }
        }

        /// <summary>Use this for transient trigger events; Animator.SetTrigger alone cannot be sampled reliably.</summary>
        public void SetTrigger(string parameterName)
        {
            if (!IsSpawned) throw new InvalidOperationException("NetworkAnimator is not spawned");
            if (writer == AnimatorWriter.Server ? !HasAuthority : !IsOwner)
                throw new InvalidOperationException("Only the configured Animator writer can set a network trigger");
            EnsureAnimator();
            int hash = Animator.StringToHash(parameterName);
            bool found = false;
            foreach (var parameter in animator.parameters)
                if (parameter.nameHash == hash && parameter.type == AnimatorControllerParameterType.Trigger)
                    found = true;
            if (!found) throw new ArgumentException($"Animator has no trigger '{parameterName}'", nameof(parameterName));
            animator.SetTrigger(hash);
            using (var payload = new NetWriter())
            {
                payload.Write((ushort)1);
                payload.Write(hash);
                payload.Write((byte)AnimatorControllerParameterType.Trigger);
                payload.Write((byte)0);
                NetworkManager.SendAnimator(this, payload.ToArray());
            }
        }

        protected override void WriteExtraSnapshot(NetWriter output)
        {
            EnsureAnimator();
            var parameters = animator.parameters;
            ushort count = 0;
            foreach (var parameter in parameters)
                if (parameter.type != AnimatorControllerParameterType.Trigger) count++;
            output.Write(count);
            foreach (var parameter in parameters)
                if (parameter.type != AnimatorControllerParameterType.Trigger) WriteParameter(output, parameter);
            if (animator.layerCount > byte.MaxValue)
                throw new InvalidOperationException("NetworkAnimator supports at most 255 layers");
            output.Write((byte)animator.layerCount);
            for (int layer = 0; layer < animator.layerCount; layer++) WriteLayer(output, layer);
        }

        protected override void ReadExtraSnapshot(NetReader reader)
        {
            EnsureAnimator();
            Apply(reader);
            CacheCurrent();
        }

        internal override void WriteExtraOwnerState(NetWriter output)
        {
            bool ownerWritten = writer == AnimatorWriter.Owner;
            output.Write(ownerWritten);
            if (ownerWritten) WriteExtraSnapshot(output);
        }

        internal override void ReadExtraOwnerState(NetReader reader)
        {
            bool ownerWritten = reader.ReadBool();
            if (ownerWritten != (writer == AnimatorWriter.Owner))
                throw new InvalidOperationException("Animator writer differs across workers");
            if (ownerWritten) ReadExtraSnapshot(reader);
        }

        internal bool AcceptOwnerState(SessionId sender, byte[] payload)
        {
            if (writer != AnimatorWriter.Owner || sender.Value == 0 || sender != OwnerSession) return false;
            using (var reader = new NetReader(payload))
            {
                Apply(reader);
                if (reader.HasRemaining) throw new InvalidOperationException("Extra NetworkAnimator data");
            }
            return true;
        }

        internal void AcceptServerState(byte[] payload)
        {
            using (var reader = new NetReader(payload))
            {
                Apply(reader);
                if (reader.HasRemaining) throw new InvalidOperationException("Extra NetworkAnimator data");
            }
        }

        private void Apply(NetReader reader)
        {
            EnsureAnimator();
            int count = reader.ReadUShort();
            for (int i = 0; i < count; i++)
            {
                int hash = reader.ReadInt();
                var type = (AnimatorControllerParameterType)reader.ReadByte();
                bool known = false;
                foreach (var parameter in animator.parameters)
                    if (parameter.nameHash == hash && parameter.type == type) known = true;
                if (!known) throw new InvalidOperationException($"Animator parameter {hash}/{type} differs across prefabs");
                switch (type)
                {
                    case AnimatorControllerParameterType.Bool: animator.SetBool(hash, reader.ReadBool()); break;
                    case AnimatorControllerParameterType.Int: animator.SetInteger(hash, reader.ReadInt()); break;
                    case AnimatorControllerParameterType.Float: animator.SetFloat(hash, reader.ReadFloat()); break;
                    case AnimatorControllerParameterType.Trigger: animator.SetTrigger(hash); break;
                    default: throw new InvalidOperationException($"Unsupported Animator parameter type {type}");
                }
            }
            int layers = reader.ReadByte();
            for (int i = 0; i < layers; i++)
            {
                int layer = reader.ReadByte();
                int state = reader.ReadInt();
                float time = reader.ReadFloat();
                if (layer >= animator.layerCount) throw new InvalidOperationException("Animator layer count differs across prefabs");
                if (state != 0 && animator.GetCurrentAnimatorStateInfo(layer).fullPathHash != state)
                    animator.Play(state, layer, time);
            }
        }

        private void CacheCurrent()
        {
            lastValues.Clear();
            foreach (var parameter in animator.parameters)
                if (parameter.type != AnimatorControllerParameterType.Trigger)
                    lastValues[parameter.nameHash] = ReadParameter(parameter);
            for (int layer = 0; layer < animator.layerCount; layer++)
                lastStates[layer] = animator.GetCurrentAnimatorStateInfo(layer).fullPathHash;
        }

        private object ReadParameter(AnimatorControllerParameter parameter)
        {
            int hash = parameter.nameHash;
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool: return animator.GetBool(hash);
                case AnimatorControllerParameterType.Int: return animator.GetInteger(hash);
                case AnimatorControllerParameterType.Float: return animator.GetFloat(hash);
                default: throw new InvalidOperationException($"Unsupported Animator parameter type {parameter.type}");
            }
        }

        private void WriteParameter(NetWriter output, AnimatorControllerParameter parameter)
        {
            output.Write(parameter.nameHash);
            output.Write((byte)parameter.type);
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Bool: output.Write(animator.GetBool(parameter.nameHash)); break;
                case AnimatorControllerParameterType.Int: output.Write(animator.GetInteger(parameter.nameHash)); break;
                case AnimatorControllerParameterType.Float: output.Write(animator.GetFloat(parameter.nameHash)); break;
            }
        }

        private void WriteLayer(NetWriter output, int layer)
        {
            var state = animator.GetCurrentAnimatorStateInfo(layer);
            output.Write((byte)layer);
            output.Write(state.fullPathHash);
            output.Write(state.normalizedTime);
        }
    }
}
