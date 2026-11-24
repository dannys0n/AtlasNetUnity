using System;
using System.Collections.Generic;
using UnityEngine;

namespace AtlasNet
{
    // Placement decisions belong to the dummy backend, not sample controllers.
    internal interface ILocalAuthorityPlacement
    {
        void AddWorker(ulong id);
        void RemoveWorker(ulong id);
        ulong OwnerAt(float x, float z);
        bool ShouldMove(ulong current, float x, float z, float margin);
        bool IsWithinInterest(ulong worker, float x, float z, float radius);
    }

    internal interface IAuthorityRegionDebug
    {
        bool TryGetRegion(ulong worker, out Vector2[] vertices);
    }

    /// <summary>Unbounded, equal-angle Voronoi cells. Only diagnostic polygons are clipped.</summary>
    internal sealed class LocalVoronoi : ILocalAuthorityPlacement, IAuthorityRegionDebug
    {
        internal readonly struct Seed
        {
            public readonly float X, Z;
            public Seed(float x, float z) { X = x; Z = z; }
        }
        private readonly SortedDictionary<ulong, Seed> seeds = new SortedDictionary<ulong, Seed>();
        private readonly Dictionary<ulong, Seed[]> regions = new Dictionary<ulong, Seed[]>();
        private readonly float minX, maxX, minZ, maxZ, centerX, centerZ;
        internal LocalVoronoi(Vector2 center, float debugExtent)
            : this(center.x - debugExtent, center.x + debugExtent, center.y - debugExtent, center.y + debugExtent) { }
        // This rectangle is a diagnostic crop, never an ownership boundary.
        internal LocalVoronoi(float minX, float maxX, float minZ, float maxZ)
        {
            if (!(maxX > minX) || !(maxZ > minZ)) throw new ArgumentException("Debug bounds must have positive size");
            this.minX = minX; this.maxX = maxX; this.minZ = minZ; this.maxZ = maxZ;
            centerX = (minX + maxX) * .5f; centerZ = (minZ + maxZ) * .5f;
        }
        internal int Count => seeds.Count;
        internal IReadOnlyDictionary<ulong, Seed> Seeds => seeds;
        internal void AddWorker(ulong id)
        {
            if (seeds.ContainsKey(id)) return;
            seeds.Add(id, default); Rebalance();
        }
        internal void RemoveWorker(ulong id) { if (seeds.Remove(id)) Rebalance(); }
        private void Rebalance()
        {
            var ids = new List<ulong>(seeds.Keys);
            // Equal-radius, equally spaced sites produce equal angular shares at every distance.
            // Sorted IDs provide deterministic tie-breaking, independent of join order.
            for (int i = 0; i < ids.Count; i++)
            {
                double angle = Math.PI + (ids.Count == 2 ? 0 : Math.PI / ids.Count) + i * Math.PI * 2 / ids.Count;
                seeds[ids[i]] = ids.Count == 1 ? new Seed(centerX, centerZ)
                    : new Seed(centerX + (float)(Math.Cos(angle) * 8), centerZ + (float)(Math.Sin(angle) * 8));
            }
            regions.Clear(); foreach (var id in ids) regions.Add(id, BuildRegion(id));
        }
        void ILocalAuthorityPlacement.AddWorker(ulong id) => AddWorker(id);
        void ILocalAuthorityPlacement.RemoveWorker(ulong id) => RemoveWorker(id);
        ulong ILocalAuthorityPlacement.OwnerAt(float x, float z) => OwnerAt(x, z);
        bool ILocalAuthorityPlacement.ShouldMove(ulong current, float x, float z, float margin) => ShouldMove(current, x, z, margin);
        bool ILocalAuthorityPlacement.IsWithinInterest(ulong worker, float x, float z, float radius) => IsWithinInterest(worker, x, z, radius);
        public bool TryGetRegion(ulong worker, out Vector2[] vertices)
        {
            var polygon = Region(worker); vertices = new Vector2[polygon.Length];
            for (int i = 0; i < polygon.Length; i++) vertices[i] = new Vector2(polygon[i].X, polygon[i].Z);
            return seeds.ContainsKey(worker);
        }
        internal ulong OwnerAt(float x, float z)
        {
            if (seeds.Count == 0) throw new InvalidOperationException("No local workers are registered");
            ulong owner = 0; double best = double.PositiveInfinity;
            foreach (var pair in seeds)
            {
                double dx = (double)pair.Value.X - x, dz = (double)pair.Value.Z - z;
                double distance = dx * dx + dz * dz;
                if (distance < best) { best = distance; owner = pair.Key; }
            }
            return owner;
        }
        internal Seed[] Region(ulong id) => regions.TryGetValue(id, out var polygon) ? polygon : Array.Empty<Seed>();
        // Signed half-plane distance numerator, evaluated relative to the boundary midpoint.
        private static double Side(Seed a, Seed b, double x, double z) =>
            ((double)b.X - a.X) * (x - ((double)a.X + b.X) * .5) +
            ((double)b.Z - a.Z) * (z - ((double)a.Z + b.Z) * .5);
        private bool Inside(ulong id, double x, double z)
        {
            var seed = seeds[id];
            foreach (var pair in seeds) if (pair.Key != id && Side(seed, pair.Value, x, z) > 1e-5) return false;
            return true;
        }
        private Seed[] BuildRegion(ulong id)
        {
            var seed = seeds[id];
            var polygon = new List<Seed> { new Seed(minX, minZ), new Seed(maxX, minZ), new Seed(maxX, maxZ), new Seed(minX, maxZ) };
            foreach (var pair in seeds)
            {
                if (pair.Key == id) continue;
                var clipped = new List<Seed>();
                for (int i = 0; i < polygon.Count; i++)
                {
                    var a = polygon[i]; var b = polygon[(i + 1) % polygon.Count];
                    double sa = Side(seed, pair.Value, a.X, a.Z), sb = Side(seed, pair.Value, b.X, b.Z);
                    if (sa <= 0) clipped.Add(a);
                    if ((sa < 0 && sb > 0) || (sa > 0 && sb < 0))
                    {
                        double t = sa / (sa - sb);
                        clipped.Add(new Seed((float)(a.X + (b.X - a.X) * t), (float)(a.Z + (b.Z - a.Z) * t)));
                    }
                }
                polygon = clipped;
            }
            return polygon.ToArray();
        }
        internal bool IsWithinInterest(ulong worker, float x, float z, float radius)
        {
            if (!seeds.TryGetValue(worker, out var seed)) return false;
            if (OwnerAt(x, z) == worker) return true;
            if (!(radius > 0)) return false;
            double r2 = (double)radius * radius;
            // The closest point of a convex cell is on an edge or vertex. Query its
            // unbounded half-planes directly; the debug crop must never affect interest.
            foreach (var edge in seeds)
            {
                if (edge.Key == worker) continue;
                double nx = (double)edge.Value.X - seed.X, nz = (double)edge.Value.Z - seed.Z;
                double side = Side(seed, edge.Value, x, z), t = side / (nx * nx + nz * nz);
                double px = x - t * nx, pz = z - t * nz;
                if (Inside(worker, px, pz) && (px - x) * (px - x) + (pz - z) * (pz - z) <= r2) return true;
                foreach (var other in seeds)
                {
                    if (other.Key == worker || other.Key <= edge.Key) continue;
                    double ox = (double)other.Value.X - seed.X, oz = (double)other.Value.Z - seed.Z;
                    double determinant = nx * oz - nz * ox;
                    if (Math.Abs(determinant) < 1e-10) continue;
                    double a = -Side(seed, edge.Value, centerX, centerZ), b = -Side(seed, other.Value, centerX, centerZ);
                    px = centerX + (a * oz - nz * b) / determinant; pz = centerZ + (nx * b - a * ox) / determinant;
                    if (Inside(worker, px, pz) && (px - x) * (px - x) + (pz - z) * (pz - z) <= r2) return true;
                }
            }
            return false;
        }
        internal bool ShouldMove(ulong current, float x, float z, float margin)
        {
            if (!seeds.TryGetValue(current, out var seed)) return false;
            ulong next = OwnerAt(x, z); if (next == current) return false;
            var destination = seeds[next]; double nx = (double)destination.X - seed.X, nz = (double)destination.Z - seed.Z;
            // Hysteresis in world units from the bisector, including far outside the debug window.
            return Side(seed, destination, x, z) > Math.Max(0, margin) * Math.Sqrt(nx * nx + nz * nz);
        }
    }
}
