namespace AtlasNet.Rpc
{
    /// <summary>
    /// Implemented by NetBehaviours that can receive ServerRpc messages.
    /// </summary>
    internal interface IServerRpcHandler
    {
        void HandleServerRpc(ulong rpcId);
    }
}
