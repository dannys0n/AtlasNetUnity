using UnityEngine;

namespace AtlasNet
{
    /// <summary>
    /// Base class for network-aware MonoBehaviours.
    /// Similar to NGO's NetworkBehaviour. For now, it just exposes role flags and NetObject access.
    /// </summary>
    public abstract class NetBehaviour : MonoBehaviour
    {
        private NetObject _netObject;

        /// <summary>The NetObject this behaviour belongs to.</summary>
        public NetObject NetObject => _netObject != null ? _netObject : (_netObject = GetComponent<NetObject>());

        /// <summary>Convenience: true if running as server.</summary>
        public bool IsServer => AtlasNetManager.IsServer;

        /// <summary>Convenience: true if running as client.</summary>
        public bool IsClient => AtlasNetManager.IsClient;

        /// <summary>Convenience: the local client id.</summary>
        public ulong LocalClientId => AtlasNetManager.LocalClientId;
    }
}
