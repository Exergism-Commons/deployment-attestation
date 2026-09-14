using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Exergism.DeploymentAttestation.Agent;

internal static class HttpClientFactory
{
    public static HttpClient CreateGet()
        => CreateClient(CreateGetHandler());

    public static HttpClient CreateAttestation()
        => CreateClient(CreateAttestationHandler());

    private static HttpClient CreateClient(HttpMessageHandler handler)
        => new(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

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
    private const uint STATX_MODE = 0x00000002;
    private const uint STATX_UID = 0x00000008;
    private const uint STATX_GID = 0x00000010;
    private const uint STATX_MTIME = 0x00000040;
    private const uint STATX_CTIME = 0x00000080;
    private const uint STATX_INO = 0x00000100;
    private const uint STATX_SIZE = 0x00000200;
    private const uint STATX_REGULAR_REQUIRED = STATX_TYPE | STATX_MODE | STATX_CTIME | STATX_INO | STATX_SIZE;
    private const uint STATX_TRUSTED_CONFIG_REQUIRED = STATX_REGULAR_REQUIRED | STATX_UID | STATX_GID;
    private const uint STATX_DIRECTORY_REQUIRED = STATX_TYPE | STATX_MTIME | STATX_CTIME | STATX_INO;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFREG = 0x8000;

    public static string ReadTrustedConfigText(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new AgentException("Configuration path must be absolute");

        path = Path.GetFullPath(path);
        var fd = Native.open(path, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException($"Configuration not readable as a real file: {path}; errno={Marshal.GetLastPInvokeError()}");

        try
        {
            if (Native.statx(fd, "", AT_EMPTY_PATH, STATX_TRUSTED_CONFIG_REQUIRED, out var before) != 0)
                throw new AgentException($"Could not inspect configuration file: {path}; errno={Marshal.GetLastPInvokeError()}");
            if ((before.Mask & STATX_TRUSTED_CONFIG_REQUIRED) != STATX_TRUSTED_CONFIG_REQUIRED ||
                (before.Mode & S_IFMT) != S_IFREG)
                throw new AgentException($"Configuration must be a regular file: {path}");

            var effectiveUid = Native.geteuid();
            if (before.Uid != effectiveUid)
                throw new AgentException($"Configuration must be owned by effective uid {effectiveUid}: {path}");

            var mode = (UnixFileMode)(before.Mode & ~S_IFMT);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new AgentException($"Configuration must not be writable by group or others: {path}");

            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            fd = -1;
            using var stream = new FileStream(handle, FileAccess.Read);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);

            var openFd = checked((int)handle.DangerousGetHandle());
            if (Native.statx(openFd, "", AT_EMPTY_PATH, STATX_TRUSTED_CONFIG_REQUIRED, out var after) != 0)
                throw new AgentException($"Could not re-check configuration file: {path}; errno={Marshal.GetLastPInvokeError()}");

            if (FileSnapshot.From(before, buffer.ToArray()).MatchesMetadata(after) is false ||
                before.Uid != after.Uid ||
                before.Gid != after.Gid ||
                before.Mode != after.Mode)
                throw new AgentException($"Configuration changed while being read: {path}");

            try
            {
                return new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray());
            }
            catch (DecoderFallbackException ex)
            {
                throw new AgentException($"Configuration is not valid UTF-8: {path}", ex);
            }
        }
        finally
        {
            if (fd >= 0)
                _ = Native.close(fd);
        }
    }

    public static void EnsureDirectory(string target, UnixFileMode mode)
    {
        target = Path.GetFullPath(target);
        ValidateExistingDirectoryChainNoSymlinks(target);

        var existed = Directory.Exists(target);
        var missing = new Stack<string>();
        var current = target;

        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
                throw new AgentException($"unsafe existing ancestor: {current}");
            missing.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                throw new AgentException($"cannot resolve existing ancestor for directory: {target}");
            current = parent;
        }

        while (missing.Count > 0)
        {
            var dir = missing.Pop();
            Directory.CreateDirectory(dir, mode);
            File.SetUnixFileMode(dir, mode);
            FsyncRequiredDirectory(dir, dir);
            FsyncRequiredDirectory(
                Path.GetDirectoryName(dir)
                    ?? throw new AgentException($"directory has no parent: {dir}"),
                $"parent of {dir}");
        }

