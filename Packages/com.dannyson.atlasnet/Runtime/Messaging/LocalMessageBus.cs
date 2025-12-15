using System;
using System.Collections.Generic;

namespace AtlasNet.Messaging
{
    /// <summary>
    /// Simple in-process message bus.
    /// Used for host mode, testing, and early development.
    /// </summary>
    public sealed class LocalMessageBus : IAtlasMessageBus
    {
        private readonly Dictionary<(Type type, ulong topic), List<Delegate>> _handlers = new();

        public void Publish<T>(ulong topic, in T message) where T : struct
        {
            var key = (typeof(T), topic);
            if (!_handlers.TryGetValue(key, out var list))
                return;

            // Copy to avoid modification during iteration
            var snapshot = list.ToArray();
            foreach (var handler in snapshot)
                ((Action<T>)handler).Invoke(message);
        }

        public IDisposable Subscribe<T>(ulong topic, Action<T> handler) where T : struct
        {
            var key = (typeof(T), topic);

            if (!_handlers.TryGetValue(key, out var list))
            {
                list = new List<Delegate>();
                _handlers[key] = list;
            }

            list.Add(handler);

            return new Subscription(() => list.Remove(handler));
        }

        private sealed class Subscription : IDisposable
        {
            private Action _dispose;

            public Subscription(Action dispose) => _dispose = dispose;

            public void Dispose()
            {
                _dispose?.Invoke();
                _dispose = null;
            }
        }
    }
}
