namespace AtlasNet.Rpc.Messages
{
	/// <summary>
	/// Universal ServerRpc envelope carrying serialized payload bytes.
	/// </summary>
	public struct ServerRpcEnvelope
	{
		public ulong NetId;
		public ulong RpcId;

		/// <summary>
		/// Serialized payload bytes (empty for no-arg RPC).
		/// </summary>
		public byte[] PayloadBytes;
	}
}
