namespace Dbe;

// Owns what the caller sees from one adapter process. A compile-ahead attempt may run
// before a reference has generated its headers, so a failure there is not a result:
// its diagnostics are dropped, and the missing object sends the source back through
// the ordinary Build, which runs in dependency order and reports any real error.
internal static class CompilerOutput
{
    internal static readonly bool Deferred = Environment.GetEnvironmentVariable("DBE_COMPILE_AHEAD") == "1";
    private static readonly List<(bool Error, byte[] Bytes)> held = [];
    internal static void Write(byte[] bytes, bool error = false)
    {
        if (bytes.Length == 0) return;
        if (Deferred) { lock (held) held.Add((error, bytes)); return; }
        Put(bytes, error);
    }
    internal static void Error(string line) => Write(System.Text.Encoding.UTF8.GetBytes(line + Environment.NewLine), true);
    private static void Put(byte[] bytes, bool error)
    {
        using var stream = error ? Console.OpenStandardError() : Console.OpenStandardOutput();
        stream.Write(bytes);
    }
    internal static int Finish(int exitCode)
    {
        if (!Deferred) return exitCode;
        if (exitCode != 0)
        {
            Console.WriteLine("DBE: compile-ahead deferred to the ordinary Build");
            return 0;
        }
        lock (held) foreach (var (error, bytes) in held) Put(bytes, error);
        return 0;
    }
}
