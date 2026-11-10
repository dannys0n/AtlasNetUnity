using System;

namespace AtlasNet
{
    /// <summary>Tracks local replay history and the authority's ordered input queue.</summary>
    internal sealed class PredictionTimeline<TInput> where TInput : struct
    {
        private const int HistorySize = 128;
        private const int StartBufferTicks = 2;

        private struct Entry
        {
            public uint Sequence;
            public TInput Input;
            public bool Valid;
        }

        private readonly Entry[] history = new Entry[HistorySize];
        private readonly Entry[] inbox = new Entry[HistorySize];
        private uint localSequence;
        private int startBuffer;
        private int queuedInputs;
        private bool started;

        public uint Acknowledged { get; private set; }

        public void RestoreAcknowledgement(uint sequence) => Acknowledged = sequence;

        public void InitializeLocalSequence() => localSequence = Acknowledged;

        public void Reset()
        {
            localSequence = Acknowledged = 0;
            startBuffer = queuedInputs = 0;
            started = false;
            Array.Clear(history, 0, history.Length);
            Array.Clear(inbox, 0, inbox.Length);
        }

        public uint RecordLocal(TInput input, bool authoritative = false)
        {
            uint sequence = unchecked(++localSequence);
            if (authoritative) Acknowledged = sequence;
            else history[Slot(sequence)] = new Entry { Sequence = sequence, Input = input, Valid = true };
            return sequence;
        }

        public bool TryGetLocal(uint sequence, out TInput input) => TryGet(history, sequence, out input);

        public void Queue(uint sequence, TInput input)
        {
            int ahead = Difference(sequence, Acknowledged);
            if (ahead <= 0 || ahead >= HistorySize || TryGet(inbox, sequence, out _)) return;

            if (!started)
            {
                started = true;
                startBuffer = StartBufferTicks;
                Acknowledged = unchecked(sequence - 1);
            }
            inbox[Slot(sequence)] = new Entry { Sequence = sequence, Input = input, Valid = true };
            queuedInputs++;
        }

        public bool TryConsume(out TInput input)
        {
            input = default;
            if (!started) return false;
            if (startBuffer > 0)
            {
                startBuffer--;
                return false;
            }

            // Hold the cursor if packets have not arrived yet, including after a worker handoff.
            if (queuedInputs == 0) return false;

            uint sequence = unchecked(++Acknowledged);
            if (!TryGet(inbox, sequence, out input)) return false;
            inbox[Slot(sequence)] = default;
            queuedInputs--;
            return true;
        }

        // The caller restores the confirmed simulation state before replaying inputs.
        public void ReplayFrom(uint sequence, Action<TInput> simulate)
        {
            Acknowledged = sequence;
            int count = Difference(localSequence, sequence);
            if (count < 0 || count >= HistorySize)
            {
                localSequence = sequence;
                Array.Clear(history, 0, history.Length);
                return;
            }

            for (int i = 1; i <= count; i++)
                if (TryGetLocal(unchecked(sequence + (uint)i), out var input)) simulate(input);
        }

        public void WriteHandoff(NetWriter writer, Action<NetWriter, TInput> writeInput)
        {
            writer.Write(started);
            writer.Write(startBuffer);
            for (int offset = 1; offset < HistorySize; offset++)
            {
                uint sequence = unchecked(Acknowledged + (uint)offset);
                if (!TryGet(inbox, sequence, out var input)) continue;
                writer.Write(offset);
                writeInput(writer, input);
            }
            writer.Write(0);
        }

        public void ReadHandoff(NetReader reader, Func<NetReader, TInput> readInput)
        {
            started = reader.ReadBool();
            startBuffer = reader.ReadInt();
            queuedInputs = 0;
            Array.Clear(inbox, 0, inbox.Length);
            int previousOffset = 0;
            for (int offset = reader.ReadInt(); offset != 0; offset = reader.ReadInt())
            {
                if (offset <= previousOffset || offset >= HistorySize)
                    throw new InvalidOperationException("Invalid predicted input offset.");
                uint sequence = unchecked(Acknowledged + (uint)offset);
                inbox[Slot(sequence)] = new Entry { Sequence = sequence, Input = readInput(reader), Valid = true };
                queuedInputs++;
                previousOffset = offset;
            }
        }

        internal static int Difference(uint a, uint b) => unchecked((int)(a - b));

        private static int Slot(uint sequence) => (int)(sequence % HistorySize);

        private static bool TryGet(Entry[] entries, uint sequence, out TInput input)
        {
            var entry = entries[Slot(sequence)];
            if (entry.Valid && entry.Sequence == sequence)
            {
                input = entry.Input;
                return true;
            }
            input = default;
            return false;
        }
    }
}
