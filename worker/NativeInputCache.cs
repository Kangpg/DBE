using System.Runtime.InteropServices;

namespace Dbe;

// Received bytes are hash checked before publication. Pinned cache files deny
// writes, so attempt hard links can share immutable bytes without repeated copies.
sealed class NativeInputCache(string root) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, FileStream> pins = new(StringComparer.Ordinal);
    private const long Budget = 2L * 1024 * 1024 * 1024;
    private long retained;
    private long stored = -1;
    private int storedFiles;
    private static string Key(InputFile file) => file.Sha256 + "-" + file.ModifiedUtcTicks!.Value;
    internal async Task Receive(Stream stream, Request request, string workspace, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(root);
            Wire.NoReparse(root);
            if (stored < 0)
            {
                long size = 0; int count = 0;
                foreach (string existing in Directory.EnumerateFiles(root))
                {
                    token.ThrowIfCancellationRequested();
                    Wire.NoReparse(existing);
                    size += new FileInfo(existing).Length;
                    if (++count > 65536 || size > Budget) throw new Rejected("Retained native cache exceeds budget");
                }
                stored = size;
                storedFiles = count;
            }
            List<int> missing = [];
            var declared = new Dictionary<string, long>(StringComparer.Ordinal);
            for (int i = 0; i < request.Files!.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                InputFile file = request.Files[i];
                if (file.Deferred) continue;
                string key = Key(file), path = Path.Combine(root, key);
                if (declared.TryGetValue(key, out long priorLength))
                {
                    if (priorLength != file.Length) throw new Rejected("Native input cache metadata conflict");
                    continue;
                }
                declared.Add(key, file.Length);
                if (pins.TryGetValue(key, out var pinned))
                {
                    if (pinned.Length != file.Length) throw new Rejected("Native cache length mismatch");
                    continue;
                }
                if (File.Exists(path)) Pin(path, file);
                else missing.Add(i);
            }
            if ((long)storedFiles + missing.Count > 65536 || stored + missing.Sum(i => request.Files[i].Length) > Budget)
                throw new Rejected("Native cache capacity exhausted");
            await Wire.Write(stream, new Reply("accepted", request.Attempt, MissingInputs: missing.ToArray()), token);
            foreach (int i in missing)
            {
                InputFile file = request.Files[i];
                string path = Path.Combine(root, Key(file));
                if (stored + file.Length > Budget) throw new Rejected("Native cache budget exhausted");
                var disk = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!);
                if (disk.AvailableFreeSpace < 2L * 1024 * 1024 * 1024 + file.Length)
                    throw new Rejected("Native cache disk headroom insufficient");
                string pending = Path.Combine(workspace, "cache-" + i);
                await Wire.ReceiveFile(stream, pending, file, token);
                File.SetLastWriteTimeUtc(pending, new DateTime(file.ModifiedUtcTicks!.Value, DateTimeKind.Utc));
                // A repeated digest/timestamp within this request shares one cache entry.
                if (!pins.ContainsKey(Key(file)))
                {
                    File.Move(pending, path);
                    stored += file.Length;
                    storedFiles++;
                    Pin(path, file);
                }
                else File.Delete(pending);
            }
        }
        finally { gate.Release(); }
        // Cache mutation is serialized; independent attempt trees are not.
        // Every referenced cache file remains immutable and pinned until Serve
        // has awaited all active attempts, including their materialization.
        if (request.Version < 6) Materialize(request, workspace, token);
    }

    internal void Materialize(Request request, string workspace, CancellationToken token)
    {
        string inputs = Path.Combine(workspace, "inputs");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void EnsureDirectory(string path)
        {
            if (!directories.Add(path)) return;
            if (directories.Count > 65536) throw new Rejected("Native directory budget exhausted");
            if (!path.Equals(inputs, StringComparison.OrdinalIgnoreCase)) EnsureDirectory(Path.GetDirectoryName(path)!);
            Directory.CreateDirectory(path);
            Wire.NoReparse(path);
        }
        foreach (InputFile file in request.Files!)
        {
            token.ThrowIfCancellationRequested();
            string destination = Path.Combine(inputs, file.Path.Replace('/', '\\'));
            EnsureDirectory(Path.GetDirectoryName(destination)!);
            if (file.Deferred)
            {
                byte[] marker = System.Text.Encoding.UTF8.GetBytes("#error " + MappedCompile.Marker(file.Path) + "\n");
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(marker);
                File.SetLastWriteTimeUtc(destination, new DateTime(file.ModifiedUtcTicks!.Value, DateTimeKind.Utc));
            }
            else if (!CreateHardLink(@"\\?\" + destination, @"\\?\" + Path.Combine(root, Key(file)), IntPtr.Zero))
                throw new Rejected("Native input hard link failed. Win32=" + Marshal.GetLastWin32Error());
        }
    }
    private void Pin(string path, InputFile file)
    {
        if (retained + file.Length > Budget) throw new Rejected("Native cache budget exhausted");
        Wire.NoReparse(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != file.Length || File.GetLastWriteTimeUtc(path).Ticks != file.ModifiedUtcTicks ||
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)) != file.Sha256)
                throw new Rejected("Native cache identity mismatch");
            pins.Add(Key(file), stream); retained += file.Length;
        }
        catch { stream.Dispose(); throw; }
    }
    public void Dispose() { foreach (var pin in pins.Values) pin.Dispose(); gate.Dispose(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr attributes);
}
