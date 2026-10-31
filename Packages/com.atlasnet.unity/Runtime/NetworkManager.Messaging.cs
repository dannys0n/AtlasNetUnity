using System;
using System.Collections.Generic;

namespace AtlasNet
{
    public sealed partial class NetworkManager
    {
        public static readonly SessionId ServerSessionId = default;
        readonly List<SessionId> connectedClients = new();
        IReadOnlyList<SessionId> connectedClientsView;
        CustomMessagingManager customMessagingManager;

        /// <summary>Admitted gameplay clients, synchronized to clients as in NGO 2.13. Workers are excluded.</summary>
        public IReadOnlyList<SessionId> ConnectedClientsIds => connectedClientsView ??= connectedClients.AsReadOnly();

        /// <summary>Backend adapters update the admitted roster on Unity's main thread. This does not raise local connection callbacks for remote peers.</summary>
        public void UpdateConnectedClients(IReadOnlyList<SessionId> sessions)
        {
            if (sessions == null) throw new ArgumentNullException(nameof(sessions));
            var copy = new List<SessionId>(sessions);
            connectedClients.Clear(); connectedClients.AddRange(copy);
        }
        public bool IsConnectedClient => IsRunning && IsClient && LocalSession.Value != 0;
        public CustomMessagingManager CustomMessagingManager => customMessagingManager ??= new CustomMessagingManager(this);

        public string DisconnectReason { get; private set; } = string.Empty;
        public string LastTransportError { get; private set; } = string.Empty;
        public event Action OnTransportFailure;

        internal void ResetConnectionDiagnostics()
        { DisconnectReason = LastTransportError = string.Empty; }

        /// <summary>Backend adapters report a reason before raising disconnection callbacks.</summary>
        public void ReportDisconnectReason(string reason) => DisconnectReason = reason ?? string.Empty;

        /// <summary>Fatal adapter failure. Notify gameplay, then stop this session.</summary>
        public void ReportTransportFailure(string reason)
        {
            LastTransportError = reason ?? "Transport failed.";
            DisconnectReason = LastTransportError;
            try { OnTransportFailure?.Invoke(); }
            finally { if (IsRunning) Stop(); }
        }

        public event Action OnServerStarted;
        public event Action<bool> OnServerStopped;
        public event Action<bool> OnClientStopped;
        /// <summary>Server: admitted client. Client: local initial replica synchronization is complete.</summary>
        public event Action<SessionId> OnClientConnectedCallback;
        /// <summary>Also reports failed client connection attempts, for which no admitted session exists yet.</summary>
        public event Action<SessionId> OnClientDisconnectCallback;

        void HandleSessionJoined(SessionId session)
        {
            if (!connectedClients.Contains(session)) connectedClients.Add(session);
            SessionJoined?.Invoke(session);
            OnClientConnectedCallback?.Invoke(session);
        }

        void HandleSessionLeft(SessionId session)
        {
            bool admitted = connectedClients.Remove(session);
            if (admitted) SessionLeft?.Invoke(session);
            OnClientDisconnectCallback?.Invoke(session);
        }

        void HandleStopped(bool server, bool client)
        {
            connectedClients.Clear();
            customMessagingManager?.ClearHandlers();
            customMessagingManager = null;
            // NGO NetworkManager.ShutdownInternal notifies the host client before the server.
            if (client) OnClientStopped?.Invoke(server);
            if (server) OnServerStopped?.Invoke(client);
        }

        internal void ValidateNamedMessageRecipient(SessionId recipient) => Backend.ValidateNamedMessageRecipient(recipient);
        internal void SendNamedMessage(SessionId recipient, ulong id, byte[] payload, NetworkDelivery delivery)
            => Backend.SendNamedMessage(recipient, id, payload, delivery);

        /// <summary>Backend adapters call this on Unity's main thread after checking session admission.</summary>
        public void DispatchNamedMessage(SessionId sender, ulong messageId, byte[] payload)
        {
            if (payload == null || payload.Length > AtlasNet.CustomMessagingManager.MaxPayloadSize)
                throw new ArgumentException("Invalid named-message payload.", nameof(payload));
            CustomMessagingManager.Dispatch(sender, messageId, payload);
        }

        public void DisconnectClient(SessionId session, string reason = null) => Backend.DisconnectClient(session, reason);
    }
}
