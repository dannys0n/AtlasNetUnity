using System;
using System.IO;

namespace AtlasNet
{
    internal sealed partial class LocalWorldBackend
    {
        public event Action ServerStarted;
        public event Action<bool, bool> Stopped;

        public void ValidateNamedMessageRecipient(SessionId recipient)
        {
            if (!IsRunning || isWorker) throw new InvalidOperationException("Named messages require a running gameplay server or client.");
            if (IsServer)
            {
                if (recipient == NetworkManager.ServerSessionId || (IsHost && recipient == LocalSession) || clientSessions.Contains(recipient)) return;
                throw new ArgumentException("The named-message recipient is not an admitted client.", nameof(recipient));
            }
            if (LocalSession.Value == 0) throw new InvalidOperationException("Wait for the client connection callback before sending named messages.");
            if (recipient != NetworkManager.ServerSessionId) throw new InvalidOperationException("Clients send named messages to the server; the server chooses any recipients for a relay.");
        }

        public void SendNamedMessage(SessionId recipient, ulong messageId, byte[] payload, NetworkDelivery delivery)
        {
            ValidateNamedMessageRecipient(recipient);
            if (IsServer && (recipient == NetworkManager.ServerSessionId || (IsHost && recipient == LocalSession)))
            {
                var sender = recipient == NetworkManager.ServerSessionId && IsHost ? LocalSession : NetworkManager.ServerSessionId;
                runtime.DispatchNamedMessage(sender, messageId, payload);
                return;
            }
            using var writer = new NetWriter();
            writer.Write((byte)Packet.NamedMessage);
            writer.Write(messageId);
            writer.WriteBytes(payload);
            var packet = writer.ToArray();
            // Delivery and sequencing are explicit per message name, independent of entity RPCs.
            if (IsServer) transport.SendTo(recipient, packet, delivery, messageId);
            else transport.SendToServer(packet, delivery, messageId);
        }

        void ReceiveNamedMessage(SessionId sender, NetReader reader)
        {
            if (isWorker || (IsServer ? !clientSessions.Contains(sender) : LocalSession.Value == 0 || sender != NetworkManager.ServerSessionId)) return;
            ulong id = reader.ReadULong();
            byte[] payload = reader.ReadBytes();
            if (reader.HasRemaining || payload.Length > CustomMessagingManager.MaxPayloadSize)
                throw new InvalidDataException("Invalid named-message payload.");
            runtime.DispatchNamedMessage(sender, id, payload);
        }

        void SendClientRoster(SessionId recipient, Packet type)
        {
            using var writer = new NetWriter();
            writer.Write((byte)type);
            writer.Write(clientSessions.Count + (IsHost ? 1 : 0));
            if (IsHost) writer.Write(LocalSession);
            foreach (var session in clientSessions) writer.Write(session);
            SendTo(recipient, writer.ToArray());
        }

        void BroadcastClientRoster(SessionId except = default)
        {
            foreach (var session in clientSessions)
                if (session != except) SendClientRoster(session, Packet.ClientRoster);
        }

        void ReceiveClientRoster(NetReader reader)
        {
            int count = reader.ReadInt();
            if (count < 0 || count > reader.Remaining / sizeof(ulong)) throw new InvalidDataException("Invalid client roster length.");
            var roster = new SessionId[count];
            var seen = new System.Collections.Generic.HashSet<SessionId>();
            for (int i = 0; i < count; i++)
            {
                roster[i] = reader.ReadSessionId();
                if (roster[i].Value == 0 || !seen.Add(roster[i])) throw new InvalidDataException("Invalid client roster entry.");
            }
            if (reader.HasRemaining || !seen.Contains(LocalSession)) throw new InvalidDataException("Invalid client roster snapshot.");
            runtime.UpdateConnectedClients(roster);
        }

        public void DisconnectClient(SessionId session)
        {
            if (!IsRunning || !IsServer || isWorker) throw new InvalidOperationException("Only the gameplay server can disconnect a client.");
            if (IsHost && session == LocalSession) throw new InvalidOperationException("Stop the host to disconnect its local player.");
            if (!clientSessions.Contains(session)) throw new ArgumentException("The session is not an admitted client.", nameof(session));
            transport.Disconnect(session);
        }
    }
}
