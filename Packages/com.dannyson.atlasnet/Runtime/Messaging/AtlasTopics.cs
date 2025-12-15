namespace AtlasNet.Messaging
{
    /// <summary>
    /// Common routing topics used by AtlasNet.
    /// These are stable ids, not strings, for performance.
    /// </summary>
    public static class AtlasTopics
    {
        /// <summary>
        /// Messages intended for the authoritative server.
        /// </summary>
        public const ulong Server = 1;
    }
}
