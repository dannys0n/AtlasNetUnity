using System;
using System.Net.Sockets;
using AtlasNet;
using UnityEngine;

static class Program
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Main()
    {
        HandoffChecks.Run();
        var entity = new AtlasNet.EntityId(829381);
        var session = new SessionId(42);
        using (var writer = new NetWriter())
        {
            writer.Write(entity);
            writer.Write(session);
            writer.Write(new Vector3(1, 2, 3));
            writer.WriteBytes(new byte[] { 4, 5, 6 });
            using (var reader = new NetReader(writer.ToArray()))
            {
                Check(reader.ReadEntityId() == entity, "Entity ID round trip");
                Check(reader.ReadSessionId() == session, "Session ID round trip");
                Check(reader.ReadVector3() == new Vector3(1, 2, 3), "Vector round trip");
                Check(reader.ReadBytes().Length == 3, "Byte payload round trip");
                Check(!reader.HasRemaining, "Reader has unexpected trailing data");
            }
        }

        var regions = new LocalVoronoi(-15, 15, -15, 15);
        regions.AddWorker(0);
        Check(regions.Region(0).Length == 4, "Single worker must own rectangular world");
        regions.AddWorker(2);
        var firstCell = regions.Region(0);
        var secondCell = regions.Region(2);
        Check(firstCell.Length >= 3 && secondCell.Length >= 3, "Workers need drawable regions");
        Check(Math.Abs(Area(firstCell) + Area(secondCell) - 900) < 0.01,
            "Worker region polygons must cover the world without gaps or overlap");
        var firstCenter = Center(firstCell);
        var secondCenter = Center(secondCell);
        Check(regions.IsWithinInterest(0, firstCenter.X, firstCenter.Z, 0),
            "First worker must retain entities in its own region");
        Check(!regions.IsWithinInterest(2, firstCenter.X, firstCenter.Z, 0),
            "Other workers must not receive an entire remote region");
        Check(regions.IsWithinInterest(2, firstCenter.X, firstCenter.Z, 100),
            "A sufficiently wide ghost overlap must include nearby remote entities");
        Check(regions.IsWithinInterest(2, secondCenter.X, secondCenter.Z, 0),
            "Second worker must retain entities in its own region");
        Check(LocalWorldPolicy.WithinRadius(new Vector3(0, 10, 0), new Vector3(3, -10, 4), 5),
            "Local interest radius is measured on X/Z, including its boundary");
        Check(!LocalWorldPolicy.WithinRadius(Vector3.zero, new Vector3(3, 0, 4), 4.99f),
            "Local interest must exclude entities outside the radius");
        var crossServerDemo = new LocalVoronoi(-12, 12, -12, 12);
        crossServerDemo.AddWorker(0);
        crossServerDemo.AddWorker(2);
        Check(crossServerDemo.OwnerAt(2, 2) != crossServerDemo.OwnerAt(-6, -6),
            "Cross-server demo spawns must land on different workers");
        regions.AddWorker(3);
        Check(Math.Abs(Area(regions.Region(0)) + Area(regions.Region(2)) +
            Area(regions.Region(3)) - 900) < 0.01,
            "Three worker region polygons must cover the world");


        LocalTcpTransport server = null;
        int port = 17000;
        for (; port < 17100; port++)
        {
            try { server = new LocalTcpTransport(true, "127.0.0.1", port); break; }
            catch (SocketException) { }
        }
        Check(server != null, "No local test port available");
        using (server)
        using (var client = new LocalTcpTransport(false, "127.0.0.1", port))
        {
            SessionId connected = default;
            SessionId sender = default;
            byte[] received = null;
            byte[] reply = null;
            server.Connected += id => connected = id;
            server.Received += (id, data) => { sender = id; received = data; };
            client.Received += (_, data) => reply = data;
            server.Poll();
            Check(connected.Value == 2, "Remote session assignment");
            client.SendToServer(new byte[] { 10, 11, 12 });
            server.Poll();
            Check(sender == connected && received?.Length == 3 && received[0] == 10, "Client to server framing");
            server.SendTo(connected, new byte[] { 20, 21 });
            client.Poll();
            Check(reply?.Length == 2 && reply[0] == 20, "Server to client framing");
        }
        Console.WriteLine("AtlasNet codec and local transport checks passed.");
    }

    static double Area(LocalVoronoi.Seed[] vertices)
    {
        double twice = 0;
        for (int i = 0; i < vertices.Length; i++)
        {
            var next = vertices[(i + 1) % vertices.Length];
            twice += vertices[i].X * next.Z - next.X * vertices[i].Z;
        }
        return Math.Abs(twice) * 0.5;
    }

    static LocalVoronoi.Seed Center(LocalVoronoi.Seed[] vertices)
    {
        float x = 0, z = 0;
        foreach (var vertex in vertices) { x += vertex.X; z += vertex.Z; }
        return new LocalVoronoi.Seed(x / vertices.Length, z / vertices.Length);
    }
}
