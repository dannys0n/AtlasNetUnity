using System;
using System.Collections.Generic;
using AtlasNet.Serialization;

namespace AtlasNet.Rpc
{
	/// <summary>
	/// Maps (BehaviourType, RpcId) to a strongly-typed handler that can deserialize and invoke.
	/// </summary>
	internal static class ServerRpcHandlerRegistry
	{
		private static readonly Dictionary<(Type type, ulong rpcId), Action<AtlasNet.NetBehaviour, byte[]>> _handlers = new();

		public static void Register<TBehaviour, TPayload>(ulong rpcId, Action<TBehaviour, TPayload> handler)
				where TBehaviour : AtlasNet.NetBehaviour
				where TPayload : struct
		{
			_handlers[(typeof(TBehaviour), rpcId)] = (beh, bytes) =>
			{
				var payload = BlittableSerializer.FromBytes<TPayload>(bytes);
				handler((TBehaviour)beh, payload);
			};
		}

		public static bool TryInvoke(Type behaviourType, ulong rpcId, AtlasNet.NetBehaviour instance, byte[] payloadBytes)
		{
			if (_handlers.TryGetValue((behaviourType, rpcId), out var action))
			{
				action(instance, payloadBytes);
				return true;
			}
			return false;
		}
	}
}
