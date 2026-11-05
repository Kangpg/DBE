using System.Text;

namespace Dbe;

// Durable path/length/mtime -> SHA-256 memo for unchanged native inputs. It only
// avoids rereading bytes: MSBuild's own tracking trusts the same metadata, and
// any malformed or missing state simply means hashing again. A file changed
// within RacyWindow of hashing is not recorded, because a later edit could land
// on the same timestamp (the rule Git calls "racy" entries).
sealed class NativeHashIndex
{
    private const long ByteLimit = 64L * 1024 * 1024;
    private const int EntryLimit = 262144;
    private static readonly TimeSpan RacyWindow = TimeSpan.FromSeconds(2);
    private readonly record struct Entry(long Length, long Ticks, string Hash);
    private readonly string path;
    private readonly Dictionary<string, Entry> known;
    private readonly Dictionary<string, Entry> added = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();
    private NativeHashIndex(string path, Dictionary<string, Entry> known) { this.path = path; this.known = known; }

    internal static NativeHashIndex Load(string stateDirectory)
    {
        string file = Path.Combine(stateDirectory, "native-hash-index.txt");
        return new NativeHashIndex(file, Read(file));
    }
    private static Dictionary<string, Entry> Read(string file)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > ByteLimit) return entries;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split('\t', 4);
                if (parts.Length == 4 && long.TryParse(parts[0], out long ticks) && long.TryParse(parts[1], out long length) &&
                    Wire.ValidHash(parts[2]) && Path.IsPathFullyQualified(parts[3]))
                    entries[parts[3]] = new Entry(length, ticks, parts[2]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DecoderFallbackException) { entries.Clear(); }
        return entries;
    }
    internal bool TryGet(string file, long length, long ticks, out string hash)
    {
        hash = "";
        if (!known.TryGetValue(file, out Entry entry) || entry.Length != length || entry.Ticks != ticks) return false;
        hash = entry.Hash;
        return true;
    }
    internal void Add(string file, long length, long ticks, string hash)
    {
        if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) < RacyWindow) return;
        lock (gate) added[file] = new Entry(length, ticks, hash);
    }
    // Other adapter processes may have saved since Load, so merge before the
    // atomic replacement. Losing a race only costs a later rehash.
    internal void Save()
    {
        lock (gate)
        {
            if (added.Count == 0) return;
            var merged = Read(path);
            if (merged.Count + added.Count > EntryLimit) merged.Clear();
            foreach (var (file, entry) in added) merged[file] = entry;
            string pending = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var writer = new StreamWriter(pending, false, new UTF8Encoding(false)))
                    foreach (var (file, entry) in merged)
                        writer.Write(entry.Ticks + "\t" + entry.Length + "\t" + entry.Hash + "\t" + file + "\n");
                File.Move(pending, path, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            finally { try { File.Delete(pending); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
            added.Clear();
        }
    }
}
