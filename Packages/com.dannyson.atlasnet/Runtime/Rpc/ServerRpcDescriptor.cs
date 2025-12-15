using System;
using System.Reflection;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Precomputed metadata + invoke delegate for a ServerRpc.
	/// </summary>
	internal sealed class ServerRpcDescriptor
	{
		public readonly ulong RpcId;
		public readonly ParameterInfo[] Parameters;
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
