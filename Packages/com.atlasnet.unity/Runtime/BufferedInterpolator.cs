using System;

namespace AtlasNet
{
    // A fixed-size, receipt-time interpolation buffer. Simulation and owner-written
    // channels never use this: it is local presentation for joined clients only.
    internal sealed class BufferedInterpolator<T>
    {
        struct Sample { public double Time; public T Value; }
        readonly Sample[] samples = new Sample[32];
        readonly Func<T, T, float, T> lerp;
        int first, count;
        public BufferedInterpolator(Func<T, T, float, T> interpolate) => lerp = interpolate;
        public void Reset(double time, T value) { first = count = 0; Add(time, value); }
        public void Add(double time, T value)
        {
            if (count > 0)
            {
                int last = (first + count - 1) % samples.Length;
                // Several packets can be received in one render frame. Keep the newest.
                if (time <= samples[last].Time) { samples[last].Value = value; return; }
            }
            if (count == samples.Length) { first = (first + 1) % samples.Length; count--; }
            samples[(first + count++) % samples.Length] = new Sample { Time = time, Value = value };
        }
        public T Evaluate(double time)
        {
            while (count > 1 && samples[(first + 1) % samples.Length].Time <= time)
            { first = (first + 1) % samples.Length; count--; }
            var from = samples[first];
            if (count < 2 || time <= from.Time) return from.Value;
            var to = samples[(first + 1) % samples.Length];
            return lerp(from.Value, to.Value, (float)((time - from.Time) / (to.Time - from.Time)));
        }
    }
}
