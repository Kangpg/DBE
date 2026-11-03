using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;

namespace Dbe;

// Trusted receiving-host OS dependencies. These are ordinary immutable files,
// copied once to the Worker's volume and hard-linked into each private tree.
sealed class RuntimeInputs(string compiler, string root) : IDisposable
{
    private sealed record RuntimeFile(string Cache, string RelativePath);
    private readonly List<FileStream> pins = [];
    private RuntimeFile[]? files;
    private string? unavailable;
    private readonly object gate = new();
    internal void Populate(string inputs)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string destination = Path.Combine(inputs, MappedCompile.InputPath(windows).Replace('/', '\\'));
        if (!Directory.Exists(Path.Combine(inputs, char.ToUpperInvariant(windows[0]).ToString()))) return;
        lock (gate)
        {
            if (unavailable is not null) throw new Rejected("OS runtime unavailable: " + unavailable);
            try { files ??= Prepare(); }
            catch (Exception e) when (e is Rejected or IOException or UnauthorizedAccessException or BadImageFormatException)
            { unavailable = e.Message; throw; }
            Directory.CreateDirectory(destination);
            foreach (var source in files)
            {
                string path = Path.Combine(destination, source.RelativePath.Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path)) throw new Rejected("Native input conflicts with OS runtime");
                if (!CreateHardLink(@"\\?\" + path, @"\\?\" + source.Cache, IntPtr.Zero)) throw new Rejected("Runtime input hard link failed");
            }
        }
    }
    private RuntimeFile[] Prepare()
    {
        Directory.CreateDirectory(root);
        Wire.NoReparse(root);
        string compilerDirectory = Path.GetDirectoryName(compiler)!;
        string system = Environment.SystemDirectory;
        var pending = new Queue<string>(Directory.GetFiles(compilerDirectory).Where(x => Path.GetExtension(x).Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(x).Equals(".exe", StringComparison.OrdinalIgnoreCase)));
        foreach (string name in new[] { "ntdll.dll", "kernel32.dll", "kernelbase.dll", "ucrtbase.dll", "combase.dll",
            "bcrypt.dll", "bcryptprimitives.dll", "cryptbase.dll", "cryptsp.dll", "rsaenh.dll", "ncrypt.dll", "ncryptprov.dll" })
            pending.Enqueue(Path.Combine(system, name));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<RuntimeFile>();
        long bytes = 0;
        while (pending.TryDequeue(out string? source))
        {
            if (!seen.Add(source)) continue;
            Wire.NoReparse(source);
            if (seen.Count > 512) throw new Rejected("OS runtime dependency count exceeds bound");
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var pe = new PEReader(input, PEStreamOptions.LeaveOpen);
            var directory = pe.PEHeaders.PEHeader?.ImportTableDirectory ?? default;
            if (directory.RelativeVirtualAddress != 0)
            {
                var descriptors = pe.GetSectionData(directory.RelativeVirtualAddress).GetReader();
                while (descriptors.RemainingBytes >= 20)
                {
                    int thunk = descriptors.ReadInt32(); descriptors.ReadInt32(); descriptors.ReadInt32();
                    int nameRva = descriptors.ReadInt32(); int address = descriptors.ReadInt32();
                    if (thunk == 0 && nameRva == 0 && address == 0) break;
                    var nameReader = pe.GetSectionData(nameRva).GetReader();
                    List<byte> name = [];
                    while (nameReader.RemainingBytes > 0)
                    {
                        byte value = nameReader.ReadByte();
                        if (value == 0) break;
                        if (name.Count >= 255) throw new Rejected("Invalid runtime dependency name");
                        name.Add(value);
                    }
                    string dependency = Encoding.ASCII.GetString(name.ToArray());
                    if (Path.GetFileName(dependency) != dependency) throw new Rejected("Invalid runtime dependency path");
                    string local = Path.Combine(compilerDirectory, dependency), os = Path.Combine(system, dependency);
                    if (File.Exists(local)) pending.Enqueue(local);
                    else if (File.Exists(os)) pending.Enqueue(os);
                }
            }
            if (!Path.GetDirectoryName(source)!.Equals(system, StringComparison.OrdinalIgnoreCase)) continue;
            bytes += input.Length;
            if (bytes > 512L * 1024 * 1024) throw new Rejected("OS runtime byte budget exceeded");
            string target = Path.Combine(root, Path.GetFileName(source));
            input.Position = 0;
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input));
            if (!File.Exists(target)) { input.Position = 0; using var output = File.Open(target, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); }
            Wire.NoReparse(target);
            var pin = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (pin.Length != input.Length || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pin)) != hash)
            { pin.Dispose(); throw new Rejected("OS runtime cache mismatch"); }
            pins.Add(pin);
            result.Add(new RuntimeFile(target, "System32/" + Path.GetFileName(source)));
        }
        foreach (string source in Directory.GetFiles(system, "*.nls"))
        {
            string name = Path.GetFileName(source), target = Path.Combine(root, name);
            pins.Add(PinDataCopy(source, target)); result.Add(new RuntimeFile(target, "System32/" + name));
        }
        string sorting = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Globalization", "Sorting");
        if (Directory.Exists(sorting))
        foreach (string source in Directory.GetFiles(sorting, "*.nls"))
        {
            string target = Path.Combine(root, "sorting-" + Path.GetFileName(source));
            pins.Add(PinDataCopy(source, target)); result.Add(new RuntimeFile(target, "Globalization/Sorting/" + Path.GetFileName(source)));
        }
        return result.ToArray();
    }
    private static FileStream PinDataCopy(string source, string target)
    {
        Wire.NoReparse(source); Wire.NoReparse(target);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input));
        if (!File.Exists(target))
        {
            input.Position = 0;
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output); // Copy bytes, never the host file's ReadOnly attribute.
        }
        if (new FileInfo(target).Length != input.Length || Wire.HashFile(target) != hash) throw new Rejected("Runtime data cache mismatch");
        // Normalize an owned, validated cache copy left by an older runtime.
        var attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
        var pin = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (pin.Length != input.Length || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pin)) != hash)
        { pin.Dispose(); throw new Rejected("Pinned runtime data changed"); }
        return pin;
    }
    public void Dispose() { foreach (var pin in pins) pin.Dispose(); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string name, string existing, IntPtr attributes);
}
