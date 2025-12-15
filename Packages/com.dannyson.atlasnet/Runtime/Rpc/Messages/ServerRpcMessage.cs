namespace AtlasNet.Rpc.Messages
{
    /// <summary>
    /// Generic ServerRpc message carrying parameters.
    /// </summary>
    public struct ServerRpcMessage<T> where T : struct
    {
        public ulong NetId;
        public ulong RpcId;
        public T Payload;
    }
}
