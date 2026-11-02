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
        var tcp = new TcpClient();
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

    internal async Task<Reply> Compile(Request request, string inputRoot, string outputRoot, CancellationToken token)
    {
        Wire.Validate(request);
        outputRoot = System.IO.Path.GetFullPath(outputRoot);
        Wire.NoReparse(outputRoot);
        if (Directory.Exists(outputRoot)) throw new Rejected("Output directory must be new");
        Directory.CreateDirectory(outputRoot);
        var connection = await Connect(token);
        using var tcp = connection.Tcp;
        using var tls = connection.Tls;
        await Wire.Write(tls, request, token);
        Reply accepted = await Wire.Read<Reply>(tls, token);
        if (accepted.Status != "accepted") return accepted;
        if (accepted.Attempt != request.Attempt) throw new Rejected("Attempt mismatch");
        using (BuildTiming.Measure("rpc.upload"))
        foreach (InputFile file in request.Files!)
        {
            string path = System.IO.Path.Combine(inputRoot, file.Path.Replace('/', '\\'));
            Wire.NoReparse(path);
            await Wire.SendFile(tls, path, file, token);
        }
        Reply reply;
        using (BuildTiming.Measure("rpc.await-result"))
            reply = await Wire.Read<Reply>(tls, token);
        if (reply.Attempt != request.Attempt || reply.StdoutBytes is < 0 or > Wire.DiagnosticLimit || reply.StderrBytes is < 0 or > Wire.DiagnosticLimit)
            throw new Rejected("Invalid result framing");
        if ((reply.Status == "completed") != (reply.Output is not null) ||
            (reply.Status == "completed" && reply.ExitCode != 0)) throw new Rejected("Inconsistent result");
        bool needsPdb = request.ReturnsPdb && reply.Status == "completed";
        if ((request.NativePch is not null && reply.Status == "completed") != (reply.ProducerObject is not null) ||
            (reply.ProducerObject is { } producer && (producer.Path != "producer.obj" || producer.Length is <= 0 or > Wire.OutputLimit || !Wire.ValidHash(producer.Sha256))))
            throw new Rejected("Invalid PCH producer object manifest");
        if (needsPdb != (reply.Pdb is not null) || (reply.Pdb is { } pdb &&
            (pdb.Path != "dbe-" + request.Attempt + ".pdb" || pdb.Length is <= 0 or > Wire.PdbLimit || !Wire.ValidHash(pdb.Sha256))))
            throw new Rejected("Invalid PDB manifest");
        using var receive = BuildTiming.Measure("rpc.receive-publish");
        await ReadDiagnostic(tls, System.IO.Path.Combine(outputRoot, "stdout.bin"), reply.StdoutBytes, token);
        await ReadDiagnostic(tls, System.IO.Path.Combine(outputRoot, "stderr.bin"), reply.StderrBytes, token);
        if (reply.Output is { } output && (output.Path != "result.obj" || output.Length <= 0 ||
            output.Length > (request.Version == 3 ? Wire.PreparedObjectLimit : Wire.OutputLimit) || !Wire.ValidHash(output.Sha256)))
            throw new Rejected("Invalid output manifest");
        var artifacts = new[] { reply.Output, reply.Pdb, reply.ProducerObject }.OfType<InputFile>().ToArray();
        foreach (var item in artifacts)
            await Wire.ReceiveFile(tls, System.IO.Path.Combine(outputRoot, item.Path + ".partial"), item, token);
        foreach (var item in artifacts)
            File.Move(System.IO.Path.Combine(outputRoot, item.Path + ".partial"), System.IO.Path.Combine(outputRoot, item.Path));
        await File.WriteAllTextAsync(System.IO.Path.Combine(outputRoot, "result.json"), JsonSerializer.Serialize(new
        {
            request.Attempt, request.Toolchain, request.Version, profile = request.Version switch {
                1 => "explicit-input-no-sdk-v1", 4 => "native-pch-pair-v4", _ => "preprocessed-v" + request.Version }, result = reply
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
