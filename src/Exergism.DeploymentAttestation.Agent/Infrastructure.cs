using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Exergism.DeploymentAttestation.Agent;

internal static class HttpClientFactory
{
    public static HttpClient CreateGet()
        => new(CreateGetHandler(), disposeHandler: true);

    public static HttpClient CreateAttestation()
        => new(CreateAttestationHandler(), disposeHandler: true);

    internal static SocketsHttpHandler CreateGetHandler()
        => CreateHandler(allowAutoRedirect: true);

    internal static SocketsHttpHandler CreateAttestationHandler()
        => CreateHandler(allowAutoRedirect: false);

    private static SocketsHttpHandler CreateHandler(bool allowAutoRedirect)
        => new()
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = allowAutoRedirect
        };
}

internal sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool clearEnvironment = false)
    {
        using var process = Create(fileName, arguments, workingDirectory, environment, clearEnvironment);
        if (!process.Start())
            throw new AgentException($"Could not start process: {fileName}");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            await process.WaitForExitAsync(cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new AgentException($"Process timed out: {fileName}");
        }

        return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    public static async Task<byte[]> RunBytesAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan? timeout = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        bool clearEnvironment = false)
    {
        using var process = Create(fileName, arguments, workingDirectory, environment, clearEnvironment);
        if (!process.Start())
            throw new AgentException($"Could not start process: {fileName}");

        await using var output = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(output);
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var cts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        try
        {
            await process.WaitForExitAsync(cts?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new AgentException($"Process timed out: {fileName}");
        }

        await copyTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new AgentException($"{fileName} exited {process.ExitCode}: {stderr.Trim()}");
        return output.ToArray();
    }

    private static Process Create(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        bool clearEnvironment)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? "/"
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (clearEnvironment)
            psi.Environment.Clear();
        if (environment is not null)
            foreach (var pair in environment)
                psi.Environment[pair.Key] = pair.Value;

        return new Process { StartInfo = psi };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The caller is already failing closed.
        }
    }
}

internal sealed class AgentLock : IDisposable
{
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private readonly FileStream? _stream;
    private readonly bool _ownsFlock;

    private AgentLock(FileStream? stream, bool ownsFlock)
    {
        _stream = stream;
        _ownsFlock = ownsFlock;
    }

    public static AgentLock? Acquire(string path, bool alreadyHeld, bool recovery)
    {
        if (alreadyHeld)
            return new AgentLock(null, false);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        var fd = checked((int)stream.SafeFileHandle.DangerousGetHandle());
        if (Native.flock(fd, LockExclusive | LockNonBlocking) == 0)
            return new AgentLock(stream, true);

        var error = Marshal.GetLastPInvokeError();
        stream.Dispose();
        if (error is 11 or 35)
        {
            if (recovery)
                throw new AgentException("Cannot coordinate recovery while another agent invocation holds the lock");
            Console.WriteLine("Another coordinator/agent invocation holds the lock");
            return null;
        }
        throw new AgentException($"flock failed for {path}: errno={error}");
    }

    public void Dispose()
    {
        if (_stream is null)
            return;
        if (_ownsFlock)
        {
            var fd = checked((int)_stream.SafeFileHandle.DangerousGetHandle());
            _ = Native.flock(fd, LockUnlock);
        }
        _stream.Dispose();
    }
}

internal static class Durability
{
    private const int ORdOnly = 0;
    private const int ODirectory = 0x10000;

    public static void EnsureDirectory(string target, UnixFileMode mode)
    {
        target = Path.GetFullPath(target);
        var missing = new Stack<string>();
        var current = target;

        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
                throw new AgentException($"unsafe existing ancestor: {current}");
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;
            current = parent;
        }

        if (Directory.Exists(current) && new DirectoryInfo(current).LinkTarget is not null)
            throw new AgentException($"unsafe symlink ancestor: {current}");

        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir, mode);
            File.SetUnixFileMode(dir, mode);
            FsyncDirectory(Path.GetDirectoryName(dir)!);
        }

        File.SetUnixFileMode(target, mode);
        FsyncDirectory(target);
        FsyncDirectory(Path.GetDirectoryName(target)!);
    }

    public static void AtomicWrite(string target, ReadOnlySpan<byte> bytes, UnixFileMode mode)
    {
        var dir = Path.GetDirectoryName(target) ?? throw new AgentException($"No parent directory for {target}");
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(temp, mode);
            FsyncFile(temp);
            File.Move(temp, target, overwrite: true);
            FsyncDirectory(dir);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    public static void AtomicWriteText(string target, string text, UnixFileMode mode)
        => AtomicWrite(target, System.Text.Encoding.UTF8.GetBytes(text), mode);

    public static void DurableDelete(string path)
    {
        if (!File.Exists(path))
            return;
        File.Delete(path);
        FsyncDirectory(Path.GetDirectoryName(path)!);
    }

    public static void FsyncFileAndParent(string path)
    {
        FsyncFile(path);
        FsyncDirectory(Path.GetDirectoryName(path)!);
    }

    public static void FsyncFile(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (Native.fsync(checked((int)handle.DangerousGetHandle())) != 0)
            throw new AgentException($"fsync failed for {path}: errno={Marshal.GetLastPInvokeError()}");
    }

    public static void FsyncDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;
        var fd = Native.open(path, ORdOnly | ODirectory);
        if (fd < 0)
            throw new AgentException($"open directory failed for {path}: errno={Marshal.GetLastPInvokeError()}");
        try
        {
            if (Native.fsync(fd) != 0)
                throw new AgentException($"fsync directory failed for {path}: errno={Marshal.GetLastPInvokeError()}");
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}

internal static class Native
{
    [DllImport("libc", SetLastError = true)]
    internal static extern int flock(int fd, int operation);

    [DllImport("libc", SetLastError = true)]
    internal static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    internal static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);
}
