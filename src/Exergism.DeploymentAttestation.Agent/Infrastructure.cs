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
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const uint STATX_TYPE = 0x00000001;
    private const uint STATX_CTIME = 0x00000080;
    private const uint STATX_INO = 0x00000100;
    private const uint STATX_SIZE = 0x00000200;
    private const uint STATX_REQUIRED = STATX_TYPE | STATX_CTIME | STATX_INO | STATX_SIZE;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;

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

    public static VerifiedRegularFile ReadRegularFileNoFollow(string path, string displayPath)
    {
        var fd = OpenRegularFileNoFollow(path, displayPath, out var beforeStat);
        try
        {
            var mode = (UnixFileMode)(beforeStat.Mode & ~S_IFMT);

            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            fd = -1;
            using var stream = new FileStream(handle, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var data = buffer.ToArray();

            var openFd = checked((int)handle.DangerousGetHandle());
            var afterStat = StatRegularFileDescriptor(openFd, displayPath);
            var beforeSnapshot = FileSnapshot.From(beforeStat, data);
            var afterSnapshot = FileSnapshot.From(afterStat, data);
            if (beforeSnapshot != afterSnapshot)
                throw new AgentException($"Tracked regular file changed while being verified: {displayPath}");

            return new VerifiedRegularFile(data, afterSnapshot, mode);
        }
        finally
        {
            if (fd >= 0)
                _ = Native.close(fd);
        }
    }

    public static void FsyncRegularFileNoFollow(
        string path,
        string displayPath,
        FileSnapshot expectedSnapshot)
    {
        var fd = OpenRegularFileNoFollow(path, displayPath, out var beforeStat);
        try
        {
            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            fd = -1;
            using var stream = new FileStream(handle, FileAccess.Read);
            var openFd = checked((int)handle.DangerousGetHandle());

            EnsureSnapshotMetadata(expectedSnapshot, beforeStat, displayPath);
            EnsureSnapshotDigest(expectedSnapshot, stream, displayPath, "before fsync");

            if (Native.fsync(openFd) != 0)
                throw new AgentException($"fsync tracked regular file failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

            var afterStat = StatRegularFileDescriptor(openFd, displayPath);
            EnsureSnapshotMetadata(expectedSnapshot, afterStat, displayPath);
            stream.Position = 0;
            EnsureSnapshotDigest(expectedSnapshot, stream, displayPath, "after fsync");
        }
        finally
        {
            if (fd >= 0)
                _ = Native.close(fd);
        }
    }

    private static LinuxStatx StatRegularFileDescriptor(int fd, string displayPath)
    {
        if (Native.statx(fd, "", AT_EMPTY_PATH, STATX_REQUIRED, out var stat) != 0)
            throw new AgentException($"statx tracked regular file failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

        if ((stat.Mask & STATX_REQUIRED) != STATX_REQUIRED)
            throw new AgentException($"statx omitted tracked file snapshot fields for {displayPath}");

        if ((stat.Mode & S_IFMT) != S_IFREG)
            throw new AgentException($"Tracked path is not a regular file: {displayPath}");

        return stat;
    }

    private static void EnsureSnapshotMetadata(
        FileSnapshot expectedSnapshot,
        LinuxStatx stat,
        string displayPath)
    {
        if (!expectedSnapshot.MatchesMetadata(stat))
            throw new AgentException($"Tracked regular file changed during durability barrier: {displayPath}");
    }

    private static void EnsureSnapshotDigest(
        FileSnapshot expectedSnapshot,
        Stream stream,
        string displayPath,
        string phase)
    {
        stream.Position = 0;
        var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(digest, expectedSnapshot.Sha256, StringComparison.Ordinal))
            throw new AgentException($"Tracked regular file bytes changed {phase}: {displayPath}");
    }

    private static int OpenRegularFileNoFollow(
        string path,
        string displayPath,
        out LinuxStatx stat)
    {
        var fd = Native.open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException($"open tracked regular file failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

        try
        {
            stat = StatRegularFileDescriptor(fd, displayPath);
            return fd;
        }
        catch
        {
            _ = Native.close(fd);
            throw;
        }
    }

    public static void FsyncDirectory(string path)
    {
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            return;

        FsyncRequiredDirectory(path, path);
    }

    public static void FsyncRequiredDirectory(string path, string displayPath)
    {
        if (string.IsNullOrEmpty(path))
            throw new AgentException($"Required directory path is empty during fsync: {displayPath}");

        var fd = Native.open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException($"open required directory failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");
        try
        {
            if (Native.fsync(fd) != 0)
                throw new AgentException($"fsync required directory failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");
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

internal readonly record struct FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode)
{
    internal static FileIdentity From(LinuxStatx stat)
        => new(stat.DeviceMajor, stat.DeviceMinor, stat.Inode);
}

internal readonly record struct FileSnapshot(
    FileIdentity Identity,
    long ChangeTimeSeconds,
    uint ChangeTimeNanoseconds,
    ulong Size,
    string Sha256)
{
    internal static FileSnapshot From(LinuxStatx stat, ReadOnlySpan<byte> data)
        => new(
            FileIdentity.From(stat),
            stat.ChangeTimeSeconds,
            stat.ChangeTimeNanoseconds,
            stat.Size,
            Convert.ToHexStringLower(SHA256.HashData(data)));

    internal bool MatchesMetadata(LinuxStatx stat)
        => Identity == FileIdentity.From(stat) &&
           ChangeTimeSeconds == stat.ChangeTimeSeconds &&
           ChangeTimeNanoseconds == stat.ChangeTimeNanoseconds &&
           Size == stat.Size;
}

internal readonly record struct VerifiedRegularFile(
    byte[] Data,
    FileSnapshot Snapshot,
    UnixFileMode Mode);

[StructLayout(LayoutKind.Explicit, Size = 256)]
internal struct LinuxStatx
{
    [FieldOffset(0)]
    internal uint Mask;

    [FieldOffset(28)]
    internal ushort Mode;

    [FieldOffset(32)]
    internal ulong Inode;

    [FieldOffset(40)]
    internal ulong Size;

    [FieldOffset(96)]
    internal long ChangeTimeSeconds;

    [FieldOffset(104)]
    internal uint ChangeTimeNanoseconds;

    [FieldOffset(136)]
    internal uint DeviceMajor;

    [FieldOffset(140)]
    internal uint DeviceMinor;
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
    internal static extern int statx(
        int dirfd,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathname,
        int flags,
        uint mask,
        out LinuxStatx statxbuf);

    [DllImport("libc", SetLastError = true)]
    internal static extern int close(int fd);
}
