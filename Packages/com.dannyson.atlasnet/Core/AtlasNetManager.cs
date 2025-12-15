using UnityEngine;

namespace AtlasNet
{
    /// <summary>
    /// Global entry point for AtlasNet runtime state (role, ids, etc.).
    /// This is intentionally tiny for the first vertical slice.
    /// </summary>
    public static class AtlasNetManager
    {
        /// <summary>True if this process is acting as the server.</summary>
        public static bool IsServer { get; private set; }

        /// <summary>True if this process is acting as a client.</summary>
        public static bool IsClient { get; private set; }

        /// <summary>Local client id (0 is fine for now; we'll formalize later).</summary>
        public static ulong LocalClientId { get; private set; }

        /// <summary>
        /// Starts AtlasNet in a simple Host mode (server + client in same process).
        /// This will be useful for our first test without a real transport.
        /// </summary>
        public static void StartHost()
        {
            IsServer = true;
            IsClient = true;
            LocalClientId = 1;
            Debug.Log("[AtlasNet] Host started.");
        }

        /// <summary>Stops AtlasNet and clears flags.</summary>
        public static void Shutdown()
        {
            IsServer = false;
            IsClient = false;
            LocalClientId = 0;
            Debug.Log("[AtlasNet] Shutdown.");
        }
    }
}
