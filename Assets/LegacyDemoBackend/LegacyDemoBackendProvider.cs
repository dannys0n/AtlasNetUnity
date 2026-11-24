using UnityEngine;

namespace AtlasNet
{
    // Preserves the repository's historical M2 demo; excluded from UPM.
    public sealed class LegacyDemoBackendProvider : WorldBackendProvider
    {
        public override IWorldBackend CreateBackend(NetworkManager manager) => new LocalWorldBackend(manager);
    }
}
