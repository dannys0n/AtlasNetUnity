using AtlasNet.Rpc;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>
    /// Base class for network-aware MonoBehaviours.
    /// Similar to NGO's NetworkBehaviour. For now, it just exposes role flags and NetObject access.
    /// </summary>
    public abstract class NetBehaviour : MonoBehaviour, IServerRpcHandler
    {
        private NetObject _netObject;

        /// <summary>The NetObject this behaviour belongs to.</summary>
        public NetObject NetObject => _netObject != null ? _netObject : (_netObject = GetComponent<NetObject>());

        /// <summary>Convenience: true if running as server.</summary>
        public bool IsServer => AtlasNetManager.IsServer;

        /// <summary>Convenience: true if running as client.</summary>
        public bool IsClient => AtlasNetManager.IsClient;

        /// <summary>Convenience: the local client id.</summary>
        public ulong LocalClientId => AtlasNetManager.LocalClientId;

        protected virtual void Awake()
        {
            RpcRegistry.Register(GetType());
        }

		    /// <summary>
		    /// Dispatches a ServerRpc by id.
		    /// </summary>
		    public virtual void HandleServerRpc(ulong rpcId)
		    {
			      var method = RpcRegistry.Resolve(GetType(), rpcId);
			      if (method == null)
				      return;

			      method.Invoke(this, null);
		    }

		/// <summary>
		/// Emits a ServerRpc message with parameters.
		/// </summary>
		protected void SendServerRpc<T>(
				T payload,
				[System.Runtime.CompilerServices.CallerMemberName] string methodName = null
		) where T : struct
		{
			var method = GetType().GetMethod(
					methodName,
					System.Reflection.BindingFlags.Instance |
					System.Reflection.BindingFlags.Public |
					System.Reflection.BindingFlags.NonPublic);

			if (method == null)
				return;

			var rpcId = RpcRegistry.GetRpcId(method);
			if (rpcId == 0)
				return;

			var bytes = AtlasNet.Serialization.BlittableSerializer.ToBytes(payload);

			AtlasNetManager.MessageBus.Publish(
					Messaging.AtlasTopics.Server,
					new Rpc.Messages.ServerRpcEnvelope
					{
						NetId = NetObject.NetId,
						RpcId = rpcId,
						PayloadBytes = bytes
					}
			);
		}
	}
}
