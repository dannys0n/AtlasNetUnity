using System;
using System.Collections.Generic;
using System.Reflection;

namespace AtlasNet
{
    internal interface INetworkVariable
    {
        NetworkVariableReadPermission ReadPermission { get; }
        NetworkVariableWritePermission WritePermission { get; }
        void Write(NetWriter writer);
        void Read(NetReader reader);
    }

    public enum NetworkVariableReadPermission { Everyone, Owner }
    public enum NetworkVariableWritePermission { Server, Owner }

    /// <summary>A persistent value. The server keeps its current value for new observers.</summary>
    public sealed class NetworkVariable<T> : INetworkVariable
    {
        private NetworkBehaviour behaviour;
        private ushort id;
        private T value;
        public NetworkVariableReadPermission ReadPermission { get; }
        public NetworkVariableWritePermission WritePermission { get; }
        public T Value
        {
            get
            {
                if (behaviour != null && behaviour.IsSpawned && ReadPermission == NetworkVariableReadPermission.Owner &&
                    !behaviour.HasAuthority && !behaviour.IsOwner)
                    throw new InvalidOperationException($"Only the owner can read {behaviour.GetType().Name} variable {id}");
                return value;
            }
            set => Set(value);
        }
        public event Action<T, T> Changed;
        public event Action<T, T> OnValueChanged
        {
            add => Changed += value;
            remove => Changed -= value;
        }

        public NetworkVariable() : this(default) { }
        public NetworkVariable(T initial, NetworkVariableReadPermission readPerm = NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission writePerm = NetworkVariableWritePermission.Server)
        {
            value = initial;
            ReadPermission = readPerm;
            WritePermission = writePerm;
        }

        internal NetworkVariable(NetworkBehaviour behaviour, ushort id, T initial)
        {
            value = initial;
            ReadPermission = NetworkVariableReadPermission.Everyone;
            WritePermission = NetworkVariableWritePermission.Server;
            Bind(behaviour, id);
        }

        internal void Bind(NetworkBehaviour owner, ushort variableId)
        {
            if (behaviour != null && behaviour != owner)
                throw new InvalidOperationException("A NetworkVariable cannot be shared by multiple behaviours");
            behaviour = owner;
            id = variableId;
        }

