using System;

namespace AtlasNet.Messaging
{
    /// <summary>
    /// Core publish/subscribe abstraction for AtlasNet.
    /// This is transport-agnostic and works in-process or over the network.
    /// </summary>
    public interface IAtlasMessageBus
    {
        /// <summary>
        /// Publishes a message to a topic.
        /// </summary>
        void Publish<T>(ulong topic, in T message) where T : struct;

        /// <summary>
        /// Subscribes a handler to a topic.
        /// </summary>
        IDisposable Subscribe<T>(ulong topic, Action<T> handler) where T : struct;
    }
}
