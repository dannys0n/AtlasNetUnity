using System;
using AtlasNet;

internal static class HandoffChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var source = new SessionId(2);
        var destination = new SessionId(3);
        var transfer = new HandoffTransaction(source, destination, 7, 100, waitingForExport: true);
        Check(!transfer.AcceptsPrepared(destination, 8), "An early prepare ACK cannot commit before export");
        Check(!transfer.TryAcceptExport(destination, 7, 8, 3), "Another worker cannot export the source state");
        Check(!transfer.TryAcceptExport(source, 6, 8, 3), "A stale source epoch cannot export");
        Check(!transfer.TryAcceptExport(source, 7, 9, 3), "Export must match the proposed epoch");
        Check(!transfer.TryAcceptExport(source, 7, 8, 4), "Export must match the destination");
        Check(!transfer.AcceptsPrepared(destination, 8), "Invalid exports must not advance the transaction");
        Check(transfer.TryAcceptExport(source, 7, 8, 3), "The exact source export must advance the transaction");
        Check(!transfer.TryAcceptExport(source, 7, 8, 3), "A duplicate export must not advance twice");
        Check(!transfer.AcceptsPrepared(source, 8), "Only the destination can acknowledge prepare");
        Check(!transfer.AcceptsPrepared(destination, 7), "Old ACKs cannot commit");
        Check(transfer.AcceptsPrepared(destination, 8), "Export followed by matching prepare ACK can commit");
        Check(!transfer.IsExpired(250, 30), "Handoff remains valid at its five-second timeout boundary");
        Check(transfer.IsExpired(251, 30), "Handoff expires after five seconds");

        var localSource = new HandoffTransaction(default, destination, 0, 0, waitingForExport: false);
        Check(localSource.AcceptsPrepared(destination, 1), "A locally captured source can prepare immediately");

        // Abort leaves committed epoch 7 unchanged. Retry reserves 9 instead of reusing 8.
        var retry = new HandoffTransaction(source, destination, 7, 200, waitingForExport: true, proposedEpoch: 9);
        Check(!retry.TryAcceptExport(source, 7, 8, 3), "An aborted attempt's export cannot advance a retry");
        Check(retry.TryAcceptExport(source, 7, 9, 3), "Retry accepts its own export despite a skipped epoch");
        Check(!retry.AcceptsPrepared(destination, 8), "An aborted attempt's delayed ACK cannot commit a retry");
        Check(retry.AcceptsPrepared(destination, 9), "Retry accepts its own prepare ACK");

        var wrapped = new HandoffTransaction(source, destination, 0, uint.MaxValue - 10, true);
        Check(!wrapped.IsExpired(100, 30), "Tick wrap must not immediately expire a handoff");
        Check(wrapped.IsExpired(150, 30), "Timeout must still work across tick wrap");
        bool overflowRejected = false;
        try { new HandoffTransaction(source, destination, uint.MaxValue, 0, true); }
        catch (OverflowException) { overflowRejected = true; }
        Check(overflowRejected, "Authority epoch overflow cannot wrap back to zero");
        Console.WriteLine("AtlasNet handoff ordering, stale-attempt, and timeout checks passed.");
    }
}
