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
			var objects = UnityEngine.Object.FindObjectsOfType<NetObject>();
			foreach (var obj in objects)
			{
				if (obj.NetId != msg.NetId)
					continue;

				var behaviours = obj.GetComponents<NetBehaviour>();
				foreach (var beh in behaviours)
				{
					if (!RpcRegistry.TryGetDescriptor(
							beh.GetType(),
							msg.RpcId,
							out var descriptor))
						continue;

					object[] args = null;

					if (descriptor.Parameters.Length > 0)
					{
						args = ServerRpcPayloadPacker.Unpack(
								msg.PayloadBytes,
								descriptor.Parameters);
					}

					descriptor.Invoke(beh, args);
					return;
				}
			}

			UnityEngine.Debug.LogWarning(
					$"[AtlasNet] Failed to dispatch ServerRpc {msg.RpcId} for NetObject {msg.NetId}");
		}

	}
}
