using System;
using System.Linq;
using AtlasNet;
using UnityEngine;

static class PartitionChecks
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static void Run()
    {
        foreach (int count in new[] { 1, 2, 3, 4, 7 })
        {
            var world = new LocalVoronoi(new Vector2(40, -20), 2);
            for (ulong i = 0; i < (ulong)count; i++) world.AddWorker(i);
            var shares = new int[count];
            for (int i = 0; i < 840; i++)
            {
                double angle = (i + .317) * Math.PI * 2 / 840;
                float x = 40 + (float)(Math.Cos(angle) * 50000), z = -20 + (float)(Math.Sin(angle) * 50000);
                ulong owner = world.OwnerAt(x, z); shares[(int)owner]++;
                Check(world.IsWithinInterest(owner, x, z, 0), "Ownership must extend beyond the debug crop");
                Check(world.ShouldMove((owner + 1) % (ulong)count, x, z, .5f) == (count > 1), "Far-away ownership must still trigger handoffs");
            }
            Check(shares.Max() - shares.Min() <= 1, "Workers must have equal angular area shares around the configured center");
        }
        var balanced = new LocalVoronoi(-15, 15, -15, 15);
        balanced.AddWorker(0); balanced.AddWorker(2);
        Check(balanced.Region(0).Length == 4 && balanced.Region(2).Length == 4, "Two cells should crop to rectangles");
        Check(Math.Abs(Area(balanced.Region(0)) - 450) < .01, "Two workers should evenly split the debug square");
        var old = balanced.Seeds[0]; balanced.AddWorker(3);
        Check(old.X != balanced.Seeds[0].X || old.Z != balanced.Seeds[0].Z, "Joining workers must reposition existing seeds");
        balanced.AddWorker(4);
        foreach (ulong id in new ulong[] { 0, 2, 3, 4 })
        {
            Check(Math.Abs(Area(balanced.Region(id)) - 225) < .01, "Four workers should form four equal squares");
            Check(balanced.Region(id).Length >= 4, "Each quadrant needs a drawable polygon");
        }
        balanced.RemoveWorker(3); balanced.RemoveWorker(4);
        Check(Math.Abs(Area(balanced.Region(0)) - 450) < .01, "Departures must rebalance remaining cells");
        var cropped = new LocalVoronoi(new Vector2(100, -50), 2); cropped.AddWorker(0); cropped.AddWorker(2);
        Check(cropped.IsWithinInterest(0, 100.4f, 10000, .5f), "Ghost interest must work on the unbounded boundary outside the debug crop");
        Check(!cropped.IsWithinInterest(0, 100.6f, 10000, .5f), "Ghost interest must honor the radius outside the debug crop");
        Check(!cropped.ShouldMove(0, 100.4f, 10000, .5f) && cropped.ShouldMove(0, 100.6f, 10000, .5f), "Boundary margin must use perpendicular world distance");
        Console.WriteLine("Balanced, centered, unbounded Voronoi partition checks passed.");
    }
    static double Area(LocalVoronoi.Seed[] polygon)
    {
        double area = 0;
        for (int i = 0; i < polygon.Length; i++) { var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length]; area += (double)a.X * b.Z - (double)b.X * a.Z; }
        return Math.Abs(area) * .5;
    }
}
