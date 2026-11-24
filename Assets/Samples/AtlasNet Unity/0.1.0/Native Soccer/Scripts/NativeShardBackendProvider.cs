using AtlasNet;
using UnityEngine;

[CreateAssetMenu(menuName = "AtlasNet/Native Shard Backend")]
public sealed class NativeShardBackendProvider : WorldBackendProvider
{
    [SerializeField] private NetworkObject observerPrefab;

    public override IWorldBackend CreateBackend(NetworkManager runtime) => new NativeSoccerBackend(runtime, observerPrefab);
}
