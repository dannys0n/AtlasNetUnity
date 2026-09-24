using System;
using System.Collections.Generic;

namespace AtlasNet
{
    internal interface INetworkVariable
    {
        void Write(NetWriter writer);
        void Read(NetReader reader);
    }

    /// <summary>A server-written persistent value. New observers receive its current value in the spawn snapshot.</summary>
    public sealed class NetworkVariable<T> : INetworkVariable
    {
        private readonly NetworkBehaviour behaviour;
        private readonly ushort id;
        private T value;
        public T Value => value;
        public event Action<T, T> Changed;

        internal NetworkVariable(NetworkBehaviour behaviour, ushort id, T initial)
        {
            this.behaviour = behaviour;
            this.id = id;
            value = initial;
        }

        public void Set(T next)
        {
            if (!behaviour.HasSimulationAuthority)
                throw new InvalidOperationException($"Only the server can write {behaviour.GetType().Name} variable {id}");
            if (EqualityComparer<T>.Default.Equals(value, next)) return;
            Apply(next);
            behaviour.NetworkObject.Manager.SendVariable(behaviour, id, this);
        }

        private void Apply(T next)
        {
            T old = value;
            value = next;
            Changed?.Invoke(old, next);
        }

        void INetworkVariable.Write(NetWriter writer) => NetValueCodec<T>.Write(writer, value);
        void INetworkVariable.Read(NetReader reader) => Apply(NetValueCodec<T>.Read(reader));
    }

    /// <summary>Attach to the same GameObject as NetworkObject. RPC methods use explicit numeric IDs and payloads.</summary>
    public abstract class NetworkBehaviour : UnityEngine.MonoBehaviour
    {
        private readonly Dictionary<ushort, INetworkVariable> variables = new Dictionary<ushort, INetworkVariable>();
        public NetworkObject NetworkObject { get; private set; }
        public bool IsServer => NetworkObject != null && NetworkObject.Manager.IsServer;
        public bool HasSimulationAuthority => NetworkObject != null && NetworkObject.HasSimulationAuthority;
        public bool IsOwner => NetworkObject != null && NetworkObject.IsOwner;
        public SessionId OwnerSession => NetworkObject.OwnerSession;
        internal byte BehaviourIndex { get; private set; }

        internal void Initialize(NetworkObject networkObject, byte index)
        {
            NetworkObject = networkObject;
            BehaviourIndex = index;
            OnNetworkSpawn();
        }

        protected NetworkVariable<T> RegisterVariable<T>(ushort id, T initial)
        {
            if (variables.ContainsKey(id)) throw new InvalidOperationException($"Duplicate variable ID {id}");
            var variable = new NetworkVariable<T>(this, id, initial);
            variables.Add(id, variable);
            return variable;
        }

        protected void AuthorityRpc(ushort method, Action<NetWriter> write = null) =>
            NetworkObject.Manager.SendRpc(this, RpcDestination.Authority, new SessionId(0), method, write);

        protected void ObserversRpc(ushort method, Action<NetWriter> write = null) =>
            NetworkObject.Manager.SendRpc(this, RpcDestination.Observers, new SessionId(0), method, write);

        protected void TargetRpc(SessionId target, ushort method, Action<NetWriter> write = null) =>
            NetworkObject.Manager.SendRpc(this, RpcDestination.Target, target, method, write);

        public virtual void OnNetworkSpawn() { }
        public virtual void OnNetworkDespawn() { }
        public virtual void OnNetworkTick() { }
        protected virtual void OnRpc(ushort method, NetReader payload, SessionId sender) { }
        protected virtual void WriteExtraSnapshot(NetWriter writer) { }
        protected virtual void ReadExtraSnapshot(NetReader reader) { }

        internal void ReceiveRpc(ushort method, byte[] payload, SessionId sender)
        {
            using (var reader = new NetReader(payload)) OnRpc(method, reader, sender);
        }

        internal void WriteSnapshot(NetWriter writer)
        {
            writer.Write((ushort)variables.Count);
            foreach (var pair in variables)
            {
                writer.Write(pair.Key);
                using (var valueWriter = new NetWriter())
                {
                    pair.Value.Write(valueWriter);
                    writer.WriteBytes(valueWriter.ToArray());
                }
            }
            WriteExtraSnapshot(writer);
        }

        internal void ReadSnapshot(NetReader reader)
        {
            int count = reader.ReadUShort();
            for (int i = 0; i < count; i++)
            {
                ushort id = reader.ReadUShort();
                byte[] payload = reader.ReadBytes();
                if (!variables.TryGetValue(id, out var variable))
                    throw new InvalidOperationException($"Missing variable {id} on {GetType().Name}; prefab versions differ");
                using (var valueReader = new NetReader(payload)) variable.Read(valueReader);
            }
            ReadExtraSnapshot(reader);
        }

        internal void ReadVariable(ushort id, byte[] payload)
        {
            if (!variables.TryGetValue(id, out var variable))
                throw new InvalidOperationException($"Missing variable {id} on {GetType().Name}");
            using (var reader = new NetReader(payload)) variable.Read(reader);
        }
    }

    internal enum RpcDestination : byte { Authority = 1, Observers = 2, Target = 3 }
}
