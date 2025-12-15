using System;
using System.Reflection;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Precomputed metadata + compiled invoke delegate for a ServerRpc.
	/// </summary>
	internal sealed class ServerRpcDescriptor
	{
		public readonly ulong RpcId;
		public readonly ParameterInfo[] Parameters;

		/// <summary>
		/// Fast invoke delegate. No reflection at runtime.
		/// </summary>
		public readonly Action<NetBehaviour, object[]> Invoke;

		public ServerRpcDescriptor(
				ulong rpcId,
				ParameterInfo[] parameters,
				Action<NetBehaviour, object[]> invoke)
		{
			RpcId = rpcId;
			Parameters = parameters;
			Invoke = invoke;
		}
	}
}
