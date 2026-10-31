using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>Session messages independent of spawned entities. Register handlers per session; shutdown clears registrations.</summary>
    public sealed class CustomMessagingManager
    {
        // Leave room for the backend envelope inside the built-in 64 KiB fragmentation pipeline.
        public const int MaxPayloadSize = 60 * 1024;
        public delegate void HandleNamedMessageDelegate(SessionId sender, NetReader reader);
        readonly NetworkManager manager;
        readonly Dictionary<ulong, HandleNamedMessageDelegate> handlers = new();
        readonly Dictionary<string, ulong> ids = new(StringComparer.Ordinal);
        readonly Dictionary<ulong, string> names = new();

        internal CustomMessagingManager(NetworkManager manager) => this.manager = manager;

        ulong MessageId(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("A message name is required.", nameof(name));
            if (ids.TryGetValue(name, out var existing)) return existing;
            var bytes = Encoding.UTF8.GetBytes(name);
            ulong id = 14695981039346656037UL;
            foreach (byte value in bytes) id = unchecked((id ^ value) * 1099511628211UL);
            if (names.TryGetValue(id, out var other) && other != name)
                throw new InvalidOperationException("Named-message hash collision: " + name + " and " + other);
            ids.Add(name, id); names[id] = name;
            return id;
        }

        public void RegisterNamedMessageHandler(string name, HandleNamedMessageDelegate callback)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (manager.ShouldLog(LogType.Error)) manager.Log(LogType.Error, "[RegisterNamedMessageHandler] Cannot register a named message of type null or empty!");
                return;
            }
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            handlers[MessageId(name)] = callback;
        }

        public void UnregisterNamedMessageHandler(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                if (manager.ShouldLog(LogType.Error)) manager.Log(LogType.Error, "[UnregisterNamedMessageHandler] Cannot unregister a named message of type null or empty!");
                return;
            }
            ulong id = MessageId(name);
            handlers.Remove(id); ids.Remove(name); names.Remove(id);
        }

        internal void ClearHandlers()
        {
            handlers.Clear(); ids.Clear(); names.Clear();
        }

        public void SendNamedMessage(string name, SessionId recipient, NetWriter writer,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            SendNamedMessage(name, recipient, writer.ToArray(), delivery);
        }

        public void SendNamedMessage(string name, SessionId recipient, byte[] payload,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
            => manager.SendNamedMessage(recipient, MessageId(name), ValidatePayload(payload, delivery), delivery);

        public void SendNamedMessage(string name, IReadOnlyList<SessionId> recipients, NetWriter writer,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            SendNamedMessage(name, recipients, writer.ToArray(), delivery);
        }

        public void SendNamedMessage(string name, IReadOnlyList<SessionId> recipients, byte[] payload,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
        {
            if (recipients == null) throw new ArgumentNullException(nameof(recipients));
            ulong id = MessageId(name); ValidatePayload(payload, delivery);
            // A local handler can disconnect a recipient or change its caller's list.
            var targets = new List<SessionId>(recipients);
            foreach (var recipient in targets) manager.ValidateNamedMessageRecipient(recipient);
            foreach (var recipient in targets) manager.SendNamedMessage(recipient, id, payload, delivery);
        }

        public void SendNamedMessageToAll(string name, NetWriter writer,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            SendNamedMessageToAll(name, writer.ToArray(), delivery);
        }

        public void SendNamedMessageToAll(string name, byte[] payload,
            NetworkDelivery delivery = NetworkDelivery.ReliableOrdered)
        {
            if (!manager.IsServer) throw new InvalidOperationException("Only the gameplay server can broadcast named messages.");
            SendNamedMessage(name, manager.ConnectedClientsIds, payload, delivery);
        }

        static byte[] ValidatePayload(byte[] payload, NetworkDelivery delivery)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MaxPayloadSize) throw new ArgumentException("Named messages support at most " + MaxPayloadSize + " payload bytes.", nameof(payload));
            if (delivery < NetworkDelivery.ReliableOrdered || delivery > NetworkDelivery.UnreliableSequenced)
                throw new ArgumentOutOfRangeException(nameof(delivery));
            return payload;
        }

        internal void Dispatch(SessionId sender, ulong id, byte[] payload)
        {
            if (!handlers.TryGetValue(id, out var callback)) return;
            using var reader = new NetReader(payload);
            callback(sender, reader);
        }
    }
}
