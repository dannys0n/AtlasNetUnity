using System;
using AtlasNet;

static class PredictionChecks
{
    private readonly record struct DashInput(int Move, bool Dash);
    private readonly record struct DashState(int Position, int Cooldown);

    public static void Run()
    {
        var owner = new PredictionTimeline<DashInput>();
        var authority = new PredictionTimeline<DashInput>();
        var inputs = new[] { new DashInput(0, true), new DashInput(1, false),
            new DashInput(1, true), new DashInput(1, false) };

        DashState predicted = default;
        foreach (var input in inputs)
        {
            owner.RecordLocal(input);
            predicted = Simulate(predicted, input);
        }
        Check(predicted == new DashState(8, 0), "Owner should predict the first dash immediately");

        authority.Queue(1, inputs[0]);
        authority.Queue(3, inputs[2]);
        authority.Queue(4, inputs[3]);
        Check(!authority.TryConsume(out _) && !authority.TryConsume(out _), "Authority should buffer the first two ticks");
        Check(authority.TryConsume(out var first), "Authority should consume the first queued input");
        var confirmed = Simulate(new DashState(0, 2), first);
        Check(confirmed == new DashState(0, 1) && authority.Acknowledged == 1,
            "The authoritative cooldown should prevent the first dash");

        predicted = confirmed;
        owner.ReplayFrom(authority.Acknowledged, input => predicted = Simulate(predicted, input));
        Check(predicted == new DashState(7, 2),
            "Replay should restore the server's cooldown and allow the later dash");

        using var writer = new NetWriter();
        authority.WriteHandoff(writer, (packet, input) =>
        {
            packet.Write(input.Move);
            packet.Write(input.Dash);
        });
        using var reader = new NetReader(writer.ToArray());
        var worker = new PredictionTimeline<DashInput>();
        worker.RestoreAcknowledgement(authority.Acknowledged);
        worker.ReadHandoff(reader, packet => new DashInput(packet.ReadInt(), packet.ReadBool()));
        Check(!reader.HasRemaining, "Handoff should read the entire input queue");

        Check(owner.TryGetLocal(2, out var repeated), "The missing input should still be in the owner's history");
        worker.Queue(2, repeated);
        worker.Queue(3, default);
        for (int i = 0; i < 3; i++)
        {
            Check(worker.TryConsume(out var input), "The new worker should have the next input");
            confirmed = Simulate(confirmed, input);
        }
        Check(confirmed == predicted && worker.Acknowledged == 4,
            "The new worker should consume the recovered input and its transferred queue in order");
        Check(!worker.TryConsume(out _) && worker.Acknowledged == 4,
            "An empty queue must hold the acknowledgement until another input arrives");

        owner.RestoreAcknowledgement(uint.MaxValue - 1);
        owner.InitializeLocalSequence();
        Check(owner.RecordLocal(inputs[0]) == uint.MaxValue && owner.RecordLocal(inputs[1]) == 0,
            "Local input sequence should wrap at the uint boundary");
        int replayed = 0;
        owner.ReplayFrom(uint.MaxValue, input => replayed += input.Move);
        Check(replayed == 1, "Replay should include inputs after sequence wrap");
    }

    private static DashState Simulate(DashState state, DashInput input)
    {
        if (input.Dash && state.Cooldown == 0) return new DashState(state.Position + 5, 3);
        return new DashState(state.Position + input.Move, Math.Max(0, state.Cooldown - 1));
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
