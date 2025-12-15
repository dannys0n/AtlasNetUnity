using System;
using System.Collections.Generic;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Maps (BehaviourType, RpcId) to a cached invocation delegate.
	/// The registry does NOT deserialize payloads; it only invokes handlers.
	/// </summary>
	internal static class ServerRpcHandlerRegistry
	{
		/// <summary>
		/// Key: (NetBehaviour concrete type, RpcId)
		/// Value: invocation delegate taking (instance, args[])
		/// </summary>
		private static readonly Dictionary<(Type type, ulong rpcId), Action<NetBehaviour, object[]>> _handlers
				= new();

		/// <summary>
		/// Registers a ServerRpc handler delegate.
		/// This is called once during startup (reflection / codegen phase).
		/// </summary>
		public static void Register<TBehaviour>(
				ulong rpcId,
				Action<TBehaviour, object[]> handler)
				where TBehaviour : NetBehaviour
		{
			_handlers[(typeof(TBehaviour), rpcId)] = (beh, args) =>
			{
				handler((TBehaviour)beh, args);
			};
		}

		/// <summary>
		/// Attempts to invoke a registered ServerRpc handler.
		/// Returns true if a handler was found and executed.
		/// </summary>
		public static bool TryInvoke(
				Type behaviourType,
				ulong rpcId,
				NetBehaviour instance,
				object[] args)
		{
			if (_handlers.TryGetValue((behaviourType, rpcId), out var action))
			{
				action(instance, args);
				return true;
			}

			return false;
		}
	}
}