        if (existed)
        {
            var actual = File.GetUnixFileMode(target);
            if (actual != mode)
                throw new AgentException(
                    $"existing directory permissions differ for {target}: expected={mode} actual={actual}");
        }

        ValidateExistingDirectoryChainNoSymlinks(target);
        FsyncRequiredDirectory(target, target);
        FsyncRequiredDirectory(
            Path.GetDirectoryName(target)
                ?? throw new AgentException($"directory has no parent: {target}"),
            $"parent of {target}");
    }

    internal static void ValidateExistingDirectoryChainNoSymlinks(string target)
    {
        var current = Path.GetFullPath(target);
        while (true)
        {
            if (File.Exists(current) && !Directory.Exists(current))
                throw new AgentException($"unsafe non-directory ancestor: {current}");

            if (Directory.Exists(current) &&
                new DirectoryInfo(current).LinkTarget is not null)
                throw new AgentException($"unsafe symlink ancestor: {current}");

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                return;
            current = parent;
        }
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

    public static void FsyncRegularFileNoFollow(string path, string displayPath)
    {
        var fd = OpenRegularFileNoFollow(path, displayPath, out var beforeStat);
        try
        {
            if (Native.fsync(fd) != 0)
                throw new AgentException($"fsync regular file failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

            var afterStat = StatRegularFileDescriptor(fd, displayPath);
            if (FileIdentity.From(beforeStat) != FileIdentity.From(afterStat))
                throw new AgentException($"Regular file identity changed during fsync: {displayPath}");
        }
        finally
        {
            _ = Native.close(fd);
        }
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

    public static FileSnapshot VerifyAndFsyncRegularFileNoFollow(
        string path,
        string displayPath,
        string expectedSha256,
        bool requireExecutable)
    {
        var fd = OpenRegularFileNoFollow(path, displayPath, out var beforeStat);
        try
        {
            using var handle = new SafeFileHandle((nint)fd, ownsHandle: true);
            fd = -1;
            using var stream = new FileStream(handle, FileAccess.Read);
            var openFd = checked((int)handle.DangerousGetHandle());

            var mode = (UnixFileMode)(beforeStat.Mode & ~S_IFMT);
            if (requireExecutable &&
                (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                throw new AgentException($"{displayPath} is not executable");

            var digest = HashStream(stream);
            if (!string.Equals(digest, expectedSha256, StringComparison.Ordinal))
                throw new AgentException($"{displayPath} digest mismatch");

            var afterReadStat = StatRegularFileDescriptor(openFd, displayPath);
            var snapshot = FileSnapshot.FromDigest(afterReadStat, digest);
            if (!snapshot.MatchesMetadata(beforeStat))
                throw new AgentException($"{displayPath} changed while being verified");

            if (Native.fsync(openFd) != 0)
                throw new AgentException($"fsync failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

            var afterFsyncStat = StatRegularFileDescriptor(openFd, displayPath);
            EnsureSnapshotMetadata(snapshot, afterFsyncStat, displayPath);
            stream.Position = 0;
            EnsureSnapshotDigest(snapshot, stream, displayPath, "after fsync");
            return snapshot;
        }
        finally
        {
            if (fd >= 0)
                _ = Native.close(fd);
        }
    }

    public static DirectorySnapshot ReadDirectorySnapshotNoFollow(string path, string displayPath)
    {
        var fd = OpenDirectoryNoFollow(path, displayPath, out var stat);
        try
        {
            return DirectorySnapshot.From(stat);
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    public static void FsyncDirectorySnapshotNoFollow(
        string path,
        string displayPath,
        DirectorySnapshot expectedSnapshot)
    {
        var fd = OpenDirectoryNoFollow(path, displayPath, out var beforeStat);
        try
        {
            if (!expectedSnapshot.Matches(beforeStat))
                throw new AgentException($"Directory changed before fsync: {displayPath}");

            if (Native.fsync(fd) != 0)
                throw new AgentException($"fsync directory failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

            var afterStat = StatDirectoryDescriptor(fd, displayPath);
            if (!expectedSnapshot.Matches(afterStat))
                throw new AgentException($"Directory changed during fsync: {displayPath}");
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static LinuxStatx StatRegularFileDescriptor(int fd, string displayPath)
    {
        if (Native.statx(fd, "", AT_EMPTY_PATH, STATX_REGULAR_REQUIRED, out var stat) != 0)
            throw new AgentException($"statx tracked regular file failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

        if ((stat.Mask & STATX_REGULAR_REQUIRED) != STATX_REGULAR_REQUIRED)
            throw new AgentException($"statx omitted tracked file snapshot fields for {displayPath}");

        if ((stat.Mode & S_IFMT) != S_IFREG)
            throw new AgentException($"Tracked path is not a regular file: {displayPath}");

        return stat;
    }

    private static LinuxStatx StatDirectoryDescriptor(int fd, string displayPath)
    {
        if (Native.statx(fd, "", AT_EMPTY_PATH, STATX_DIRECTORY_REQUIRED, out var stat) != 0)
            throw new AgentException($"statx directory failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

        if ((stat.Mask & STATX_DIRECTORY_REQUIRED) != STATX_DIRECTORY_REQUIRED)
            throw new AgentException($"statx omitted directory snapshot fields for {displayPath}");

        if ((stat.Mode & S_IFMT) != S_IFDIR)
            throw new AgentException($"Path is not a directory: {displayPath}");

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
        var digest = HashStream(stream);
        if (!string.Equals(digest, expectedSnapshot.Sha256, StringComparison.Ordinal))
            throw new AgentException($"Tracked regular file bytes changed {phase}: {displayPath}");
    }

    private static string HashStream(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static int OpenDirectoryNoFollow(
        string path,
        string displayPath,
        out LinuxStatx stat)
    {
        var fd = Native.open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException($"open directory failed for {displayPath}: errno={Marshal.GetLastPInvokeError()}");

        try
        {
            stat = StatDirectoryDescriptor(fd, displayPath);
            return fd;
        }
        catch
        {
            _ = Native.close(fd);
            throw;
        }
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

        var fd = Native.open(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
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
        => FromDigest(stat, Convert.ToHexStringLower(SHA256.HashData(data)));

    internal static FileSnapshot FromDigest(LinuxStatx stat, string sha256)
        => new(
            FileIdentity.From(stat),
            stat.ChangeTimeSeconds,
            stat.ChangeTimeNanoseconds,
            stat.Size,
            sha256);

    internal bool MatchesMetadata(LinuxStatx stat)
        => Identity == FileIdentity.From(stat) &&
           ChangeTimeSeconds == stat.ChangeTimeSeconds &&
           ChangeTimeNanoseconds == stat.ChangeTimeNanoseconds &&
           Size == stat.Size;
}

internal readonly record struct DirectorySnapshot(
    FileIdentity Identity,
    long ChangeTimeSeconds,
    uint ChangeTimeNanoseconds,
    long ModificationTimeSeconds,
    uint ModificationTimeNanoseconds)
{
    internal static DirectorySnapshot From(LinuxStatx stat)
        => new(
            FileIdentity.From(stat),
            stat.ChangeTimeSeconds,
            stat.ChangeTimeNanoseconds,
            stat.ModificationTimeSeconds,
            stat.ModificationTimeNanoseconds);

    internal bool Matches(LinuxStatx stat)
        => Identity == FileIdentity.From(stat) &&
           ChangeTimeSeconds == stat.ChangeTimeSeconds &&
           ChangeTimeNanoseconds == stat.ChangeTimeNanoseconds &&
           ModificationTimeSeconds == stat.ModificationTimeSeconds &&
           ModificationTimeNanoseconds == stat.ModificationTimeNanoseconds;
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

    [FieldOffset(20)]
    internal uint Uid;

    [FieldOffset(24)]
    internal uint Gid;

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

    [FieldOffset(112)]
    internal long ModificationTimeSeconds;

    [FieldOffset(120)]
    internal uint ModificationTimeNanoseconds;

    [FieldOffset(136)]
    internal uint DeviceMajor;

    [FieldOffset(140)]
    internal uint DeviceMinor;
}

internal static class Native
{
    [DllImport("libc")]
    internal static extern uint geteuid();

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
