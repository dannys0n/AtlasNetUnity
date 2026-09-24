using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    /// <summary>A reusable set of spawnable prefabs assigned to one or more NetworkManagers.</summary>
    [CreateAssetMenu(fileName = "NetworkPrefabsList", menuName = "AtlasNet/Network Prefabs List")]
    public sealed class NetworkPrefabsList : ScriptableObject
    {
        [SerializeField] private List<NetworkObject> prefabs = new List<NetworkObject>();

        public IReadOnlyList<NetworkObject> Prefabs => prefabs;
    }
}
