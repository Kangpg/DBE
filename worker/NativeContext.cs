using System.Text.Json;

namespace Dbe;

// Parsed immutable common inputs. Requests vary only their source and consumer
// options; a reference cannot change the namespace or producer recipe.
sealed class NativeContext
{
    internal Request Definition { get; }
    internal string Identity { get; }
    private readonly string descriptor;
    private readonly HashSet<string> paths;
    internal NativeContext(Request request)
    {
        request = request with { Files = request.Files?.ToArray(), Options = request.Options?.ToArray(),
            NativeInput = request.NativeInput is { } input ? input with {
                Producer = input.Producer is { } producer ? producer with { Options = producer.Options?.ToArray()! } : null } : null };
        Wire.Validate(request);
        if (request.Version != 6 || Key(request) != request.ContextKey) throw new Rejected("Native context identity mismatch");
        Definition = request; Identity = request.ContextKey!; descriptor = Descriptor(request);
        paths = request.Files!.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    internal byte[] Encode() => Encode(Definition);
    internal static string Key(Request request) => Wire.Hash(Encode(request));
    private static byte[] Encode(Request request) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = "native-context-v1", request.Toolchain, request.Files,
        request.NativeInput!.WorkingDirectory, request.NativeInput.Include, request.NativeInput.Producer
    }, Wire.Json);
    private static string Descriptor(Request request) => Wire.Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { request.Toolchain, request.NativeInput!.WorkingDirectory, request.NativeInput.Include, request.NativeInput.Producer }, Wire.Json));
    internal static void ValidateReferenceHeader(Request request)
    {
        if (request.Version != 6 || request.Operation != "compile" || request.Files is not null || request.NativeInput is null ||
            request.NativePch is not null || !Wire.ValidHash(request.ContextKey) || !Wire.ValidHash(request.Toolchain) ||
            !Guid.TryParseExact(request.Attempt, "N", out _)) throw new Rejected("Invalid native context reference");
        Wire.ValidatePath(request.Source, 512);
        if (!new[] { ".cpp", ".cc", ".cxx" }.Contains(Path.GetExtension(request.Source), StringComparer.OrdinalIgnoreCase))
            throw new Rejected("Expected native C++ source");
        MappedCompile.ValidateOptions(request.Options);
    }
    internal Request Expand(Request reference)
    {
        ValidateReferenceHeader(reference);
        if (reference.ContextKey != Identity || Descriptor(reference) != descriptor || !paths.Contains(reference.Source!))
            throw new Rejected("Native context reference changed its definition");
        reference.NativeInput!.Validate(reference, paths);
        return reference with { Files = Definition.Files };
    }
}
sealed record NativeInvocation(Request Request, NativeContext Context);
