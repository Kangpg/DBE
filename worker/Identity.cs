using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Dbe;

sealed record WorkerConfig(string BindAddress, int Port, int Slots, int MemoryMiB, int TimeoutSeconds,
    string Compiler, string Certificate, string AllowedClientPin, string StateDirectory);
sealed record ClientConfig(string Host, int Port, string Certificate, string WorkerPin);

static class Identity
{
    // Windows Schannel cannot use EphemeralKeySet here. UserKeySet without
    // PersistKeySet gives it a key container whose lifetime follows this object.
    // No certificate is installed in a trust store.
    internal static X509Certificate2 Load(string file) => X509CertificateLoader.LoadPkcs12FromFile(file, "",
        X509KeyStorageFlags.UserKeySet);
    internal static string Pin(X509Certificate cert) => Wire.Hash(cert.GetRawCertData());
    internal static bool Matches(X509Certificate? cert, string pin)
    {
        if (cert is null || !Wire.ValidHash(pin)) return false;
        using var parsed = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
        return DateTime.UtcNow >= parsed.NotBefore.ToUniversalTime() && DateTime.UtcNow < parsed.NotAfter.ToUniversalTime() &&
            CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pin), SHA256.HashData(cert.GetRawCertData()));
    }

    internal static SslServerAuthenticationOptions Server(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate, ClientCertificateRequired = true,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };
    internal static SslClientAuthenticationOptions Client(X509Certificate2 certificate) => new()
    {
        TargetHost = "dbe-worker", ClientCertificates = new X509CertificateCollection { certificate },
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
    };

    private static X509Certificate2 Create(string name, string eku)
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
    }

    internal static void CreatePair(string root, string compiler, string host = "127.0.0.1", int port = 43127)
    {
        if (Directory.Exists(root)) throw new Rejected("Bundle directory must be new");
        Directory.CreateDirectory(root);
        string worker = System.IO.Path.Combine(root, "worker"), client = System.IO.Path.Combine(root, "client");
        Directory.CreateDirectory(worker); Directory.CreateDirectory(client);
        using var serverCert = Create("dbe-worker", "1.3.6.1.5.5.7.3.1");
        using var clientCert = Create("dbe-agent", "1.3.6.1.5.5.7.3.2");
        File.WriteAllBytes(System.IO.Path.Combine(worker, "identity.pfx"), serverCert.Export(X509ContentType.Pfx, ""));
        File.WriteAllBytes(System.IO.Path.Combine(client, "identity.pfx"), clientCert.Export(X509ContentType.Pfx, ""));
        File.WriteAllText(System.IO.Path.Combine(worker, "worker.json"), JsonSerializer.Serialize(
            new WorkerConfig(host, port, 2, 2048, 120, compiler, "identity.pfx", Pin(clientCert), "state"), Wire.Json));
        File.WriteAllText(System.IO.Path.Combine(client, "client.json"), JsonSerializer.Serialize(
            new ClientConfig(host, port, "identity.pfx", Pin(serverCert)), Wire.Json));
    }

    internal static (T Config, string Root) ReadConfig<T>(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        Wire.NoReparse(path);
        if (new FileInfo(path).Length > Wire.HeaderLimit) throw new Rejected("Config too large");
        return (JsonSerializer.Deserialize<T>(File.ReadAllText(path), Wire.Json) ?? throw new Rejected("Null config"),
            System.IO.Path.GetDirectoryName(path)!);
    }
}
