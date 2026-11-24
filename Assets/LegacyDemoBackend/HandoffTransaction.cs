using System;
using System.Collections.Generic;

namespace AtlasNet
{
    // Coordinator transaction state. A prepare ACK cannot commit before source export.
    internal sealed class HandoffTransaction
    {
        public SessionId Source { get; }
        public SessionId Destination { get; }
        public uint Epoch { get; }
        public uint CurrentEpoch { get; }
        public uint StartedAtTick { get; }
        public readonly List<byte[]> OwnerUpdates = new List<byte[]>();
        private bool waitingForExport;

        public HandoffTransaction(SessionId source, SessionId destination, uint currentEpoch,
            uint startedAtTick, bool waitingForExport, uint? proposedEpoch = null)
        {
            if (source == destination) throw new ArgumentException("Handoff needs a different destination");
            Source = source;
            Destination = destination;
            CurrentEpoch = currentEpoch;
            Epoch = proposedEpoch ?? checked(currentEpoch + 1);
            if (Epoch <= currentEpoch) throw new ArgumentException("Handoff epoch must increase");
            StartedAtTick = startedAtTick;
            this.waitingForExport = waitingForExport;
        }

        public bool TryAcceptExport(SessionId source, uint currentEpoch, uint nextEpoch, ulong destination)
        {
            if (!ExpectsExport(source, currentEpoch, nextEpoch, destination)) return false;
            waitingForExport = false;
            return true;
        }

        public bool ExpectsExport(SessionId source, uint currentEpoch, uint nextEpoch, ulong destination)
            => waitingForExport && source == Source && nextEpoch == Epoch &&
                currentEpoch == CurrentEpoch && destination == Destination.Value;

        public bool AcceptsPrepared(SessionId destination, uint epoch)
            => !waitingForExport && destination == Destination && epoch == Epoch;

        public bool IsExpired(uint tick, int tickRate)
            => unchecked(tick - StartedAtTick) > (uint)tickRate * 5;
    }
}
