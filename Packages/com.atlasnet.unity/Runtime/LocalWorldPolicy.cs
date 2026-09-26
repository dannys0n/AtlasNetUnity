using UnityEngine;

namespace AtlasNet
{
    // Development-only distance helper. The future AtlasNet bridge receives
    // authority/residency decisions; it must not reproduce this policy.
    internal static class LocalWorldPolicy
    {
        internal static bool WithinRadius(Vector3 center, Vector3 target, float radius)
        {
            float dx = target.x - center.x, dz = target.z - center.z;
            return dx * dx + dz * dz <= radius * radius;
        }
    }
}
