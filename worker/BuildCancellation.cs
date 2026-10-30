namespace Dbe;

sealed class BuildCancellation : IDisposable
{
    private readonly EventWaitHandle signal;
    private readonly RegisteredWaitHandle registration;
    private BuildCancellation(EventWaitHandle signal, CancellationTokenSource cancellation)
    {
        this.signal = signal;
        registration = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => cancellation.Cancel(), null, Timeout.Infinite, true);
    }
    internal static BuildCancellation? Observe(CancellationTokenSource cancellation)
    {
        string? name = Environment.GetEnvironmentVariable("DBE_BUILD_CANCEL_EVENT");
        if (string.IsNullOrEmpty(name)) return null;
        const string prefix = @"Local\DBE.Build.";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new Rejected("Invalid build cancellation event");
        return new BuildCancellation(EventWaitHandle.OpenExisting(name), cancellation);
    }
    public void Dispose()
    {
        using var finished = new ManualResetEvent(false);
        if (registration.Unregister(finished)) finished.WaitOne();
        signal.Dispose();
    }
}
