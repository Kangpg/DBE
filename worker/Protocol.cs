using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Dbe;

sealed record InputFile(string Path, long Length, string Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ModifiedUtcTicks = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Deferred = false);
sealed record Request(int Version, string Operation, string Attempt, string? Toolchain, string? Source, InputFile[]? Files,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Options = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PchPair? NativePch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MappedCompile? NativeInput = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ContextKey = null)
{
    internal bool ReturnsPdb => Version >= 3 && Options!.Contains("/Zi");
}
sealed record Capabilities(string Machine, string CompilerVersion, string Toolchain, int Slots, int MemoryMiB, int TimeoutSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxProtocol = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? DeferredInputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ReusablePch = null);
sealed record NativeCacheMetrics(string Key, bool InputsHit, bool PchHit, long InputBuilds, long PchBuilds,
    double WaitMs, double InputsMs, double PchMs, double CompileMs);
sealed record Reply(string Status, string Attempt = "", string? Message = null, Capabilities? Capabilities = null,
    uint? ExitCode = null, int StdoutBytes = 0, int StderrBytes = 0, InputFile? Output = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InputFile? Pdb = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InputFile? ProducerObject = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? MissingInputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? RequiredInputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] InputFile? ProducerPdb = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NativeCacheMetrics? Cache = null);
sealed class Rejected(string message) : Exception(message);

static partial class Wire
{
    internal const int HeaderLimit = 32 * 1024 * 1024, FileLimit = 16 * 1024 * 1024, TotalLimit = 32 * 1024 * 1024;
    internal const int NativeTotalLimit = 1024 * 1024 * 1024;
    internal const int OutputLimit = 32 * 1024 * 1024, DiagnosticLimit = 1024 * 1024;
    // v3 admits one prepared translation unit, streamed
    // within 128 MiB; legacy multi-file budgets remain unchanged.
    internal const int PreparedLimit = 128 * 1024 * 1024, PreparedObjectLimit = 128 * 1024 * 1024, PdbLimit = 64 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        WriteIndented = true
    };

    internal static async Task<int> Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > HeaderLimit) throw new Rejected("Header too large");
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        await stream.WriteAsync(length, token);
        await stream.WriteAsync(bytes, token);
        return bytes.Length + 4;
    }

    internal static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, token);
        int size = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (size is <= 0 or > HeaderLimit) throw new Rejected("Invalid frame length");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new Rejected("Null header");
    }

    internal static void Validate(Request request)
    {
        if (request.Version is not (1 or 2 or 3 or 4 or 5 or 6)) throw new Rejected("Unsupported protocol version");
        if (request.Operation == "info") return;
        if (request.Operation != "compile") throw new Rejected("Unsupported operation");
        if (request.Version == 1 && request.Options is not null) throw new Rejected("Options require protocol v2");
        if (request.Version is 2 or 3) CompileOptions.Validate(request.Options, request.Version);
        if ((request.Version == 4) != (request.NativePch is not null)) throw new Rejected("PCH pair requires protocol v4");
        if ((request.Version >= 5) != (request.NativeInput is not null)) throw new Rejected("Mapped inputs require protocol v5 or v6");
        if (request.Version == 6 && !ValidHash(request.ContextKey)) throw new Rejected("Expected native context identity");
        if (!Guid.TryParseExact(request.Attempt, "N", out _)) throw new Rejected("Invalid attempt id");
        if (request.Files is null || request.Files.Length < 1 || request.Files.Length > (request.Version >= 5 ? 65536 : 64)) throw new Rejected("Input count out of bounds");
        if (request.Version == 3 && request.Files.Length != 1) throw new Rejected("v3 requires one preprocessed input");
        ValidatePath(request.Source, request.Version >= 5 ? 512 : 180);
        if (!(request.Version >= 5 ? new[] { ".cpp", ".cc", ".cxx" } : new[] { ".cpp" })
            .Contains(System.IO.Path.GetExtension(request.Source!), StringComparer.OrdinalIgnoreCase)) throw new Rejected("Expected one C++ source");
        if (!ValidHash(request.Toolchain)) throw new Rejected("Expected pinned toolchain identity");
        long total = 0;
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> contents = new(StringComparer.Ordinal);
        foreach (InputFile file in request.Files)
        {
            if (file is null) throw new Rejected("Null file");
            ValidatePath(file.Path, request.Version >= 5 ? 512 : 180);
            if (request.Version >= 5 && (file.Path.Length < 3 || !char.IsAsciiLetterUpper(file.Path[0]) || file.Path[1] != '/'))
                throw new Rejected("Expected a mapped drive input");
            if (file.Deferred)
            {
                if (request.Version < 5 || file.Length < 0 || file.Sha256 != "") throw new Rejected("Invalid deferred input");
            }
            else if (file.Length < 0 || file.Length > (request.Version == 3 ? PreparedLimit : FileLimit) || !ValidHash(file.Sha256))
                throw new Rejected("Invalid input size or hash");
            if (!paths.Add(file.Path)) throw new Rejected("Duplicate/case-colliding path");
            if (!file.Deferred && (request.Version < 5 || contents.Add(file.Sha256 + "-" + file.ModifiedUtcTicks))) total += file.Length;
            if (request.Version >= 5 && (file.ModifiedUtcTicks is not { } ticks || ticks < DateTime.FromFileTimeUtc(0).Ticks || ticks > DateTime.MaxValue.Ticks))
                throw new Rejected("Expected native input timestamp");
        }
        foreach (string path in paths)
        {
            string[] parts = path.Split('/');
            for (int i = 1; i < parts.Length; i++)
                if (paths.Contains(string.Join('/', parts.Take(i)))) throw new Rejected("File/directory path collision");
        }
        if (total > (request.Version >= 5 ? NativeTotalLimit : request.Version == 3 ? PreparedLimit : TotalLimit) || !paths.Contains(request.Source!)) throw new Rejected("Input budget or source mismatch");
        request.NativePch?.Validate(request, paths);
        request.NativeInput?.Validate(request, paths);
    }

    internal static bool ValidHash(string? value) => value is not null && HashPattern().IsMatch(value);
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])($|\\.)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DevicePattern();

    internal static void ValidatePath(string? path, int limit = 180)
    {
        if (string.IsNullOrEmpty(path) || path.Length > limit || path.Contains('\\')) throw new Rejected("Invalid relative path");
        foreach (string part in path.Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || DevicePattern().IsMatch(part))
                throw new Rejected("Invalid Windows path segment");
    }

    internal static void NoReparse(string path)
    {
        for (string? p = System.IO.Path.GetFullPath(path); p != null; p = System.IO.Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new Rejected("Reparse points are not allowed: " + p);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string HashFile(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    internal static async Task ReceiveFile(Stream source, string path, InputFile manifest, CancellationToken token)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        NoReparse(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        byte[] buffer = new byte[65536];
        long remaining = manifest.Length;
        while (remaining > 0)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (count == 0) throw new EndOfStreamException("Incomplete blob");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            remaining -= count;
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != manifest.Sha256) throw new Rejected("Blob hash mismatch");
    }

    internal static async Task SendFile(Stream target, string path, InputFile manifest, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (file.Length != manifest.Length) throw new Rejected("File changed after manifest creation");
        await file.CopyToAsync(target, token);
    }
}
