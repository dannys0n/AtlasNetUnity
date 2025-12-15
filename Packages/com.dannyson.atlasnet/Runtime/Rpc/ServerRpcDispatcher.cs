using AtlasNet.Messaging;
using UnityEngine;

namespace AtlasNet.Rpc
{
    internal static class ServerRpcDispatcher
    {
        public static void Initialize()
        {
			      AtlasNetManager.MessageBus.Subscribe<Rpc.Messages.ServerRpcMessage<int>>(
					      AtlasTopics.Server,
					      OnServerRpcInt
			      );

				}

				private static void OnServerRpcInt(Rpc.Messages.ServerRpcMessage<int> msg)
				{
  					Dispatch(msg.NetId, msg.RpcId, msg.Payload);
				}

				private static void Dispatch<T>(ulong netId, ulong rpcId, T payload)
				{
						var allObjects = Object.FindObjectsOfType<NetObject>();
						foreach (var obj in allObjects)
						{
								if (obj.NetId != netId)
										continue;

								var behaviours = obj.GetComponents<NetBehaviour>();
								foreach (var beh in behaviours)
								{
										var method = RpcRegistry.Resolve(beh.GetType(), rpcId);
										if (method == null)
												continue;

										method.Invoke(beh, new object[] { payload });
										return;
								}
						}
				}
    }
}
