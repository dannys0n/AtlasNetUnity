using AtlasNet.Messaging;
using AtlasNet.Rpc.Messages;
using UnityEngine;

namespace AtlasNet.Rpc
{
    internal static class ServerRpcDispatcher
    {
        public static void Initialize()
        {
			      AtlasNetManager.MessageBus.Subscribe<ServerRpcEnvelope>(
					      AtlasTopics.Server,
					      OnServerRpc
			      );

				}
				private static void OnServerRpc(ServerRpcEnvelope msg)
				{
						Dispatch(msg);
				}


		private static void Dispatch(ServerRpcEnvelope msg)
		{
			// TEMP: scene scan. Later replaced with NetObject registry.
			var objects = Object.FindObjectsOfType<NetObject>();
			foreach (var obj in objects)
			{
				if (obj.NetId != msg.NetId)
					continue;

				var behaviours = obj.GetComponents<NetBehaviour>();
				foreach (var beh in behaviours)
				{
					// Try fast-path delegate dispatch
					if (ServerRpcHandlerRegistry.TryInvoke(
							beh.GetType(),
							msg.RpcId,
							beh,
							msg.PayloadBytes))
					{
						return;
					}
				}

				Debug.LogWarning(
						$"[AtlasNet] No ServerRpc handler found for RpcId {msg.RpcId} on NetObject {msg.NetId}");
				return;
			}

			Debug.LogWarning(
					$"[AtlasNet] NetObject {msg.NetId} not found for ServerRpc {msg.RpcId}");
		}
	}
}
