using System;
using System.Collections.Generic;
using AtlasNet;
using Newtonsoft.Json.Linq;
using EntityId = AtlasNet.EntityId;

// Owned by the dedicated server adapter and called on Unity's simulation thread.
internal sealed class UnityShardMigration
{
    private const int MaxSnapshotBytes = 48 * 1024;

    private enum Phase { Prepared, Committed, Aborted }
    private sealed class Transfer
    {
        public EntityId Entity;
        public bool Extract;
        public byte[] Snapshot;
        public Phase Phase;
    }

    private readonly NetworkManager manager;
    private readonly Func<EntityId, byte[]> capture;
    private readonly Action<EntityId, bool> setFrozen;
    private readonly Action<EntityId, byte[]> restore;
    private readonly Dictionary<Guid, Transfer> transfers = new();
    private readonly HashSet<EntityId> pending = new();

    public UnityShardMigration(NetworkManager manager, Func<EntityId, byte[]> capture,
        Action<EntityId, bool> setFrozen,
        Action<EntityId, byte[]> restore)
    {
        this.manager = manager;
        this.capture = capture;
        this.setFrozen = setFrozen;
        this.restore = restore;
    }

    public bool Handle(JObject request, JObject reply)
    {
        string type = request.Value<string>("type");
        var operation = type switch
        {
            "prepare_extract" or "prepare_restore" => Phase.Prepared,
            "commit_extract" or "commit_restore" => Phase.Committed,
            "abort_extract" or "abort_restore" => Phase.Aborted,
            _ => throw new ArgumentException("Unknown migration operation.", nameof(request))
        };
        bool extract = type.EndsWith("_extract", StringComparison.Ordinal);
        var entity = new EntityId(request.Value<ulong>("entity"));
        if (entity.Value == 0 || !Guid.TryParse(request.Value<string>("transfer"), out var id) || id == Guid.Empty)
            return false;

        if (!transfers.TryGetValue(id, out var transfer))
        {
            if (operation == Phase.Aborted)
            {
                transfers.Add(id, new Transfer { Entity = entity, Extract = extract, Phase = Phase.Aborted });
                return true;
            }

            if (operation != Phase.Prepared || pending.Contains(entity) ||
                manager.TryGet(entity, out _) != extract)
                return false;

            byte[] snapshot = extract ? capture(entity) : ReadSnapshot(request);
            if (snapshot == null || snapshot.Length > MaxSnapshotBytes) return false;
            if (extract) setFrozen(entity, true);
            transfer = new Transfer { Entity = entity, Extract = extract, Snapshot = snapshot };
            transfers.Add(id, transfer);
            pending.Add(entity);
        }
        else if (transfer.Entity != entity || transfer.Extract != extract)
        {
            return false;
        }

        if (operation == Phase.Prepared && transfer.Phase == Phase.Prepared)
        {
            if (extract) reply["snapshot"] = WriteSnapshot(transfer.Snapshot);
            else if (ReadSnapshot(request) is not byte[] repeated ||
                !repeated.AsSpan().SequenceEqual(transfer.Snapshot))
                return false;
            return true;
        }

        if (transfer.Phase == operation) return true;
        if (transfer.Phase != Phase.Prepared) return false;

        if (operation == Phase.Committed)
        {
            if (extract) manager.RemoveReplica(entity);
            else restore(entity, transfer.Snapshot);
        }

        if (extract) setFrozen(entity, false);
        transfer.Phase = operation;
        transfer.Snapshot = null;
        pending.Remove(entity);
        return true;
    }

    private static byte[] ReadSnapshot(JObject request)
    {
        if (request["snapshot"] is not JArray array || array.Count > MaxSnapshotBytes) return null;
        var snapshot = new byte[array.Count];
        for (int i = 0; i < snapshot.Length; i++)
        {
            if (array[i].Type != JTokenType.Integer || !byte.TryParse(array[i].ToString(), out snapshot[i]))
                return null;
        }
        return snapshot;
    }

    private static JArray WriteSnapshot(byte[] snapshot)
    {
        var result = new JArray();
        foreach (byte value in snapshot) result.Add(value);
        return result;
    }
}
