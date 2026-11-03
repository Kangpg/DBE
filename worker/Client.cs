using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Dbe;

sealed class Client : IDisposable
{
    private readonly ClientConfig config;
    private readonly X509Certificate2 certificate;
    internal Client(ClientConfig config, string root)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || config.Port is < 1 or > 65535 || !Wire.ValidHash(config.WorkerPin))
            throw new Rejected("Invalid client configuration");
        this.config = config;
        certificate = Identity.Load(System.IO.Path.Combine(root, config.Certificate));
    }

    internal async Task<(TcpClient Tcp, SslStream Tls)> Connect(CancellationToken token)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(config.Host, config.Port, deadline.Token);
            var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => Identity.Matches(cert, config.WorkerPin));
            try { await tls.AuthenticateAsClientAsync(Identity.Client(certificate), deadline.Token); }
            catch { tls.Dispose(); throw; }
            return (tcp, tls);
        }
        catch { tcp.Dispose(); throw; }
    }

    internal async Task<Capabilities> Info(CancellationToken token, int version = 1)
    {
        var connection = await Connect(token);
        using var tcp = connection.Tcp;
        using var tls = connection.Tls;
        await Wire.Write(tls, new Request(version, "info", "", null, null, null), token);
        Reply reply = await Wire.Read<Reply>(tls, token);
        if (reply.Status != "ready" || reply.Capabilities is null) throw new Rejected("Worker not ready: " + reply.Status);
        return reply.Capabilities;
    }

    internal async Task<Reply> Compile(Request request, string inputRoot, string outputRoot, CancellationToken token, bool nativePaths = false, NativeClientCache? cache = null)
    {
        if (request.Version >= 6 && cache is not null) cache.Validate(request); else Wire.Validate(request);
        outputRoot = System.IO.Path.GetFullPath(outputRoot);
        Wire.NoReparse(outputRoot);
        if (Directory.Exists(outputRoot)) throw new Rejected("Output directory must be new");
        Directory.CreateDirectory(outputRoot);
        bool reference = request.Version >= 6 && cache?.Knows(config.WorkerPin, request.ContextKey!) == true;
        (TcpClient Tcp, SslStream Tls) connection;
        Reply accepted;
        long headerBytesSent = 0;
        for (int negotiation = 0; ; negotiation++)
        {
            connection = await Connect(token);
            try
            {
                headerBytesSent += await Wire.Write(connection.Tls, reference ? request with { Files = null } : request, token);
                accepted = await Wire.Read<Reply>(connection.Tls, token);
            }
            catch { connection.Tls.Dispose(); connection.Tcp.Dispose(); throw; }
            if (accepted.Status != "context-miss" || !reference || negotiation != 0) break;
            if (accepted.Attempt != request.Attempt) { connection.Tls.Dispose(); connection.Tcp.Dispose(); throw new Rejected("Context miss attempt mismatch"); }
            cache!.Forget(config.WorkerPin, request.ContextKey!);
            reference = false; connection.Tls.Dispose(); connection.Tcp.Dispose();
        }
        using var tcp = connection.Tcp;
        using var tls = connection.Tls;
        if (accepted.Status != "accepted") return accepted;
        if (accepted.Attempt != request.Attempt) throw new Rejected("Attempt mismatch");
        int[] upload = request.Version >= 5 ? accepted.MissingInputs ?? throw new Rejected("Missing cache negotiation") : Enumerable.Range(0, request.Files!.Length).ToArray();
        if (upload.Any(i => i < 0 || i >= request.Files!.Length || request.Files[i].Deferred) || upload.Distinct().Count() != upload.Length)
            throw new Rejected("Invalid cache negotiation");
        using (BuildTiming.Measure("rpc.upload"))
        foreach (int index in upload)
        {
            InputFile file = request.Files![index];
            string path = nativePaths ? NativeBuild.Original(file.Path) : System.IO.Path.Combine(inputRoot, file.Path.Replace('/', '\\'));
            Wire.NoReparse(path);
            await Wire.SendFile(tls, path, file, token);
        }
        Reply reply;
        using (BuildTiming.Measure("rpc.await-result"))
            reply = await Wire.Read<Reply>(tls, token);
        if (reply.Attempt != request.Attempt || reply.StdoutBytes is < 0 or > Wire.DiagnosticLimit || reply.StderrBytes is < 0 or > Wire.DiagnosticLimit)
            throw new Rejected("Invalid result framing");
        if (reply.Status == "input-required")
        {
            if (request.Version < 5 || reply.RequiredInputs is not { Length: > 0 } required ||
                required.Distinct().Count() != required.Length || required.Any(i => i < 0 || i >= request.Files!.Length || !request.Files[i].Deferred))
                throw new Rejected("Invalid native input request");
        }
        else if (reply.RequiredInputs is not null) throw new Rejected("Unexpected native input request");
        InputFile[] batchObjects = request.Version == 7 ? NativeBatch.ValidateOutputs(request, reply) : [];
        if (request.Version != 7 && (reply.Outputs is not null || (reply.Status == "completed") != (reply.Output is not null)) ||
            (reply.Status == "completed" && reply.ExitCode != 0)) throw new Rejected("Inconsistent result");
        bool needsPdb = request.ReturnsPdb && reply.Status == "completed";
        if (((request.NativePch is not null || request.NativeInput?.Producer is not null) && reply.Status == "completed") != (reply.ProducerObject is not null) ||
            (reply.ProducerObject is { } producer && (producer.Path != "producer.obj" || producer.Length is <= 0 or > Wire.OutputLimit || !Wire.ValidHash(producer.Sha256))))
            throw new Rejected("Invalid PCH producer object manifest");
        if (needsPdb != (reply.Pdb is not null) || (reply.Pdb is { } pdb &&
            (pdb.Path != "dbe-" + request.Attempt + ".pdb" || pdb.Length is <= 0 or > Wire.PdbLimit || !Wire.ValidHash(pdb.Sha256))))
            throw new Rejected("Invalid PDB manifest");
        bool needsSeed = request.Version >= 6 && request.NativeInput?.Producer is not null && needsPdb;
        if (needsSeed != (reply.ProducerPdb is not null) || reply.ProducerPdb is { } seed &&
            (!seed.Path.StartsWith("dbe-", StringComparison.Ordinal) || !seed.Path.EndsWith(".pdb", StringComparison.Ordinal) ||
             !Guid.TryParseExact(seed.Path[4..^4], "N", out _) || seed.Path == reply.Pdb?.Path ||
             seed.Length is <= 0 or > Wire.PdbLimit || !Wire.ValidHash(seed.Sha256))) throw new Rejected("Invalid producer PDB manifest");
        if (reply.Cache is { } metrics && (request.Version < 6 || metrics.Key != request.ContextKey)) throw new Rejected("Invalid native cache receipt");
        if (request.Version >= 6 && reply.Status == "completed" && reply.Cache is null) throw new Rejected("Missing native cache receipt");
        bool seedHit = reply.ProducerPdb is { } symbols && cache?.Restore(symbols, System.IO.Path.Combine(outputRoot, symbols.Path)) == true;
        if (reply.ProducerPdb is not null) await tls.WriteAsync(new byte[] { seedHit ? (byte)0 : (byte)1 }, token);
        using var receive = BuildTiming.Measure("rpc.receive-publish");
        await ReadDiagnostic(tls, System.IO.Path.Combine(outputRoot, "stdout.bin"), reply.StdoutBytes, token);
        await ReadDiagnostic(tls, System.IO.Path.Combine(outputRoot, "stderr.bin"), reply.StderrBytes, token);
        if (reply.Output is { } output && (output.Path != "result.obj" || output.Length <= 0 ||
            output.Length > (request.Version is 3 or 5 or 6 or 7 ? Wire.PreparedObjectLimit : Wire.OutputLimit) || !Wire.ValidHash(output.Sha256)))
            throw new Rejected("Invalid output manifest");
        var artifacts = batchObjects.Concat(new[] { reply.Output, reply.Pdb, reply.ProducerObject, seedHit ? null : reply.ProducerPdb }.OfType<InputFile>()).ToArray();
        foreach (var item in artifacts)
            await Wire.ReceiveFile(tls, System.IO.Path.Combine(outputRoot, item.Path + ".partial"), item, token);
        foreach (var item in artifacts)
            File.Move(System.IO.Path.Combine(outputRoot, item.Path + ".partial"), System.IO.Path.Combine(outputRoot, item.Path));
        if (reply.ProducerPdb is { } received && !seedHit) cache?.Store(received, System.IO.Path.Combine(outputRoot, received.Path));
        if (request.Version >= 6 && reply.Status == "completed") cache?.Remember(config.WorkerPin, request.ContextKey!);
        await File.WriteAllTextAsync(System.IO.Path.Combine(outputRoot, "result.json"), JsonSerializer.Serialize(new
        {
            request.Attempt, request.Toolchain, request.Version, profile = request.Version switch {
                1 => "explicit-input-no-sdk-v1", 7 => "native-batch-v7", 4 => "native-pch-pair-v4", 5 => "native-mapped-input-v5", 6 => "reusable-native-input-v6", _ => "preprocessed-v" + request.Version },
            producerPdbCacheHit = request.Version >= 6 ? (bool?)seedHit : null,
            producerPdbBytesReceived = reply.ProducerPdb is not null && !seedHit ? reply.ProducerPdb.Length : 0,
            contextReferenceUsed = reference, headerBytesSent, batchFiles = request.BatchSources?.Length,
            inputFilesSent = upload.Length, inputBytesSent = upload.Sum(i => request.Files![i].Length), result = reply
        }, Wire.Json), token);
        return reply;
    }

    private static async Task ReadDiagnostic(Stream stream, string path, int size, CancellationToken token)
    {
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        await File.WriteAllBytesAsync(path, bytes, token);
    }
    public void Dispose() => certificate.Dispose();
}
