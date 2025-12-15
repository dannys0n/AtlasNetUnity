using UnityEngine;

namespace AtlasNet
{
    /// <summary>
    /// Identifies a networked entity. Similar role to NGO's NetworkObject,
    /// but we will keep this minimal until messaging is wired.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NetObject : MonoBehaviour
    {
        /// <summary>Unique network identity for this object (assigned later by spawn system).</summary>
        [field: SerializeField]
        public ulong NetId { get; private set; }

        /// <summary>Client that owns this object (authority rules come later).</summary>
        [field: SerializeField]
        public ulong OwnerClientId { get; private set; }

        /// <summary>
        /// Temporary helper for early testing. In a real system, NetId comes from a spawn manager.
        /// </summary>
        public void DebugSetIdentity(ulong netId, ulong ownerClientId)
        {
            NetId = netId;
            OwnerClientId = ownerClientId;
        }
    }
}
