using AtlasNet;
using NUnit.Framework;

namespace AtlasNetDemo.Tests
{
    public sealed class NetworkVariableSnapshotTests
    {
        [TestCase(0)]
        [TestCase(3)]
        public void RepeatedSnapshotsNotifyOnlyRealValueChanges(int initial)
        {
            var value = new NetworkVariable<int>(initial);
            int calls = 0, previous = -1, current = -1;
            value.Changed += (oldValue, newValue) => { calls++; previous = oldValue; current = newValue; };
            void Restore(int next)
            {
                using var writer = new NetWriter(); writer.Write(next);
                using var reader = new NetReader(writer.ToArray()); ((INetworkVariable)value).Read(reader);
            }
            Restore(initial); Restore(initial);
            Assert.AreEqual(0, calls, "Restoring the same value is not a new event.");
            Restore(initial + 1); Restore(initial + 1);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(initial, previous);
            Assert.AreEqual(initial + 1, current);
            Assert.AreEqual(initial + 1, value.Value);
        }
    }
}
