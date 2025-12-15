namespace AtlasNet.Rpc
{
    /// <summary>
    /// Stable identifier for an RPC method.
    /// In the future this will be generated deterministically.
    /// </summary>
    public readonly struct RpcMethodId
    {
        public readonly ulong Value;

        public RpcMethodId(ulong value)
        {
            Value = value;
        }
    }
}