        public void Set(T next)
        {
            if (behaviour == null || behaviour.NetworkObject == null)
                throw new InvalidOperationException("NetworkVariable is not attached to a spawned NetworkBehaviour");
            bool allowed = WritePermission == NetworkVariableWritePermission.Server
                ? behaviour.HasAuthority : behaviour.IsOwner;
            if (!allowed)
                throw new InvalidOperationException($"Only {WritePermission} can write {behaviour.GetType().Name} variable {id}");
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

    /// <summary>Attach to the same GameObject as NetworkObject.</summary>
    public abstract class NetworkBehaviour : UnityEngine.MonoBehaviour
    {
        private readonly Dictionary<ushort, INetworkVariable> variables = new Dictionary<ushort, INetworkVariable>();
        private readonly Dictionary<uint, MethodInfo> rpcMethods = new Dictionary<uint, MethodInfo>();
        private string pendingRpcBody;
        public NetworkObject NetworkObject { get; private set; }
        public NetworkManager NetworkManager => NetworkObject?.Manager;
        public bool IsSpawned => NetworkObject != null && NetworkObject.IsSpawned;
        public bool IsServer => NetworkObject != null && NetworkObject.Manager.IsServer;
        public bool IsClient => NetworkObject != null && NetworkObject.Manager.IsClient;
        public bool IsHost => IsServer && IsClient;
        public bool HasAuthority => NetworkObject != null && NetworkObject.HasAuthority;
        public bool IsOwner => NetworkObject != null && NetworkObject.IsOwner;
        public SessionId OwnerSession => NetworkObject.OwnerSession;
        internal byte BehaviourIndex { get; private set; }

        internal void Initialize(NetworkObject networkObject, byte index)
        {
            NetworkObject = networkObject;
            BehaviourIndex = index;
            BindDeclaredVariables();
            BindRpcMethods();
        }

        private void BindDeclaredVariables()
        {
            var fields = GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            ushort id = 1;
            foreach (var field in fields)
            {
                if (!field.FieldType.IsGenericType || field.FieldType.GetGenericTypeDefinition() != typeof(NetworkVariable<>)) continue;
                if (field.GetValue(this) is not INetworkVariable variable)
                    throw new InvalidOperationException($"Initialize NetworkVariable field {GetType().Name}.{field.Name} when declaring it");
                if (variables.ContainsKey(id)) throw new InvalidOperationException($"Duplicate variable ID {id}");
                field.FieldType.GetMethod("Bind", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(variable, new object[] { this, id });
                variables.Add(id++, variable);
            }
        }

        private void BindRpcMethods()
        {
            foreach (var method in GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var target = RpcMethods.TargetOf(method);
                if (target == null) continue;
                RpcMethods.Validate(method, target.Value);
                uint id = RpcMethods.IdOf(method);
                if (rpcMethods.ContainsKey(id)) throw new InvalidOperationException($"RPC ID collision on {GetType().Name}.{method.Name}");
                rpcMethods.Add(id, method);
            }
        }

        protected NetworkVariable<T> RegisterVariable<T>(ushort id, T initial)
        {
            if (variables.ContainsKey(id)) throw new InvalidOperationException($"Duplicate variable ID {id}");
            var variable = new NetworkVariable<T>(this, id, initial);
            variables.Add(id, variable);
            return variable;
        }

        /// <summary>Low-level RPC send helper. Gameplay code normally calls an attributed RPC method directly.</summary>
        protected void SendRpc(string method, params object[] arguments) => SendNamedRpc(default, method, arguments, false);
        protected void SendRpc(SessionId target, string method, params object[] arguments) => SendNamedRpc(target, method, arguments, true);

        private void SendNamedRpc(SessionId target, string name, object[] arguments, bool hasExplicitTarget)
        {
            if (NetworkObject == null || !NetworkObject.IsSpawned)
                throw new InvalidOperationException($"Cannot send {name} before {GetType().Name} is spawned");
            if (arguments == null) arguments = Array.Empty<object>();
            MethodInfo match = null;
            foreach (var method in rpcMethods.Values)
                if (method.Name == name)
                {
                    if (match != null) throw new InvalidOperationException($"Overloaded RPC name {name} is ambiguous");
                    match = method;
                }
            if (match == null) throw new InvalidOperationException($"No [Rpc] method named {name} on {GetType().Name}");
            SendTo sendTo = RpcMethods.TargetOf(match).Value;
            if (hasExplicitTarget != (sendTo == SendTo.SpecifiedInParams))
                throw new InvalidOperationException($"RPC {name} requires {(sendTo == SendTo.SpecifiedInParams ? "an explicit target" : "no explicit target")}");
            RpcDestination destination = RpcMethods.DestinationOf(sendTo);
            if (sendTo == SendTo.Owner) target = OwnerSession;
            var parameters = match.GetParameters();
            int firstPayloadParameter = sendTo == SendTo.SpecifiedInParams ? 1 : 0;
            int count = parameters.Length - firstPayloadParameter - (destination == RpcDestination.Authority && parameters.Length > 0 && parameters[parameters.Length - 1].ParameterType == typeof(SessionId) ? 1 : 0);
            if (arguments.Length != count) throw new ArgumentException($"{name} expects {count} RPC arguments");
            NetworkObject.Manager.SendRpc(this, destination, target, RpcMethods.IdOf(match), writer =>
            {
                for (int i = 0; i < count; i++) RpcMethods.Write(writer, parameters[i + firstPayloadParameter].ParameterType, arguments[i]);
            });
        }

        /// <summary>Used by generated RPC entry code to run a received method body without sending it again.</summary>
        protected bool EnterReceivedRpc(string method)
        {
            if (pendingRpcBody != method) return false;
            pendingRpcBody = null;
            return true;
        }

        public virtual void OnNetworkSpawn() { }
        public virtual void OnNetworkDespawn() { }
        public virtual void OnNetworkTick() { }
        /// <summary>Called after a committed worker-authority change for an existing entity.</summary>
        public virtual void OnSimulationAuthorityChanged() { }
        protected virtual void WriteExtraSnapshot(NetWriter writer) { }
        protected virtual void ReadExtraSnapshot(NetReader reader) { }
        /// <summary>Optional simulation-only state transferred between local workers, not sent to observers.</summary>
        protected virtual void WriteHandoffState(NetWriter writer) { }
        protected virtual void ReadHandoffState(NetReader reader) { }
        internal void WriteHandoff(NetWriter writer) => WriteHandoffState(writer);
        internal void ReadHandoff(NetReader reader) => ReadHandoffState(reader);

        internal void ReceiveRpc(uint method, byte[] payload, SessionId sender, RpcDestination destination, SessionId target)
        {
            if (!rpcMethods.TryGetValue(method, out var handler))
                throw new InvalidOperationException($"Unknown RPC {method} on {GetType().Name}; prefab versions may differ");
            if (RpcMethods.DestinationOf(RpcMethods.TargetOf(handler).Value) != destination)
                throw new InvalidOperationException($"RPC {handler.Name} cannot be received as {destination}");
            var parameters = handler.GetParameters();
            var arguments = new object[parameters.Length];
            bool hasTarget = RpcMethods.TargetOf(handler) == SendTo.SpecifiedInParams;
            bool hasSender = destination == RpcDestination.Authority &&
                parameters.Length > 0 && parameters[parameters.Length - 1].ParameterType == typeof(SessionId);
            using (var reader = new NetReader(payload))
            {
                if (hasTarget) arguments[0] = target;
                int firstPayloadParameter = hasTarget ? 1 : 0;
                int count = parameters.Length - firstPayloadParameter - (hasSender ? 1 : 0);
                for (int i = 0; i < count; i++) arguments[i + firstPayloadParameter] = RpcMethods.Read(reader, parameters[i + firstPayloadParameter].ParameterType);
                if (reader.HasRemaining) throw new InvalidOperationException($"Extra RPC payload for {handler.Name}");
            }
            if (hasSender) arguments[arguments.Length - 1] = sender;
            string previous = pendingRpcBody;
            pendingRpcBody = handler.Name;
            try { handler.Invoke(this, arguments); }
            finally { pendingRpcBody = previous; }
        }

        internal void WriteSnapshot(NetWriter writer)
        {
            WriteSnapshot(writer, default, false);
        }

        internal void WriteSnapshot(NetWriter writer, SessionId reader, bool filterPrivate)
        {
            ushort count = 0;
            foreach (var pair in variables)
                if (!filterPrivate || pair.Value.ReadPermission == NetworkVariableReadPermission.Everyone || reader == OwnerSession)
                    count++;
            writer.Write(count);
            foreach (var pair in variables)
            {
                if (filterPrivate && pair.Value.ReadPermission == NetworkVariableReadPermission.Owner && reader != OwnerSession)
                    continue;
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
            using (var reader = new NetReader(payload))
            {
                variable.Read(reader);
                if (reader.HasRemaining)
                    throw new InvalidOperationException($"Extra variable data for {GetType().Name} variable {id}");
            }
        }

        internal INetworkVariable GetVariable(ushort id)
        {
            if (!variables.TryGetValue(id, out var variable))
                throw new InvalidOperationException($"Missing variable {id} on {GetType().Name}");
            return variable;
        }

        internal RpcInvokePermission GetRpcPermission(uint id)
        {
            if (!rpcMethods.TryGetValue(id, out var method))
                throw new InvalidOperationException($"Unknown RPC {id} on {GetType().Name}");
            return method.GetCustomAttribute<RpcAttribute>().InvokePermission;
        }

        internal RpcDestination GetRpcDestination(uint id)
        {
            if (!rpcMethods.TryGetValue(id, out var method))
                throw new InvalidOperationException($"Unknown RPC {id} on {GetType().Name}");
            return RpcMethods.DestinationOf(RpcMethods.TargetOf(method).Value);
        }
    }

    internal enum RpcDestination : byte { Authority = 1, Observers = 2, Target = 3, Everyone = 4 }

    public enum SendTo { Authority, Observers, Owner, SpecifiedInParams, Everyone }
    public enum RpcInvokePermission { Server, Owner, Everyone }

    /// <summary>Marks an RPC. The IL post-processor turns calls into sends.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RpcAttribute : Attribute
    {
        public SendTo Target { get; }
        public RpcInvokePermission InvokePermission { get; set; } = RpcInvokePermission.Everyone;
        public RpcAttribute(SendTo target) => Target = target;
    }

    internal static class RpcMethods
    {
        internal static void Validate(MethodInfo method, SendTo target)
        {
            if (method.IsStatic || method.ReturnType != typeof(void) || method.IsGenericMethod)
                throw new InvalidOperationException($"RPC method {method.Name} must be an instance non-generic void method");
            if (!method.Name.EndsWith("Rpc", StringComparison.Ordinal))
                throw new InvalidOperationException($"RPC method {method.Name} must end with 'Rpc'");
            var parameters = method.GetParameters();
            if (target == SendTo.SpecifiedInParams &&
                (parameters.Length == 0 || parameters[0].ParameterType != typeof(SessionId)))
                throw new InvalidOperationException($"RPC {method.Name} needs a first SessionId target parameter");
            for (int i = 0; i < parameters.Length; i++)
            {
                Type type = parameters[i].ParameterType;
                if (target == SendTo.Authority && i == parameters.Length - 1 && type == typeof(SessionId)) continue;
                if (target == SendTo.SpecifiedInParams && i == 0) continue;
                if (type == typeof(int) || type == typeof(float) || type == typeof(bool) ||
                    type == typeof(string) || type == typeof(UnityEngine.Vector2) ||
                    type == typeof(UnityEngine.Vector3) || type == typeof(UnityEngine.Quaternion)) continue;
                throw new NotSupportedException($"RPC {method.Name} has unsupported parameter {parameters[i].Name} ({type.Name})");
            }
        }

        internal static SendTo? TargetOf(MethodInfo method)
        {
            return method.GetCustomAttribute<RpcAttribute>()?.Target;
        }

        internal static RpcDestination DestinationOf(SendTo target) => target switch
        {
            SendTo.Authority => RpcDestination.Authority,
            SendTo.Observers => RpcDestination.Observers,
            SendTo.Owner => RpcDestination.Target,
            SendTo.SpecifiedInParams => RpcDestination.Target,
            SendTo.Everyone => RpcDestination.Everyone,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        internal static uint IdOf(MethodInfo method)
        {
            if (method.ReturnType != typeof(void) || method.IsGenericMethod)
                throw new InvalidOperationException($"RPC {method.Name} must be a non-generic void method");
            string signature = method.DeclaringType.FullName + "." + method.Name;
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.IsOut || parameter.ParameterType.IsByRef)
                    throw new InvalidOperationException($"RPC {method.Name} cannot have ref or out parameters");
                signature += ":" + parameter.ParameterType.FullName;
            }
            uint hash = 2166136261;
            foreach (char letter in signature) hash = unchecked((hash ^ letter) * 16777619);
            return hash | 0x80000000;
        }

        internal static void Write(NetWriter writer, Type type, object value)
        {
            if (type == typeof(int)) writer.Write((int)value);
            else if (type == typeof(float)) writer.Write((float)value);
            else if (type == typeof(bool)) writer.Write((bool)value);
            else if (type == typeof(string)) writer.Write((string)value);
            else if (type == typeof(UnityEngine.Vector2)) { var v = (UnityEngine.Vector2)value; writer.Write(v.x); writer.Write(v.y); }
            else if (type == typeof(UnityEngine.Vector3)) writer.Write((UnityEngine.Vector3)value);
            else if (type == typeof(UnityEngine.Quaternion)) writer.Write((UnityEngine.Quaternion)value);
            else throw new NotSupportedException($"RPC parameter type {type.Name} is not supported");
        }

        internal static object Read(NetReader reader, Type type)
        {
            if (type == typeof(int)) return reader.ReadInt();
            if (type == typeof(float)) return reader.ReadFloat();
            if (type == typeof(bool)) return reader.ReadBool();
            if (type == typeof(string)) return reader.ReadString();
            if (type == typeof(UnityEngine.Vector2)) return new UnityEngine.Vector2(reader.ReadFloat(), reader.ReadFloat());
            if (type == typeof(UnityEngine.Vector3)) return reader.ReadVector3();
            if (type == typeof(UnityEngine.Quaternion)) return reader.ReadQuaternion();
            throw new NotSupportedException($"RPC parameter type {type.Name} is not supported");
        }
    }
}
