using AtlasNet.Messaging;
using AtlasNet.Rpc.Messages;
using UnityEngine;

namespace AtlasNet.Rpc
{
    internal static class ServerRpcDispatcher
    {
        public static void Initialize()
        {
            AtlasNetManager.MessageBus.Subscribe<ServerRpcMessage>(
                AtlasTopics.Server,
                OnServerRpc
            );
        }

        private static void OnServerRpc(ServerRpcMessage msg)
        {
            var allObjects = Object.FindObjectsOfType<NetObject>();
            foreach (var obj in allObjects)
            {
                if (obj.NetId != msg.NetId)
                    continue;

                var behaviours = obj.GetComponents<NetBehaviour>();
                foreach (var beh in behaviours)
                {
                    beh.HandleServerRpc(msg.RpcId);
                }
                return;
            }

            Debug.LogWarning($"[AtlasNet] NetObject {msg.NetId} not found.");
        }
    }
}
