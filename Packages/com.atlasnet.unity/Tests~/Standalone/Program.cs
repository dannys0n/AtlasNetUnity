using System;
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
        PredictionChecks.Run();
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

        var ordering=new MessageSequence();var peer=new SessionId(20);
        Check(ordering.Accept(peer,1,10),"First sequence accepted");
        Check(!ordering.Accept(peer,1,9)&&!ordering.Accept(peer,1,10),"Old and duplicate sequence rejected");
        Check(ordering.Accept(peer,2,1),"Independent streams do not suppress each other");
        Check(ordering.Accept(peer,3,uint.MaxValue)&&ordering.Accept(peer,3,0),"Sequence wraparound");
        ordering.Remove(peer);Check(ordering.Accept(peer,1,1),"Disconnect clears sequence state");
        Check(new RpcAttribute(SendTo.Authority).Delivery==RpcDelivery.Reliable,"RPCs default to reliable delivery");
        Check(new RpcAttribute(SendTo.Authority){Delivery=RpcDelivery.Unreliable}.Delivery==RpcDelivery.Unreliable,"NGO-style delivery configuration");

        Console.WriteLine("AtlasNet codec, sequencing, and prediction checks passed.");
    }
}
