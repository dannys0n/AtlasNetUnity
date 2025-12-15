using System;
using System.Collections.Generic;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Holds all ServerRpc descriptors for a single NetBehaviour type.
	/// </summary>
	internal sealed class ServerRpcTable
	{
		private readonly Dictionary<ulong, ServerRpcDescriptor> _rpcs = new();

		public void Add(ServerRpcDescriptor descriptor)
		{
			_rpcs[descriptor.RpcId] = descriptor;
		}

		public bool TryGet(ulong rpcId, out ServerRpcDescriptor descriptor)
		{
			return _rpcs.TryGetValue(rpcId, out descriptor);
		}
	}
}
