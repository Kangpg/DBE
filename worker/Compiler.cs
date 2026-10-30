using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Dbe;

// Prototype identity covers the locked Hostx64/x64 executable/DLL directory.
// It is NOT an SDK/header/toolchain closure. /X + explicit inputs is the only profile.
sealed class Compiler : IDisposable
{
    internal string Path { get; }
    internal string Identity { get; }
    internal string Version { get; }
    private readonly List<FileStream> pins = [];

    internal Compiler(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        Wire.NoReparse(Path);
        if (!File.Exists(Path) || !System.IO.Path.GetFileName(Path).Equals("cl.exe", StringComparison.OrdinalIgnoreCase))
            throw new Rejected("Compiler must be an installed absolute cl.exe");
        try
        {
            string directory = System.IO.Path.GetDirectoryName(Path)!;
            var files = Directory.GetFiles(directory).Where(p => System.IO.Path.GetExtension(p).ToLowerInvariant() is ".dll" or ".exe")
                .OrderBy(p => System.IO.Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToArray();
            StringBuilder identity = new("dbe-msvc-explicit-input-v1\n");
            foreach (string file in files)
            {
                Wire.NoReparse(file);
                var pin = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                pins.Add(pin);
                string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(pin));
                identity.Append(System.IO.Path.GetFileName(file).ToLowerInvariant()).Append(':').Append(hash).Append('\n');
            }
            Identity = Wire.Hash(Encoding.UTF8.GetBytes(identity.ToString()));
            Version = FileVersionInfo.GetVersionInfo(Path).FileVersion ?? "unknown";
        }
        catch { Dispose(); throw; }
    }

    internal Task<Execution> Run(string workspace, string source, int seconds, int memoryMiB, CancellationToken token, string[]? options = null, string? pdbName = null) => Task.Run(() =>
    {
        string output = System.IO.Path.Combine(workspace, "result.obj");
        string temporary = System.IO.Path.Combine(workspace, "tmp");
        Directory.CreateDirectory(temporary);
        string input = System.IO.Path.Combine(workspace, "inputs", source.Replace('/', '\\'));
        string profile = options is null ? "/std:c++20 /O2 /Z7 /utf-8" : string.Join(' ', options);
        string args = $"/nologo /c /TP {profile} /X /Fo\"{output}\" \"{input}\"";
        if (pdbName is not null) args += " /Fd" + ClInvocation.Quote(System.IO.Path.Combine(workspace, pdbName));
        return Native.Run(Path, workspace, args, seconds, memoryMiB, token);
    });

    public void Dispose() { foreach (var pin in pins) pin.Dispose(); pins.Clear(); }
}

sealed record Execution(uint Kind, uint ExitCode, uint Error, uint ProcessId, byte[] Stdout, byte[] Stderr);
static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Result { public uint Kind, ExitCode, Error, ProcessId, StdoutSize, StderrSize; }
    [DllImport("dbe-process.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void DbeRun(string exe, string cwd, string args, string? environment, SafeWaitHandle cancel,
        uint timeout, uint memory, [Out] byte[] stdout, [Out] byte[] stderr, uint capacity, out Result result);

    internal static Execution Run(string exe, string cwd, string args, int seconds, int memoryMiB, CancellationToken token, bool inheritEnvironment = false)
    {
        using var cancel = new EventWaitHandle(false, EventResetMode.ManualReset);
        using var registration = token.Register(() => cancel.Set());
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["WINDIR"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            ["PATH"] = System.IO.Path.GetDirectoryName(exe) + ";" + Environment.SystemDirectory,
            ["TEMP"] = System.IO.Path.Combine(cwd, "tmp"), ["TMP"] = System.IO.Path.Combine(cwd, "tmp"),
            ["VSLANG"] = "1033"
        };
        string block = string.Join('\0', environment.Select(p => p.Key + "=" + p.Value)) + "\0\0";
        byte[] stdout = new byte[Wire.DiagnosticLimit], stderr = new byte[Wire.DiagnosticLimit];
        DbeRun(exe, cwd, args, inheritEnvironment ? null : block, cancel.SafeWaitHandle, (uint)seconds * 1000, (uint)memoryMiB,
            stdout, stderr, Wire.DiagnosticLimit, out var result);
        return new Execution(result.Kind, result.ExitCode, result.Error, result.ProcessId,
            stdout[..(int)result.StdoutSize], stderr[..(int)result.StderrSize]);
    }
}
