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

        internal static bool WithinPhysicsRegion(Bounds region, Vector3 position, float bodyRadius,
            float padding, bool retained)
        {
            // Conservatively include a body's colliders before its root crosses the
            // region, and use a larger release area to prevent boundary ping-pong.
            region.Expand(2f * (bodyRadius + padding * (retained ? 2f : 1f)));
            return region.Contains(position);
        }
    }
}
