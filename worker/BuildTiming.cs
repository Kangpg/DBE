using System.Diagnostics;

namespace Dbe;

// QPC is shared by processes on this Windows host. Overlapping intervals can
// be unioned across MSBuild nodes without synchronizing clocks with Workers.
internal static class BuildTiming
{
    internal static readonly bool Enabled = Environment.GetEnvironmentVariable("DBE_BUILD_TIMING") == "1";
    internal static Interval Measure(string stage) => new(stage);
    internal readonly struct Interval(string stage) : IDisposable
    {
        private readonly long start = Enabled ? Stopwatch.GetTimestamp() : 0;
        public void Dispose()
        {
            if (start != 0) Emit(stage, start, Stopwatch.GetTimestamp());
        }
    }
    internal static void Emit(string stage, long start, long end) =>
        Console.WriteLine($"DBE-TIME {start} {end} {Stopwatch.Frequency} {stage}");

    // Record changes of the observed blocker, not one log line per poll.
    internal sealed class Wait : IDisposable
    {
        private string? stage;
        private long start;
        internal void Observe(string value)
        {
            if (value == stage) return;
            long now = Stopwatch.GetTimestamp();
            if (stage is not null) Emit(stage, start, now);
            stage = value;
            start = now;
        }
        public void Dispose() { if (stage is not null) Emit(stage, start, Stopwatch.GetTimestamp()); }
    }
}
