using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    // Local substitute for backend-supplied placement decisions. This is not the native ABI.
    internal interface ILocalAuthorityPlacement
    {
        void AddWorker(ulong id);
        void RemoveWorker(ulong id);
        ulong OwnerAt(float x, float z);
        bool ShouldMove(ulong current, float x, float z, float margin);
        bool IsWithinInterest(ulong worker, float x, float z, float radius);
    }

    // Optional diagnostics: production AtlasNet may provide no geometric boundary at all.
    internal interface IAuthorityRegionDebug
    {
        bool TryGetRegion(ulong worker, out Vector2[] vertices);
    }

    /// <summary>Development-only, bounded X/Z shard policy. Worker IDs are opaque to gameplay code.</summary>
    internal sealed class LocalVoronoi : ILocalAuthorityPlacement, IAuthorityRegionDebug
    {
        internal readonly struct Seed
        {
            public readonly float X;
            public readonly float Z;
            public Seed(float x, float z) { X = x; Z = z; }
        }

        private const int Samples = 32;
        private const int Candidates = 8;
        private readonly SortedDictionary<ulong, Seed> seeds = new SortedDictionary<ulong, Seed>();
        private readonly Dictionary<ulong, Seed[]> regions = new Dictionary<ulong, Seed[]>();
        private readonly float minX, maxX, minZ, maxZ;

        internal LocalVoronoi(float minX, float maxX, float minZ, float maxZ)
        {
            if (maxX <= minX || maxZ <= minZ) throw new ArgumentException("Local world bounds must have positive X and Z size");
            this.minX = minX; this.maxX = maxX; this.minZ = minZ; this.maxZ = maxZ;
        }

        internal int Count => seeds.Count;
        internal IReadOnlyDictionary<ulong, Seed> Seeds => seeds;

        internal void AddWorker(ulong id)
        {
            if (seeds.ContainsKey(id)) return;
            if (seeds.Count == 0)
            {
                seeds.Add(id, new Seed((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f));
                RebuildRegions();
                return;
            }

            var regionSizes = new Dictionary<ulong, int>();
            foreach (var existing in seeds.Keys) regionSizes.Add(existing, 0);
            for (int x = 0; x < Samples; x++)
                for (int z = 0; z < Samples; z++)
                    regionSizes[OwnerAt(SampleX(x), SampleZ(z))]++;
            ulong largest = 0;
            int largestSize = -1;
            foreach (var pair in regionSizes)
                if (pair.Value > largestSize) { largest = pair.Key; largestSize = pair.Value; }

            Seed best = default;
            float bestScore = float.MaxValue;
            for (int x = 0; x < Candidates; x++)
                for (int z = 0; z < Candidates; z++)
                {
                    var candidate = new Seed(minX + (x + 0.5f) * (maxX - minX) / Candidates,
                        minZ + (z + 0.5f) * (maxZ - minZ) / Candidates);
                    if (OwnerAt(candidate.X, candidate.Z) != largest) continue;
                    int claimed = 0, takenFromOtherRegions = 0;
                    for (int sx = 0; sx < Samples; sx++)
                        for (int sz = 0; sz < Samples; sz++)
                        {
                            float px = SampleX(sx), pz = SampleZ(sz);
                            ulong previous = OwnerAt(px, pz);
                            if (DistanceSquared(candidate, px, pz) >= DistanceSquared(seeds[previous], px, pz)) continue;
                            claimed++;
                            if (previous != largest) takenFromOtherRegions++;
                        }
                    float score = Math.Abs(claimed - largestSize * 0.5f) + takenFromOtherRegions * 2f;
                    if (score < bestScore) { bestScore = score; best = candidate; }
                }
            if (bestScore == float.MaxValue) throw new InvalidOperationException("Could not place a local worker seed");
            seeds.Add(id, best);
            RebuildRegions();
        }

        internal void RemoveWorker(ulong id)
        {
            if (seeds.Remove(id)) RebuildRegions();
        }

        void ILocalAuthorityPlacement.AddWorker(ulong id) => AddWorker(id);
        void ILocalAuthorityPlacement.RemoveWorker(ulong id) => RemoveWorker(id);
        ulong ILocalAuthorityPlacement.OwnerAt(float x, float z) => OwnerAt(x, z);
        bool ILocalAuthorityPlacement.ShouldMove(ulong current, float x, float z, float margin) =>
            ShouldMove(current, x, z, margin);
        bool ILocalAuthorityPlacement.IsWithinInterest(ulong worker, float x, float z, float radius) =>
            IsWithinInterest(worker, x, z, radius);

        public bool TryGetRegion(ulong worker, out Vector2[] vertices)
        {
            if (!seeds.ContainsKey(worker)) { vertices = Array.Empty<Vector2>(); return false; }
            var region = Region(worker);
            vertices = new Vector2[region.Length];
            for (int i = 0; i < region.Length; i++)
                vertices[i] = new Vector2(region[i].X, region[i].Z);
            return true;
        }

        internal ulong OwnerAt(float x, float z)
        {
            if (seeds.Count == 0) throw new InvalidOperationException("No local workers are registered");
            x = Math.Max(minX, Math.Min(maxX, x));
            z = Math.Max(minZ, Math.Min(maxZ, z));
            ulong owner = 0;
            float best = float.MaxValue;
            foreach (var pair in seeds)
            {
                float distance = DistanceSquared(pair.Value, x, z);
                if (distance < best) { best = distance; owner = pair.Key; }
            }
            return owner;
        }

        internal Seed[] Region(ulong id) => regions.TryGetValue(id, out var polygon) ? polygon : Array.Empty<Seed>();

        private void RebuildRegions()
        {
            regions.Clear();
            foreach (var id in seeds.Keys) regions.Add(id, BuildRegion(id));
        }

        private Seed[] BuildRegion(ulong id)
        {
            if (!seeds.TryGetValue(id, out var center)) return Array.Empty<Seed>();
            var polygon = new List<Seed>
            {
                new Seed(minX, minZ), new Seed(maxX, minZ),
                new Seed(maxX, maxZ), new Seed(minX, maxZ)
            };
            foreach (var pair in seeds)
            {
                if (pair.Key == id) continue;
                var other = pair.Value;
                float normalX = other.X - center.X, normalZ = other.Z - center.Z;
                float limit = (other.X * other.X + other.Z * other.Z -
                    center.X * center.X - center.Z * center.Z) * 0.5f;
                var clipped = new List<Seed>();
                for (int i = 0; i < polygon.Count; i++)
                {
                    var start = polygon[i];
                    var end = polygon[(i + 1) % polygon.Count];
                    float startSide = start.X * normalX + start.Z * normalZ - limit;
                    float endSide = end.X * normalX + end.Z * normalZ - limit;
                    if (startSide <= 0) clipped.Add(start);
                    if ((startSide < 0 && endSide > 0) || (startSide > 0 && endSide < 0))
                    {
                        float t = startSide / (startSide - endSide);
                        clipped.Add(new Seed(start.X + (end.X - start.X) * t,
                            start.Z + (end.Z - start.Z) * t));
                    }
                }
                polygon = clipped;
                if (polygon.Count == 0) break;
            }
            return polygon.ToArray();
        }

        internal bool IsWithinInterest(ulong worker, float x, float z, float radius)
        {
            if (!seeds.ContainsKey(worker)) return false;
            if (OwnerAt(x, z) == worker) return true;
            if (radius <= 0) return false;
            var polygon = Region(worker);
            float bestSquared = radius * radius;
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i];
                var b = polygon[(i + 1) % polygon.Length];
                float dx = b.X - a.X, dz = b.Z - a.Z;
                float lengthSquared = dx * dx + dz * dz;
                float t = lengthSquared > 0 ? ((x - a.X) * dx + (z - a.Z) * dz) / lengthSquared : 0;
                t = Math.Max(0, Math.Min(1, t));
                float offsetX = x - (a.X + t * dx), offsetZ = z - (a.Z + t * dz);
                if (offsetX * offsetX + offsetZ * offsetZ <= bestSquared) return true;
            }
            return false;
        }

        internal bool ShouldMove(ulong current, float x, float z, float margin)
        {
            if (!seeds.TryGetValue(current, out var existing)) return false;
            x = Math.Max(minX, Math.Min(maxX, x));
            z = Math.Max(minZ, Math.Min(maxZ, z));
            ulong next = OwnerAt(x, z);
            if (next == current) return false;
            float oldDistance = (float)Math.Sqrt(DistanceSquared(existing, x, z));
            float newDistance = (float)Math.Sqrt(DistanceSquared(seeds[next], x, z));
            return newDistance + Math.Max(0, margin) < oldDistance;
        }

        private float SampleX(int index) => minX + (index + 0.5f) * (maxX - minX) / Samples;
        private float SampleZ(int index) => minZ + (index + 0.5f) * (maxZ - minZ) / Samples;
        private static float DistanceSquared(Seed seed, float x, float z)
        {
            float dx = seed.X - x, dz = seed.Z - z;
            return dx * dx + dz * dz;
        }
    }
}
