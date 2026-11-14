using AtlasNet;
using UnityEngine;

[CreateAssetMenu(menuName = "AtlasNet/Native Shard Backend")]
public sealed class NativeShardBackendProvider : WorldBackendProvider
{
    public override IWorldBackend CreateBackend(NetworkManager runtime) => new NativeShardBackend(runtime);
}
