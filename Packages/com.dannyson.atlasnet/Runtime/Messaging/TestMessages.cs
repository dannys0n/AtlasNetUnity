namespace AtlasNet.Messaging
{
    /// <summary>
    /// Simple test message used to validate client → server flow.
    /// </summary>
    public struct PingServerMessage
    {
        public ulong FromClientId;
    }
}
