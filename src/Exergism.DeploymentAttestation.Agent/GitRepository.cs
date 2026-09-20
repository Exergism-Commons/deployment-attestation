using System.Security.Cryptography;
using System.Text;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal static class TrackedFileDurability
{
    internal static VerifiedRegularFile ReadVerifiedRegularFile(string path, string relativePath)
        => Durability.ReadRegularFileNoFollow(path, relativePath);

    internal static void FsyncRegularFile(
        string path,
        string relativePath,
        FileSnapshot expectedSnapshot)
        => Durability.FsyncRegularFileNoFollow(path, relativePath, expectedSnapshot);
}

internal sealed record RepositoryDurabilitySnapshot(
    Dictionary<string, FileSnapshot> Files,
    Dictionary<string, DirectorySnapshot> Directories)
{
    internal Dictionary<string, string> Symlinks { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, RepositoryFileSetSnapshot> FileSets { get; } =
        new(StringComparer.Ordinal);

    internal Dictionary<string, string> Heads { get; } =
        new(StringComparer.Ordinal);
}

internal sealed record RepositoryFileSetSnapshot(
    IReadOnlySet<string> Files,
    IReadOnlySet<string> Directories,
    IReadOnlySet<string> Submodules);

internal sealed record RepositoryDiskTreeSnapshot(
    HashSet<string> Files,
    HashSet<string> Directories);

internal sealed record GitMetadataDurabilitySnapshot(
    Dictionary<string, FileSnapshot> Files,
    Dictionary<string, DirectorySnapshot> Directories);

internal static class SubmoduleGitMarkerDurability
{
    internal static void Capture(
        string path,
        string displayPath,
        RepositoryDurabilitySnapshot verified)
    {
        path = Path.GetFullPath(path);
        if (File.Exists(path))
        {
            verified.Files[path] =
                Durability.ReadRegularFileNoFollow(path, displayPath).Snapshot;
            return;
        }

        if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
        {
            verified.Directories[path] =
                Durability.ReadDirectorySnapshotNoFollow(path, displayPath);
            return;
        }

        throw new AgentException($"Submodule Git metadata marker is missing or unsafe: {displayPath}");
    }

    internal static void Fsync(
        string path,
        string displayPath,
        RepositoryDurabilitySnapshot verified)
    {
        path = Path.GetFullPath(path);
        if (verified.Files.TryGetValue(path, out var expectedFile))
        {
            Durability.FsyncRegularFileNoFollow(path, displayPath, expectedFile);
            return;
        }

        if (verified.Directories.TryGetValue(path, out var expectedDirectory))
        {
            Durability.FsyncDirectorySnapshotNoFollow(path, displayPath, expectedDirectory);
            return;
        }

        throw new AgentException($"Submodule Git metadata marker was not snapshot-verified: {displayPath}");
    }
}

internal static class GitMetadataDurability
{
    internal static GitMetadataDurabilitySnapshot Capture(string root)
    {
        root = Path.GetFullPath(root);
        CheckoutWriteExclusion.ValidateMetadataTree(root);
        var directories = new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal)
        {
            [root] = Durability.ReadDirectorySnapshotNoFollow(root, root)
        };
        foreach (var directory in GitMetadataEnumeration.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var fullDirectory = Path.GetFullPath(directory);
            directories[fullDirectory] =
                Durability.ReadDirectorySnapshotNoFollow(fullDirectory, fullDirectory);
        }

        var files = new Dictionary<string, FileSnapshot>(StringComparer.Ordinal);
        foreach (var file in GitMetadataEnumeration.EnumerateFiles(root).Order(StringComparer.Ordinal))
        {
            var fullFile = Path.GetFullPath(file);
            files[fullFile] =
                Durability.ReadRegularFileNoFollow(fullFile, fullFile).Snapshot;
        }

        return new GitMetadataDurabilitySnapshot(files, directories);
    }

    internal static void EnsureUnchanged(
        GitMetadataDurabilitySnapshot expected,
        GitMetadataDurabilitySnapshot actual)
    {
        EnsureMapUnchanged(expected.Files, actual.Files, "Git metadata file");
        EnsureMapUnchanged(expected.Directories, actual.Directories, "Git metadata directory");
    }

    internal static void Fsync(string root, GitMetadataDurabilitySnapshot expected)
    {
        root = Path.GetFullPath(root);

        // Bind the durability barrier to a stable metadata snapshot before the
        // first fsync. A writer that changes and restores bytes still changes
        // ctime and therefore cannot silently substitute an intermediate state.
        EnsureUnchanged(expected, Capture(root));

        foreach (var pair in expected.Files.OrderBy(x => x.Key, StringComparer.Ordinal))
            Durability.FsyncRegularFileNoFollow(pair.Key, pair.Key, pair.Value);

        foreach (var pair in expected.Directories
                     .Where(x => !string.Equals(x.Key, root, StringComparison.Ordinal))
                     .OrderByDescending(x => x.Key.Count(c => c == Path.DirectorySeparatorChar))
                     .ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            Durability.FsyncDirectorySnapshotNoFollow(pair.Key, pair.Key, pair.Value);
        }

        if (!expected.Directories.TryGetValue(root, out var rootSnapshot))
            throw new AgentException($"Git metadata root was not snapshot-verified: {root}");
        Durability.FsyncDirectorySnapshotNoFollow(root, root, rootSnapshot);

        // Re-read the tree after all fsync calls so a concurrent writer cannot
        // restore expected bytes after an intermediate state was made durable.
        EnsureUnchanged(expected, Capture(root));

        var parent = Path.GetDirectoryName(root)
            ?? throw new AgentException($"Git metadata root has no parent: {root}");
        Durability.FsyncRequiredDirectory(parent, parent);
    }

    private static void EnsureMapUnchanged<T>(
        IReadOnlyDictionary<string, T> expected,
        IReadOnlyDictionary<string, T> actual,
        string kind)
        where T : notnull
    {
        if (expected.Count != actual.Count)
            throw new AgentException($"{kind} set changed during durability barrier");

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var snapshot) ||
                !EqualityComparer<T>.Default.Equals(pair.Value, snapshot))
                throw new AgentException($"{kind} changed during durability barrier: {pair.Key}");
        }
    }
}

internal enum GitMetadataTraversalMode
{
    StrictTargetCommit,
    RecoveryCurrentCheckout,
    RecoveryRollbackTarget
}

internal static class GitMetadataEnumeration
{
    internal static IEnumerable<string> EnumerateFiles(string root)
        => Directory.EnumerateFiles(
            root,
            "*",
            RecursiveNoFollowOptions());

    internal static IEnumerable<string> EnumerateDirectories(string root)
        => Directory.EnumerateDirectories(
            root,
            "*",
            RecursiveNoFollowOptions());

    internal static IEnumerable<string> EnumerateLockFiles(string root)
        => Directory.EnumerateFiles(
            root,
            "*.lock",
            RecursiveNoFollowOptions());

    private static EnumerationOptions RecursiveNoFollowOptions()
        => new()
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false
        };
}

internal enum CheckoutSealState
{
    Unsealed,
    FullySealed,
    PartiallySealed
}

internal static class CheckoutWriteExclusion
{
    private const int O_RDONLY = 0;
    private const int O_NONBLOCK = 0x800;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int AT_FDCWD = -100;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_TYPE = 0x00000001;
    private const uint STATX_NLINK = 0x00000004;
    private const uint STATX_BASIC_STATS = 0x000007ff;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFLNK = 0xA000;
    private const int O_ACCMODE = 0x3;
    private const int O_WRONLY = 0x1;
    private const int O_RDWR = 0x2;
    private const int FS_IMMUTABLE_FL = 0x00000010;

    // Linux UAPI: _IOR('f', 1, long) / _IOW('f', 2, long) on 64-bit Linux.
    private const ulong FS_IOC_GETFLAGS = 0x80086601;
    private const ulong FS_IOC_SETFLAGS = 0x40086602;

    internal static void ValidateMetadataTree(string root)
        => _ = EnumerateTreeTargets(root, rejectSymlinks: true, rejectSpecial: true);

    internal static void SealVerifiedState(
        RepositoryDurabilitySnapshot repository,
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> metadata)
    {
        var files = new HashSet<string>(repository.Files.Keys, StringComparer.Ordinal);
        var directories = new HashSet<string>(repository.Directories.Keys, StringComparer.Ordinal);

        foreach (var snapshot in metadata.Values)
        {
            files.UnionWith(snapshot.Files.Keys);
            directories.UnionWith(snapshot.Directories.Keys);
        }

        EnsureNoMultiplyLinkedFiles(files);

        var orderedFiles = files.Order(StringComparer.Ordinal).ToArray();
        var orderedDirectories = directories
            .OrderBy(PathDepth)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var newlySealed = new List<string>();

        try
        {
            // Freeze the namespace from the repository roots downward before
            // opening regular files. This prevents rename/unlink substitution
            // while individual file inodes are being sealed.
            foreach (var directory in orderedDirectories)
            {
                if (SetImmutableNoFollow(directory, immutable: true))
                    newlySealed.Add(directory);
            }

            foreach (var file in orderedFiles)
            {
                if (SetImmutableNoFollow(file, immutable: true))
                    newlySealed.Add(file);
            }
        }
        catch (Exception sealFailure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var path in newlySealed.AsEnumerable().Reverse())
            {
                try
                {
                    _ = SetImmutableNoFollow(path, immutable: false);
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailures.Add(rollbackFailure);
                }
            }

            if (rollbackFailures.Count > 0)
                throw new AgentException(
                    "Checkout sealing failed and partial seal rollback also failed",
                    new AggregateException(new[] { sealFailure }.Concat(rollbackFailures)));
            throw;
        }
    }

    internal static void EnsureVerifiedStateSealed(
        RepositoryDurabilitySnapshot repository,
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> metadata)
    {
        foreach (var file in repository.Files.Keys)
            EnsureImmutableNoFollow(file);
        foreach (var directory in repository.Directories.Keys)
            EnsureImmutableNoFollow(directory);

        foreach (var snapshot in metadata.Values)
        {
            foreach (var file in snapshot.Files.Keys)
                EnsureImmutableNoFollow(file);
            foreach (var directory in snapshot.Directories.Keys)
                EnsureImmutableNoFollow(directory);
        }
    }

    internal static void SetTreeImmutable(string root, bool immutable)
    {
        var (files, directories) = EnumerateTreeTargets(
            root,
            rejectSymlinks: false,
            rejectSpecial: false);

        if (!immutable)
        {
            foreach (var file in files.Order(StringComparer.Ordinal))
                _ = SetImmutableNoFollow(file, immutable: false);

            foreach (var directory in directories
                         .OrderByDescending(PathDepth)
                         .ThenBy(path => path, StringComparer.Ordinal))
                _ = SetImmutableNoFollow(directory, immutable: false);
            return;
        }

        EnsureNoMultiplyLinkedFiles(files);
        var orderedDirectories = directories
            .OrderBy(PathDepth)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var orderedFiles = files.Order(StringComparer.Ordinal).ToArray();
        var newlySealed = new List<string>();

        try
        {
            foreach (var directory in orderedDirectories)
            {
                if (SetImmutableNoFollow(directory, immutable: true))
                    newlySealed.Add(directory);
            }

            foreach (var file in orderedFiles)
            {
                if (SetImmutableNoFollow(file, immutable: true))
                    newlySealed.Add(file);
            }
        }
        catch (Exception sealFailure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var path in newlySealed.AsEnumerable().Reverse())
            {
                try
                {
                    _ = SetImmutableNoFollow(path, immutable: false);
                }
                catch (Exception rollbackFailure)
                {
                    rollbackFailures.Add(rollbackFailure);
                }
            }

            if (rollbackFailures.Count > 0)
                throw new AgentException(
                    "Tree reseal failed and partial reseal rollback also failed",
                    new AggregateException(new[] { sealFailure }.Concat(rollbackFailures)));
            throw;
        }
    }

    internal static CheckoutSealState InspectTreeSealState(string root)
    {
        var (files, directories) = EnumerateTreeTargets(
            root,
            rejectSymlinks: false,
            rejectSpecial: false);
        var states = files
            .Concat(directories)
            .Select(IsImmutableNoFollow)
            .ToArray();
        return ClassifySealState(states);
    }

    internal static CheckoutSealState ClassifySealState(IEnumerable<bool> immutableStates)
    {
        var any = false;
        var all = true;
        foreach (var immutable in immutableStates)
        {
            any |= immutable;
            all &= immutable;
        }

        if (!any)
            return CheckoutSealState.Unsealed;
        return all
            ? CheckoutSealState.FullySealed
            : CheckoutSealState.PartiallySealed;
    }

    internal static void AssertNoWritableReferences(
        IEnumerable<string> roots,
        RepositoryDurabilitySnapshot repository,
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> metadata)
    {
        var protectedRoots = roots
            .Select(Path.GetFullPath)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var protectedFiles = new HashSet<FileIdentity>(
            repository.Files.Values.Select(snapshot => snapshot.Identity));
        foreach (var snapshot in metadata.Values)
            protectedFiles.UnionWith(snapshot.Files.Values.Select(file => file.Identity));

        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out _))
                continue;

            AssertNoWritableDescriptors(procDir, protectedRoots, protectedFiles);
            AssertNoWritableSharedMappings(procDir, protectedRoots, protectedFiles);
        }
    }

    private static void AssertNoWritableDescriptors(
        string procDir,
        IReadOnlyCollection<string> protectedRoots,
        IReadOnlySet<FileIdentity> protectedFiles)
    {
        var fdDirectory = Path.Combine(procDir, "fd");
        try
        {
            foreach (var fdPath in Directory.EnumerateFileSystemEntries(fdDirectory))
            {
                var fdName = Path.GetFileName(fdPath);
                var fdInfo = Path.Combine(procDir, "fdinfo", fdName);
                string flagsLine;
                try
                {
                    flagsLine = File.ReadLines(fdInfo)
                        .FirstOrDefault(line => line.StartsWith("flags:", StringComparison.Ordinal))
                        ?? string.Empty;
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { return; }

                if (!IsWritableDescriptor(flagsLine))
                    continue;

                string? target;
                try
                {
                    target = new FileInfo(fdPath).LinkTarget;
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { return; }

                if (string.IsNullOrEmpty(target) || !target.StartsWith('/'))
                    continue;

                target = StripDeletedSuffix(target);
                var protectedByPath = protectedRoots.Any(root => IsUnder(target, root));
                var descriptorIdentity = TryReadDescriptorIdentity(fdPath);
                var protectedByIdentity = descriptorIdentity is not null &&
                                          protectedFiles.Contains(descriptorIdentity.Value);
                if (protectedByPath || protectedByIdentity)
                    throw new AgentException(
                        $"Writable file descriptor {Path.GetFileName(procDir)}/{fdName} references sealed checkout inode {target}");
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException)
        {
            throw new AgentException(
                $"Cannot inspect writable file descriptors for process {Path.GetFileName(procDir)}");
        }
    }

    private static void AssertNoWritableSharedMappings(
        string procDir,
        IReadOnlyCollection<string> protectedRoots,
        IReadOnlySet<FileIdentity> protectedFiles)
    {
        var mapsPath = Path.Combine(procDir, "maps");
        try
        {
            foreach (var line in File.ReadLines(mapsPath))
            {
                var fields = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 6 || fields[1].Length < 4)
                    continue;

                var permissions = fields[1];
                if (permissions[1] != 'w' || permissions[3] != 's')
                    continue;

                var target = StripDeletedSuffix(fields[5]);
                var protectedByPath = target.StartsWith('/') &&
                                      protectedRoots.Any(root => IsUnder(target, root));
                var mappingIdentity = TryParseMappingIdentity(fields[3], fields[4]);
                var protectedByIdentity = mappingIdentity is not null &&
                                          protectedFiles.Contains(mappingIdentity.Value);

                if (protectedByPath || protectedByIdentity)
                    throw new AgentException(
                        $"Writable shared mapping in process {Path.GetFileName(procDir)} references sealed checkout inode {target}");
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (UnauthorizedAccessException)
        {
            throw new AgentException(
                $"Cannot inspect writable mappings for process {Path.GetFileName(procDir)}");
        }
    }

    private static FileIdentity? TryReadDescriptorIdentity(string fdPath)
    {
        if (Native.statx(
                AT_FDCWD,
                fdPath,
                0,
                STATX_BASIC_STATS,
                out var stat) == 0)
            return FileIdentity.From(stat);

        var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        if (error is 2 or 3)
            return null;

        throw new AgentException(
            $"Cannot inspect writable descriptor identity {fdPath}; errno={error}");
    }

    internal static FileIdentity? TryParseMappingIdentity(string device, string inode)
    {
        if (!ulong.TryParse(
                inode,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsedInode) ||
            parsedInode == 0)
            return null;

        var separator = device.IndexOf(':');
        if (separator <= 0 || separator == device.Length - 1)
            return null;

        if (!uint.TryParse(
                device[..separator],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var major) ||
            !uint.TryParse(
                device[(separator + 1)..],
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var minor))
            return null;

        return new FileIdentity(major, minor, parsedInode);
    }

    internal static bool IsWritableDescriptor(string flagsLine)
    {
        if (string.IsNullOrWhiteSpace(flagsLine))
            return false;

        var value = flagsLine["flags:".Length..].Trim();
        try
        {
            var flags = Convert.ToInt32(value, 8);
            var accessMode = flags & O_ACCMODE;
            return accessMode is O_WRONLY or O_RDWR;
        }
        catch (FormatException)
        {
            throw new AgentException($"Malformed /proc fd flags: {flagsLine}");
        }
        catch (OverflowException)
        {
            throw new AgentException($"Unsafe /proc fd flags: {flagsLine}");
        }
    }

    private static (List<string> Files, List<string> Directories) EnumerateTreeTargets(
        string root,
        bool rejectSymlinks,
        bool rejectSpecial)
    {
        root = Path.GetFullPath(root);
        if (PathKindNoFollow(root) != S_IFDIR)
            throw new AgentException($"Write-exclusion root is not a real directory: {root}");

        var files = new List<string>();
        var directories = new List<string>();
        Walk(root);
        directories.Add(root);
        return (files, directories);

        void Walk(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var kind = PathKindNoFollow(entry);
                if (kind == S_IFLNK)
                {
                    if (rejectSymlinks)
                        throw new AgentException($"Git metadata symlink is not permitted: {entry}");
                    continue;
                }

                if (kind == S_IFREG)
                {
                    files.Add(Path.GetFullPath(entry));
                    continue;
                }

                if (kind == S_IFDIR)
                {
                    Walk(entry);
                    directories.Add(Path.GetFullPath(entry));
                    continue;
                }

                if (rejectSpecial)
                    throw new AgentException($"Git metadata contains unsupported special entry: {entry}");
            }
        }
    }

    private static ushort PathKindNoFollow(string path)
    {
        if (Native.statx(
                AT_FDCWD,
                path,
                AT_SYMLINK_NOFOLLOW,
                STATX_TYPE,
                out var stat) != 0)
            throw new AgentException(
                $"Could not inspect checkout path without following symlinks: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        if ((stat.Mask & STATX_TYPE) != STATX_TYPE)
            throw new AgentException($"statx omitted checkout path type: {path}");

        return (ushort)(stat.Mode & S_IFMT);
    }

    private static void EnsureNoMultiplyLinkedFiles(IEnumerable<string> files)
    {
        foreach (var file in files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            EnsureSingleLinkNoFollow(file);
    }

    internal static void EnsureSingleLinkNoFollow(string path)
    {
        var fd = Native.open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException(
                $"Could not open checkout file to inspect hardlinks: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        try
        {
            EnsureDescriptorSingleLink(fd, path);
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    internal static void EnsureSingleLinkCount(uint linkCount, string path)
    {
        if (linkCount != 1)
            throw new AgentException(
                $"Checkout regular file has {linkCount} hardlinks and cannot be safely write-excluded: {path}");
    }

    private static void EnsureDescriptorSingleLink(int fd, string path)
    {
        if (Native.statx(
                fd,
                "",
                AT_EMPTY_PATH,
                STATX_TYPE | STATX_NLINK,
                out var stat) != 0)
            throw new AgentException(
                $"Could not inspect checkout hardlink count: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        if ((stat.Mask & (STATX_TYPE | STATX_NLINK)) != (STATX_TYPE | STATX_NLINK))
            throw new AgentException($"statx omitted hardlink fields for checkout path: {path}");

        if ((stat.Mode & S_IFMT) == S_IFREG)
            EnsureSingleLinkCount(stat.LinkCount, path);
    }

    private static bool SetImmutableNoFollow(string path, bool immutable)
    {
        var fd = Native.open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException(
                $"Could not open checkout path for write exclusion: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        try
        {
            EnsureDescriptorIsRegularOrDirectory(fd, path);

            var flags = 0;
            if (Native.ioctl(fd, FS_IOC_GETFLAGS, ref flags) != 0)
                throw new AgentException(
                    $"Filesystem does not expose inode flags required for checkout write exclusion: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

            var desired = immutable
                ? flags | FS_IMMUTABLE_FL
                : flags & ~FS_IMMUTABLE_FL;
            var changed = desired != flags;
            if (!changed)
            {
                var unchanged = 0;
                if (Native.ioctl(fd, FS_IOC_GETFLAGS, ref unchanged) != 0 ||
                    ((unchanged & FS_IMMUTABLE_FL) != 0) != immutable)
                    throw new AgentException(
                        $"Checkout write-exclusion state did not persist for {path}");
                return false;
            }

            EnsureDescriptorSingleLink(fd, path);
            if (Native.ioctl(fd, FS_IOC_SETFLAGS, ref desired) != 0)
                throw new AgentException(
                    $"Could not {(immutable ? "seal" : "unseal")} checkout path {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

            try
            {
                if (immutable)
                {
                    // Once immutable is active Linux rejects new hardlinks and
                    // renames of this inode. Recheck both the link count and
                    // the pathname binding to close races between open() and
                    // FS_IOC_SETFLAGS.
                    EnsureDescriptorSingleLink(fd, path);
                    EnsurePathStillReferencesDescriptor(fd, path);
                }
                else
                {
                    // After clearing immutable another process may immediately
                    // rename/link the inode. Detect that before durability work,
                    // but never re-apply immutable on an inode that may already
                    // have escaped the protected namespace.
                    EnsureDescriptorSingleLink(fd, path);
                    EnsurePathStillReferencesDescriptor(fd, path);
                }

                if (Native.fsync(fd) != 0)
                    throw new AgentException(
                        $"Could not fsync checkout inode flags for {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

                var after = 0;
                if (Native.ioctl(fd, FS_IOC_GETFLAGS, ref after) != 0 ||
                    ((after & FS_IMMUTABLE_FL) != 0) != immutable)
                    throw new AgentException(
                        $"Checkout write-exclusion state did not persist for {path}");

                return true;
            }
            catch (Exception mutationFailure)
            {
                if (!immutable)
                    throw;

                var restore = flags;
                Exception? rollbackFailure = null;
                if (Native.ioctl(fd, FS_IOC_SETFLAGS, ref restore) != 0)
                {
                    rollbackFailure = new AgentException(
                        $"Could not restore checkout inode flags for {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
                }
                else if (Native.fsync(fd) != 0)
                {
                    rollbackFailure = new AgentException(
                        $"Could not fsync restored checkout inode flags for {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
                }

                if (rollbackFailure is not null)
                    throw new AgentException(
                        $"Checkout write-exclusion transition failed and rollback was incomplete for {path}",
                        new AggregateException(mutationFailure, rollbackFailure));
                throw;
            }
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static void EnsurePathStillReferencesDescriptor(int fd, string path)
    {
        if (Native.statx(
                fd,
                "",
                AT_EMPTY_PATH,
                STATX_BASIC_STATS,
                out var opened) != 0)
            throw new AgentException(
                $"Could not inspect sealed descriptor identity for {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        if (Native.statx(
                AT_FDCWD,
                path,
                AT_SYMLINK_NOFOLLOW,
                STATX_BASIC_STATS,
                out var named) != 0)
            throw new AgentException(
                $"Sealed checkout pathname disappeared or changed: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        EnsureSameIdentity(
            FileIdentity.From(opened),
            FileIdentity.From(named),
            path);
    }

    internal static void EnsureSameIdentity(
        FileIdentity opened,
        FileIdentity named,
        string path)
    {
        if (opened != named)
            throw new AgentException(
                $"Checkout pathname changed while being write-excluded: {path}");
    }

    internal static bool IsImmutableNoFollow(string path)
    {
        var fd = Native.open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException(
                $"Could not open checkout path to inspect write exclusion: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        try
        {
            EnsureDescriptorIsRegularOrDirectory(fd, path);
            var flags = 0;
            if (Native.ioctl(fd, FS_IOC_GETFLAGS, ref flags) != 0)
                throw new AgentException(
                    $"Filesystem does not expose inode flags required for checkout write exclusion: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
            return (flags & FS_IMMUTABLE_FL) != 0;
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static void EnsureImmutableNoFollow(string path)
    {
        var fd = Native.open(path, O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException(
                $"Could not open sealed checkout path: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        try
        {
            EnsureDescriptorIsRegularOrDirectory(fd, path);
            var flags = 0;
            if (Native.ioctl(fd, FS_IOC_GETFLAGS, ref flags) != 0 ||
                (flags & FS_IMMUTABLE_FL) == 0)
                throw new AgentException($"Checkout path is not write-excluded: {path}");
        }
        finally
        {
            _ = Native.close(fd);
        }
    }

    private static void EnsureDescriptorIsRegularOrDirectory(int fd, string path)
    {
        if (Native.statx(fd, "", AT_EMPTY_PATH, STATX_TYPE, out var stat) != 0)
            throw new AgentException(
                $"Could not inspect opened checkout path: {path}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

        var kind = (ushort)(stat.Mode & S_IFMT);
        if (kind is not (S_IFREG or S_IFDIR))
            throw new AgentException($"Checkout write exclusion only supports regular files/directories: {path}");
    }

    private static bool IsUnder(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string StripDeletedSuffix(string path)
        => path.EndsWith(" (deleted)", StringComparison.Ordinal)
            ? path[..^" (deleted)".Length]
            : path;

    private static int PathDepth(string path)
        => Path.GetFullPath(path).Count(character => character == Path.DirectorySeparatorChar);
}

internal sealed class PinnedGitMetadataRoot : IDisposable
{
    private const int O_RDONLY = 0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int O_PATH = 0x200000;
    private const int AT_EMPTY_PATH = 0x1000;
    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const uint STATX_BASIC_STATS = 0x000007ff;
    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFREG = 0x8000;
    private const int ENOENT = 2;
    private const int ESRCH = 3;
    private const int EPERM = 1;
    private const int EACCES = 13;

    private int _rootFd;
    private readonly string _displayRoot;

    private PinnedGitMetadataRoot(int rootFd, string displayRoot)
    {
        _rootFd = rootFd;
        _displayRoot = displayRoot;
    }

    internal static PinnedGitMetadataRoot Open(string root)
    {
        root = Path.GetFullPath(root);
        var fd = Native.open(root, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
        if (fd < 0)
            throw new AgentException(
                $"Git metadata root could not be pinned as a real directory: {root}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        return new PinnedGitMetadataRoot(fd, root);
    }

    internal async Task CleanupLocksAsync(
        Func<Task> assertNoRelatedGit,
        Func<FileIdentity, Task<bool>> anyProcessHasOpenIdentity)
    {
        ObjectDisposedException.ThrowIf(_rootFd < 0, this);
        await WalkAsync(_rootFd, _displayRoot, assertNoRelatedGit, anyProcessHasOpenIdentity);
    }

    private static async Task WalkAsync(
        int directoryFd,
        string displayDirectory,
        Func<Task> assertNoRelatedGit,
        Func<FileIdentity, Task<bool>> anyProcessHasOpenIdentity)
    {
        string[] names;
        try
        {
            names = Directory
                .EnumerateFileSystemEntries($"/proc/self/fd/{directoryFd}")
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Cast<string>()
                .Order(StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentException(
                $"Could not enumerate pinned Git metadata directory {displayDirectory}",
                exception);
        }

        foreach (var name in names)
        {
            if (Native.statx(
                    directoryFd,
                    name,
                    AT_SYMLINK_NOFOLLOW,
                    STATX_BASIC_STATS,
                    out var entryStat) != 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == ENOENT)
                    continue;
                throw new AgentException(
                    $"Could not inspect Git metadata entry {Path.Combine(displayDirectory, name)}; errno={error}");
            }

            var type = (ushort)(entryStat.Mode & S_IFMT);
            if (type == S_IFDIR)
            {
                var childFd = Native.openat(
                    directoryFd,
                    name,
                    O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
                if (childFd < 0)
                    throw new AgentException(
                        $"Could not pin Git metadata directory {Path.Combine(displayDirectory, name)}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
                try
                {
                    await WalkAsync(
                        childFd,
                        Path.Combine(displayDirectory, name),
                        assertNoRelatedGit,
                        anyProcessHasOpenIdentity);
                }
                finally
                {
                    _ = Native.close(childFd);
                }
                continue;
            }

            if (type != S_IFREG || !name.EndsWith(".lock", StringComparison.Ordinal))
                continue;

            await assertNoRelatedGit();

            var lockFd = Native.openat(directoryFd, name, O_PATH | O_NOFOLLOW | O_CLOEXEC);
            if (lockFd < 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == ENOENT)
                    continue;
                throw new AgentException(
                    $"Could not pin Git lock {Path.Combine(displayDirectory, name)}; errno={error}");
            }

            FileIdentity identity;
            try
            {
                if (Native.statx(
                        lockFd,
                        "",
                        AT_EMPTY_PATH,
                        STATX_BASIC_STATS,
                        out var lockStat) != 0)
                    throw new AgentException(
                        $"Could not inspect pinned Git lock {Path.Combine(displayDirectory, name)}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");

                if ((lockStat.Mode & S_IFMT) != S_IFREG)
                    throw new AgentException(
                        $"Git lock changed type during cleanup: {Path.Combine(displayDirectory, name)}");

                identity = FileIdentity.From(lockStat);
            }
            finally
            {
                _ = Native.close(lockFd);
            }

            if (await anyProcessHasOpenIdentity(identity))
                throw new AgentException(
                    $"Refusing to remove open git lock: {Path.Combine(displayDirectory, name)}");

            await assertNoRelatedGit();

            if (Native.statx(
                    directoryFd,
                    name,
                    AT_SYMLINK_NOFOLLOW,
                    STATX_BASIC_STATS,
                    out var currentStat) != 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == ENOENT)
                    continue;
                throw new AgentException(
                    $"Could not re-check Git lock {Path.Combine(displayDirectory, name)}; errno={error}");
            }

            if ((currentStat.Mode & S_IFMT) != S_IFREG ||
                FileIdentity.From(currentStat) != identity)
                throw new AgentException(
                    $"Git lock changed while being inspected: {Path.Combine(displayDirectory, name)}");

            if (Native.unlinkat(directoryFd, name, 0) != 0)
            {
                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error == ENOENT)
                    continue;
                throw new AgentException(
                    $"Could not remove stale Git lock {Path.Combine(displayDirectory, name)}; errno={error}");
            }

            if (Native.fsync(directoryFd) != 0)
                throw new AgentException(
                    $"Could not fsync Git metadata directory {displayDirectory}; errno={System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }

    internal static async Task<bool> AnyProcessHasOpenIdentityAsync(FileIdentity identity)
    {
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out _))
                continue;

            var fdDir = Path.Combine(procDir, "fd");
            string[] descriptors;
            try
            {
                descriptors = Directory.EnumerateFileSystemEntries(fdDir).ToArray();
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                throw new AgentException(
                    $"Cannot inspect process {Path.GetFileName(procDir)} fds before lock cleanup");
            }

            foreach (var descriptor in descriptors)
            {
                if (Native.statx(
                        AT_FDCWD,
                        descriptor,
                        0,
                        STATX_BASIC_STATS,
                        out var descriptorStat) == 0)
                {
                    if ((descriptorStat.Mode & S_IFMT) == S_IFREG &&
                        FileIdentity.From(descriptorStat) == identity)
                        return true;
                    continue;
                }

                var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (error is ENOENT or ESRCH)
                    continue;
                if (error is EPERM or EACCES)
                    throw new AgentException(
                        $"Cannot inspect process {Path.GetFileName(procDir)} fds before lock cleanup");
            }
        }

        await Task.CompletedTask;
        return false;
    }

    public void Dispose()
    {
        if (_rootFd < 0)
            return;
        _ = Native.close(_rootFd);
        _rootFd = -1;
    }
}

internal static class GitMetadataBoundary
{
    internal static void EnsureRepositoryRoots(
        string repository,
        IEnumerable<string> roots,
        IReadOnlyCollection<string>? parentMetadataHierarchy,
        bool isTopLevel)
    {
        repository = Path.GetFullPath(repository);
        var ownRoots = roots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (ownRoots.Length == 0)
            throw new AgentException("Git metadata hierarchy is empty");

        if (isTopLevel)
        {
            var embedded = Path.Combine(repository, GIT_METADATA_NAME);
            if (!Directory.Exists(embedded) ||
                new DirectoryInfo(embedded).LinkTarget is not null)
                throw new AgentException("Top-level deployment repository must use a real in-tree .git directory");

            EnsureRootsWithin(ownRoots, new[] { embedded });
            return;
        }

        if (parentMetadataHierarchy is null || parentMetadataHierarchy.Count == 0)
            throw new AgentException("Verified submodule metadata hierarchy is missing");

        var parentRoots = parentMetadataHierarchy
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.Ordinal);
        if (ownRoots.Any(parentRoots.Contains))
            throw new AgentException("Submodule Git metadata aliases an ancestor metadata root");

        var allowed = new HashSet<string>(
            parentRoots,
            StringComparer.Ordinal);

        var embeddedSubmoduleMetadata = Path.Combine(repository, GIT_METADATA_NAME);
        if (Directory.Exists(embeddedSubmoduleMetadata) &&
            new DirectoryInfo(embeddedSubmoduleMetadata).LinkTarget is null)
            allowed.Add(Path.GetFullPath(embeddedSubmoduleMetadata));

        EnsureRootsWithin(ownRoots, allowed);
    }

    internal static void EnsureRootsWithin(
        IEnumerable<string> roots,
        IEnumerable<string> allowedRoots)
    {
        var allowed = allowedRoots
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (allowed.Length == 0)
            throw new AgentException("Git metadata hierarchy is empty");

        foreach (var root in roots.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal))
        {
            var boundary = allowed.FirstOrDefault(candidate => IsSameOrDescendant(root, candidate));
            if (boundary is null)
                throw new AgentException($"Git metadata escapes the repository hierarchy: {root}");

            EnsureRealDirectoryChain(root, boundary);
        }
    }

    internal static bool IsSameOrDescendant(string path, string root)
    {
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);

        return string.Equals(path, root, StringComparison.Ordinal) ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static void EnsureRealDirectoryChain(string path, string boundary)
    {
        var current = Path.GetFullPath(path);
        boundary = Path.GetFullPath(boundary);

        while (true)
        {
            if (!Directory.Exists(current))
                throw new AgentException($"Git metadata root is not a directory: {current}");
            if (new DirectoryInfo(current).LinkTarget is not null)
                throw new AgentException($"Git metadata hierarchy contains a symlink: {current}");

            if (string.Equals(current, boundary, StringComparison.Ordinal))
                return;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) ||
                string.Equals(parent, current, StringComparison.Ordinal))
                throw new AgentException($"Git metadata root escaped its hierarchy: {path}");
            current = parent;
        }
    }
}

internal static class GitTreePath
{
    internal static string Resolve(string repository, string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) ||
            Path.IsPathFullyQualified(relativePath))
            throw new AgentException($"Unsafe Git tree path: {relativePath}");

        var components = relativePath.Split('/');
        if (components.Any(component =>
                string.IsNullOrEmpty(component) ||
                component is "." or ".." ||
                component == GIT_METADATA_NAME))
            throw new AgentException($"Unsafe Git tree path: {relativePath}");

        var repositoryRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(repository));
        var combined = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            string.Join(Path.DirectorySeparatorChar, components)));
        var relative = Path.GetRelativePath(repositoryRoot, combined);

        if (relative is "." or ".." ||
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(relative))
            throw new AgentException($"Git tree path escapes repository: {relativePath}");

        return combined;
    }
}

internal static class GitProcessIdentity
{
    private const string GIT_EXECUTABLE_NAME = "git";
    private const string GIT_EXECUTABLE_PREFIX = "git-";
    private const string PROC_DELETED_SUFFIX = " (deleted)";

    internal static bool IsGitExecutable(string? executableName)
    {
        if (string.IsNullOrEmpty(executableName))
            return false;

        if (executableName.EndsWith(PROC_DELETED_SUFFIX, StringComparison.Ordinal))
            executableName = executableName[..^PROC_DELETED_SUFFIX.Length];

        return executableName == GIT_EXECUTABLE_NAME ||
               executableName.StartsWith(GIT_EXECUTABLE_PREFIX, StringComparison.Ordinal);
    }
}

internal sealed class GitRepository(AgentConfig config)
{
    private readonly AgentConfig _config = config;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> HeadAsync()
        => (await GitAsync([GIT_SUBCOMMAND_REV_PARSE, "HEAD"])).StdOut.Trim();

    public async Task VerifySourceTreeExactAsync(string commit)
    {
        var metadataBindings = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        var metadataHierarchy = await VerifiedGitMetadataRootsAsync(
            _config.AppDirectory,
            commit,
            parentMetadataHierarchy: null,
            GitMetadataTraversalMode.StrictTargetCommit,
            metadataBindings);
        var metadataBeforeSeal = CaptureGitMetadataSnapshots(metadataHierarchy);

        var verified = await VerifyRepositoryExactAsync(
            _config.AppDirectory,
            commit,
            expectedMetadataBindings: metadataBindings);
        await RevalidateRepositorySnapshotAsync(verified);

        CheckoutWriteExclusion.SealVerifiedState(verified, metadataBeforeSeal);

        var protectedRoots = new HashSet<string>(
            metadataHierarchy.Select(Path.GetFullPath),
            StringComparer.Ordinal)
        {
            Path.GetFullPath(_config.AppDirectory)
        };
        CheckoutWriteExclusion.AssertNoWritableReferences(
            protectedRoots,
            verified,
            metadataBeforeSeal);

        // Sealing changes inode ctime. Re-snapshot only after write exclusion is
        // active, then prove exact bytes/structure while no unprivileged writer
        // can open a new writable handle.
        var sealedMetadata = CaptureGitMetadataSnapshots(metadataHierarchy);
        var sealedVerified = await VerifyRepositoryExactAsync(
            _config.AppDirectory,
            commit,
            expectedMetadataBindings: metadataBindings);
        await RevalidateRepositorySnapshotAsync(sealedVerified);
        EnsureGitMetadataSnapshotsUnchanged(sealedMetadata);
        CheckoutWriteExclusion.EnsureVerifiedStateSealed(sealedVerified, sealedMetadata);
    }

    public async Task SwitchSourceAsync(string commit, bool fetchFirst)
    {
        await PrepareCheckoutMutationAsync(commit);

        if (fetchFirst)
        {
            await GitRequiredAsync([GIT_SUBCOMMAND_FETCH, "--force", "--depth", "1", "origin", commit]);
            await GitRequiredAsync([GIT_SUBCOMMAND_CHECKOUT, "--detach", "FETCH_HEAD"]);
        }

        await GitRequiredAsync([GIT_SUBCOMMAND_RESET, "--hard", commit]);
        await GitRequiredAsync([GIT_SUBCOMMAND_CLEAN, "-ffdx"]);
        await SyncSubmodulesAsync(commit);

        if (await HeadAsync() != commit)
            throw new AgentException("Source HEAD mismatch after switch");
        await VerifySourceTreeExactAsync(commit);
        await FsyncCheckoutAsync(commit);
    }

    public async Task FsyncCheckoutAsync(string commit)
    {
        // Establish persistent write exclusion first. This makes the trailing
        // durability verification stable through method return instead of
        // merely moving the final TOCTOU window to another read.
        await VerifySourceTreeExactAsync(commit);

        var metadataBindings = new Dictionary<string, HashSet<string>>(
            StringComparer.Ordinal);
        var metadataHierarchy = await VerifiedGitMetadataRootsAsync(
            _config.AppDirectory,
            commit,
            parentMetadataHierarchy: null,
            GitMetadataTraversalMode.StrictTargetCommit,
            metadataBindings);
        var metadataSnapshots = CaptureGitMetadataSnapshots(metadataHierarchy);

        var verified = await VerifyRepositoryExactAsync(
            _config.AppDirectory,
            commit,
            expectedMetadataBindings: metadataBindings);
        await RevalidateRepositorySnapshotAsync(verified);
        EnsureGitMetadataSnapshotsUnchanged(metadataSnapshots);

        await FsyncRepositoryAsync(
            _config.AppDirectory,
            commit,
            verified,
            metadataHierarchy,
            metadataSnapshots);

        EnsureGitMetadataSnapshotsUnchanged(metadataSnapshots);

        var final = await VerifyRepositoryExactAsync(
            _config.AppDirectory,
            commit,
            expectedMetadataBindings: metadataBindings);
        await RevalidateRepositorySnapshotAsync(final);
        EnsureSnapshotsUnchanged(verified, final);
        EnsureGitMetadataSnapshotsUnchanged(metadataSnapshots);
        CheckoutWriteExclusion.EnsureVerifiedStateSealed(final, metadataSnapshots);
    }

    public async Task ReconcileStaleGitLocksAsync(string? rollbackCommit = null)
    {
        var head = await HeadAsync();
        var roots = await RecoveryGitMetadataRootsAsync(head, rollbackCommit);
        var protectedRoots = new HashSet<string>(roots, StringComparer.Ordinal) { Path.GetFullPath(_config.AppDirectory) };

        foreach (var root in roots)
            _ = GitMetadataDurability.Capture(root);

        var sealState = roots.ToDictionary(
            root => root,
            CheckoutWriteExclusion.InspectTreeSealState,
            StringComparer.Ordinal);

        await AssertNoRelatedGitAsync(protectedRoots);

        foreach (var root in roots.Where(root =>
                     sealState[root] is not CheckoutSealState.Unsealed))
            CheckoutWriteExclusion.SetTreeImmutable(root, immutable: false);

        Exception? cleanupFailure = null;
        try
        {
            await AssertNoRelatedGitAsync(protectedRoots);

            foreach (var root in roots)
            {
                using var pinnedRoot = PinnedGitMetadataRoot.Open(root);
                await pinnedRoot.CleanupLocksAsync(
                    () => AssertNoRelatedGitAsync(protectedRoots),
                    PinnedGitMetadataRoot.AnyProcessHasOpenIdentityAsync);
            }

            await AssertNoRelatedGitAsync(protectedRoots);
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        try
        {
            foreach (var root in roots.Where(root =>
                         sealState[root] == CheckoutSealState.FullySealed))
                CheckoutWriteExclusion.SetTreeImmutable(root, immutable: true);
        }
        catch (Exception resealFailure)
        {
            if (cleanupFailure is not null)
                throw new AgentException(
                    "Git lock reconciliation and metadata reseal both failed",
                    new AggregateException(cleanupFailure, resealFailure));
            throw;
        }

        if (cleanupFailure is not null)
            throw cleanupFailure;
    }

    private async Task PrepareCheckoutMutationAsync(string targetCommit)
    {
        var currentCommit = await HeadAsync();
        var metadataRoots = await RecoveryGitMetadataRootsAsync(currentCommit, targetCommit);

        foreach (var root in metadataRoots)
            _ = GitMetadataDurability.Capture(root);

        var protectedRoots = new HashSet<string>(
            metadataRoots.Select(Path.GetFullPath),
            StringComparer.Ordinal)
        {
            Path.GetFullPath(_config.AppDirectory)
        };

        await AssertNoRelatedGitAsync(protectedRoots);

        CheckoutWriteExclusion.SetTreeImmutable(_config.AppDirectory, immutable: false);
        foreach (var root in metadataRoots)
            CheckoutWriteExclusion.SetTreeImmutable(root, immutable: false);

        await AssertNoRelatedGitAsync(protectedRoots);
    }

    private async Task<RepositoryDurabilitySnapshot> VerifyRepositoryExactAsync(
        string repository,
        string commit,
        RepositoryDurabilitySnapshot? verified = null,
        IReadOnlyDictionary<string, HashSet<string>>? expectedMetadataBindings = null)
    {
        verified ??= new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        repository = Path.GetFullPath(repository);
        if (!Directory.Exists(repository) || new DirectoryInfo(repository).LinkTarget is not null)
            throw new AgentException($"Repository path is not a real directory: {repository}");

        await EnsureRepositoryMetadataBindingAsync(
            repository,
            expectedMetadataBindings);

        var head = (await GitAtAsync(repository, [GIT_SUBCOMMAND_REV_PARSE, "HEAD"])).StdOut.Trim();
        if (head != commit)
            throw new AgentException($"HEAD mismatch in {repository}: {head} != {commit}");
        verified.Heads[repository] = commit;

        var algorithm = (await GitAtAsync(repository, [GIT_SUBCOMMAND_REV_PARSE, "--show-object-format"])).StdOut.Trim();
        if (algorithm is not (GIT_OBJECT_FORMAT_SHA1 or GIT_OBJECT_FORMAT_SHA256))
            throw new AgentException($"Unsupported Git object format: {algorithm}");

        var entries = await ReadTreeAsync(repository, commit);
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal) { repository };
        var submodules = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var fullPath = GitTreePath.Resolve(repository, entry.RelativePath);
            AddParentChain(expectedDirectories, fullPath, repository);
            if (entry.Mode == GIT_MODE_GITLINK && entry.Kind == GIT_OBJECT_COMMIT)
            {
                if (!Directory.Exists(fullPath) || new DirectoryInfo(fullPath).LinkTarget is not null)
                    throw new AgentException($"gitlink is not a real directory: {entry.RelativePath}");

                var gitMarker = Path.Combine(fullPath, GIT_METADATA_NAME);
                SubmoduleGitMarkerDurability.Capture(
                    gitMarker,
                    $"submodule Git metadata {entry.RelativePath}",
                    verified);

                submodules[entry.RelativePath] = entry.ObjectId;
                continue;
            }

            if (entry.Kind != GIT_OBJECT_BLOB)
                throw new AgentException($"Unexpected tree object {entry.Kind}: {entry.RelativePath}");
            expectedFiles.Add(entry.RelativePath);

            byte[] data;
            var info = new FileInfo(fullPath);
            if (entry.Mode == GIT_MODE_SYMLINK)
            {
                var target = info.LinkTarget;
                if (target is null)
                    throw new AgentException($"Expected symlink: {entry.RelativePath}");
                verified.Symlinks[Path.GetFullPath(fullPath)] = target;
                data = StrictUtf8.GetBytes(target);
            }
            else if (entry.Mode is GIT_MODE_FILE or GIT_MODE_EXECUTABLE)
            {
                var verifiedFile = TrackedFileDurability.ReadVerifiedRegularFile(fullPath, entry.RelativePath);
                var executable = (verifiedFile.Mode & UnixFileMode.UserExecute) != 0;
                if (executable != (entry.Mode == GIT_MODE_EXECUTABLE))
                    throw new AgentException($"Executable bit mismatch: {entry.RelativePath}");
                data = verifiedFile.Data;
                verified.Files[Path.GetFullPath(fullPath)] = verifiedFile.Snapshot;
            }
            else
            {
                throw new AgentException($"Unsupported Git mode {entry.Mode}: {entry.RelativePath}");
            }

            var objectId = GitBlobObjectId(data, algorithm);
            if (!string.Equals(objectId, entry.ObjectId, StringComparison.Ordinal))
                throw new AgentException($"Tracked bytes differ: {entry.RelativePath}");
        }

        var expectedRelativeDirectories = expectedDirectories
            .Where(directory => !string.Equals(
                Path.GetFullPath(directory),
                repository,
                StringComparison.Ordinal))
            .Select(directory => Path.GetRelativePath(repository, directory)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .ToHashSet(StringComparer.Ordinal);

        var actualTree = EnumerateDiskTree(repository, submodules.Keys);
        EnsureDiskTreeMatches(
            repository,
            expectedFiles,
            expectedRelativeDirectories,
            actualTree);

        verified.FileSets[repository] = new RepositoryFileSetSnapshot(
            expectedFiles.ToHashSet(StringComparer.Ordinal),
            expectedRelativeDirectories,
            submodules.Keys.ToHashSet(StringComparer.Ordinal));

        foreach (var directory in expectedDirectories)
        {
            var fullDirectory = Path.GetFullPath(directory);
            verified.Directories[fullDirectory] =
                Durability.ReadDirectorySnapshotNoFollow(fullDirectory, fullDirectory);
        }

        foreach (var submodule in submodules)
        {
            var subPath = GitTreePath.Resolve(repository, submodule.Key);
            await VerifyRepositoryExactAsync(
                subPath,
                submodule.Value,
                verified,
                expectedMetadataBindings);
        }

        await EnsureRepositoryMetadataBindingAsync(
            repository,
            expectedMetadataBindings);
        return verified;
    }

    private async Task EnsureRepositoryMetadataBindingAsync(
        string repository,
        IReadOnlyDictionary<string, HashSet<string>>? expectedMetadataBindings)
    {
        if (expectedMetadataBindings is null)
            return;

        repository = Path.GetFullPath(repository);
        if (!expectedMetadataBindings.TryGetValue(repository, out var expectedRoots))
            throw new AgentException(
                $"Repository metadata binding was not captured: {repository}");

        var actualRoots = (await GitMetadataRootsAsync(repository))
            .ToHashSet(StringComparer.Ordinal);
        EnsureMetadataBindingUnchanged(repository, expectedRoots, actualRoots);
    }

    internal static void EnsureMetadataBindingUnchanged(
        string repository,
        IReadOnlySet<string> expectedRoots,
        IReadOnlySet<string> actualRoots)
    {
        if (!expectedRoots.SetEquals(actualRoots))
        {
            var extra = actualRoots.Except(expectedRoots).Order(StringComparer.Ordinal);
            var missing = expectedRoots.Except(actualRoots).Order(StringComparer.Ordinal);
            throw new AgentException(
                $"Git metadata binding changed for {repository}; " +
                $"extra=[{string.Join(",", extra)}] missing=[{string.Join(",", missing)}]");
        }
    }

    private async Task RevalidateRepositorySnapshotAsync(
        RepositoryDurabilitySnapshot verified)
    {
        await EnsureSnapshotHeadsCurrentAsync(verified);
        EnsureSnapshotObjectsCurrent(verified);
        EnsureSnapshotFileSetsCurrent(verified);
        EnsureSnapshotObjectsCurrent(verified);
        await EnsureSnapshotHeadsCurrentAsync(verified);
    }

    internal static void EnsureSnapshotObjectsCurrent(
        RepositoryDurabilitySnapshot verified)
    {
        foreach (var pair in verified.Files)
        {
            var actual = Durability.ReadRegularFileNoFollow(
                pair.Key,
                pair.Key).Snapshot;
            if (actual != pair.Value)
                throw new AgentException(
                    $"Tracked regular file changed after exact-tree traversal: {pair.Key}");
        }

        foreach (var pair in verified.Symlinks)
        {
            var actual = new FileInfo(pair.Key).LinkTarget;
            if (!string.Equals(actual, pair.Value, StringComparison.Ordinal))
                throw new AgentException(
                    $"Tracked symlink changed after exact-tree traversal: {pair.Key}");
        }

        foreach (var pair in verified.Directories)
        {
            var actual = Durability.ReadDirectorySnapshotNoFollow(
                pair.Key,
                pair.Key);
            if (actual != pair.Value)
                throw new AgentException(
                    $"Checkout directory changed after exact-tree traversal: {pair.Key}");
        }
    }

    internal static void EnsureSnapshotFileSetsCurrent(
        RepositoryDurabilitySnapshot verified)
    {
        foreach (var pair in verified.FileSets)
        {
            var actual = EnumerateDiskTree(
                pair.Key,
                pair.Value.Submodules);
            EnsureDiskTreeMatches(
                pair.Key,
                pair.Value.Files,
                pair.Value.Directories,
                actual,
                " changed after exact-tree traversal");
        }
    }

    private static async Task EnsureSnapshotHeadsCurrentAsync(
        RepositoryDurabilitySnapshot verified)
    {
        foreach (var pair in verified.Heads)
        {
            var head = (await GitAtAsync(
                pair.Key,
                [GIT_SUBCOMMAND_REV_PARSE, "HEAD"])).StdOut.Trim();
            if (!string.Equals(head, pair.Value, StringComparison.Ordinal))
                throw new AgentException(
                    $"Repository HEAD changed after exact-tree traversal: {pair.Key}");
        }
    }

    private async Task SyncSubmodulesAsync(string commit)
    {
        await PrepareGitlinksAsync(commit);
        await GitRequiredAsync([GIT_SUBCOMMAND_SUBMODULE, "sync", "--recursive"]);
        await GitRequiredAsync([GIT_SUBCOMMAND_SUBMODULE, "update", "--init", "--recursive", "--force"]);

        var clean = await GitAsync([
            GIT_SUBCOMMAND_SUBMODULE, "foreach", "--recursive",
            "git reset --hard HEAD >/dev/null && git clean -ffdx >/dev/null"
        ]);
        if (!clean.Success)
            throw new AgentException($"Could not clean populated submodules: {clean.StdErr.Trim()}");
    }

    private async Task PrepareGitlinksAsync(string commit)
    {
        var entries = await ReadTreeAsync(_config.AppDirectory, commit);
        foreach (var entry in entries.Where(entry => entry.Mode == GIT_MODE_GITLINK && entry.Kind == GIT_OBJECT_COMMIT))
        {
            var path = GitTreePath.Resolve(_config.AppDirectory, entry.RelativePath);
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            var info = new FileInfo(path);
            var unsafeEntry = info.LinkTarget is not null || !Directory.Exists(path);
            if (!unsafeEntry)
            {
                var probe = await GitAtAsync(path, ["rev-parse", "--git-dir"], required: false);
                unsafeEntry = !probe.Success;
            }

            if (unsafeEntry)
            {
                if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null)
                    Directory.Delete(path, recursive: true);
                else
                    File.Delete(path);
            }
        }
    }

    private async Task FsyncRepositoryAsync(
        string repository,
        string commit,
        RepositoryDurabilitySnapshot verified,
        IReadOnlyCollection<string> metadataHierarchy,
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> metadataSnapshots)
    {
        var entries = await ReadTreeAsync(repository, commit);
        var directories = new HashSet<string>(StringComparer.Ordinal) { repository };
        var submodules = new List<(string Path, string Commit)>();

        foreach (var entry in entries)
        {
            var path = GitTreePath.Resolve(repository, entry.RelativePath);
            AddParentChain(directories, path, repository);

            if (entry.Mode == GIT_MODE_GITLINK && entry.Kind == GIT_OBJECT_COMMIT)
            {
                submodules.Add((path, entry.ObjectId));
                var gitMarker = Path.Combine(path, GIT_METADATA_NAME);
                SubmoduleGitMarkerDurability.Fsync(
                    gitMarker,
                    $"submodule Git metadata {entry.RelativePath}",
                    verified);
                continue;
            }

            if (entry.Kind != GIT_OBJECT_BLOB)
                throw new AgentException($"Unexpected tree object during fsync {entry.Kind}: {entry.RelativePath}");

            if (entry.Mode is GIT_MODE_FILE or GIT_MODE_EXECUTABLE)
            {
                var fullPath = Path.GetFullPath(path);
                if (!verified.Files.TryGetValue(fullPath, out var expectedSnapshot))
                    throw new AgentException($"Tracked regular file was not snapshot-verified: {entry.RelativePath}");

                TrackedFileDurability.FsyncRegularFile(path, entry.RelativePath, expectedSnapshot);
                continue;
            }

            if (entry.Mode == GIT_MODE_SYMLINK)
            {
                if (new FileInfo(path).LinkTarget is null)
                    throw new AgentException($"Tracked symlink changed type during fsync: {entry.RelativePath}");
                continue;
            }

            throw new AgentException($"Unsupported Git mode during fsync {entry.Mode}: {entry.RelativePath}");
        }

        foreach (var directory in directories.OrderByDescending(x => x.Count(c => c == Path.DirectorySeparatorChar)))
        {
            var fullDirectory = Path.GetFullPath(directory);
            if (!verified.Directories.TryGetValue(fullDirectory, out var expectedDirectory))
                throw new AgentException($"Checkout directory was not snapshot-verified: {fullDirectory}");

            Durability.FsyncDirectorySnapshotNoFollow(
                fullDirectory,
                fullDirectory,
                expectedDirectory);
        }

        var repositoryMetadataRoots = await GitMetadataRootsAsync(repository);
        GitMetadataBoundary.EnsureRepositoryRoots(
            repository,
            repositoryMetadataRoots,
            metadataHierarchy,
            isTopLevel: string.Equals(
                Path.GetFullPath(repository),
                Path.GetFullPath(_config.AppDirectory),
                StringComparison.Ordinal));
        foreach (var root in repositoryMetadataRoots)
            FsyncDirectoryTree(root, metadataSnapshots);

        foreach (var submodule in submodules)
            await FsyncRepositoryAsync(
                submodule.Path,
                submodule.Commit,
                verified,
                metadataHierarchy,
                metadataSnapshots);
    }

    private static void EnsureSnapshotsUnchanged(
        RepositoryDurabilitySnapshot expected,
        RepositoryDurabilitySnapshot actual)
    {
        EnsureSnapshotMapUnchanged(
            expected.Files,
            actual.Files,
            "Tracked regular file");
        EnsureSnapshotMapUnchanged(
            expected.Directories,
            actual.Directories,
            "Checkout directory");
    }

    private static void EnsureSnapshotMapUnchanged<T>(
        IReadOnlyDictionary<string, T> expected,
        IReadOnlyDictionary<string, T> actual,
        string kind)
        where T : notnull
    {
        if (expected.Count != actual.Count)
            throw new AgentException($"{kind} set changed during durability barrier");

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var snapshot) ||
                !EqualityComparer<T>.Default.Equals(snapshot, pair.Value))
                throw new AgentException($"{kind} changed during durability barrier: {pair.Key}");
        }
    }

    internal static Dictionary<string, GitMetadataDurabilitySnapshot> CaptureGitMetadataSnapshots(
        IEnumerable<string> roots)
    {
        var snapshots = new Dictionary<string, GitMetadataDurabilitySnapshot>(
            StringComparer.Ordinal);
        foreach (var root in roots.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var fullRoot = Path.GetFullPath(root);
            snapshots[fullRoot] = GitMetadataDurability.Capture(fullRoot);
        }

        return snapshots;
    }

    internal static void EnsureGitMetadataSnapshotsUnchanged(
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> snapshots)
    {
        foreach (var pair in snapshots.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            GitMetadataDurability.EnsureUnchanged(
                pair.Value,
                GitMetadataDurability.Capture(pair.Key));
    }

    private static void FsyncDirectoryTree(
        string root,
        IReadOnlyDictionary<string, GitMetadataDurabilitySnapshot> metadataSnapshots)
    {
        root = Path.GetFullPath(root);
        if (!metadataSnapshots.TryGetValue(root, out var snapshot))
            throw new AgentException(
                $"Git metadata root was not snapshot-verified before checkout fsync: {root}");
        GitMetadataDurability.Fsync(root, snapshot);
    }

    private async Task<List<string>> GitMetadataRootsAsync(string repository)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in new[] { GIT_FLAG_DIR, "--git-common-dir" })
        {
            var result = await GitAtAsync(
                repository,
                ["rev-parse", "--path-format=absolute", flag]);
            roots.Add(Path.GetFullPath(result.StdOut.Trim()));
        }

        return roots.Order(StringComparer.Ordinal).ToList();
    }

    internal async Task<List<string>> RecoveryGitMetadataRootsAsync(
        string currentCommit,
        string? rollbackCommit = null)
    {
        var roots = new HashSet<string>(
            await VerifiedGitMetadataRootsAsync(
                _config.AppDirectory,
                currentCommit,
                parentMetadataHierarchy: null,
                GitMetadataTraversalMode.RecoveryCurrentCheckout),
            StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(rollbackCommit) &&
            !string.Equals(currentCommit, rollbackCommit, StringComparison.Ordinal))
        {
            roots.UnionWith(await VerifiedGitMetadataRootsAsync(
                _config.AppDirectory,
                rollbackCommit,
                parentMetadataHierarchy: null,
                GitMetadataTraversalMode.RecoveryRollbackTarget));
        }

        return roots.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<List<string>> VerifiedGitMetadataRootsAsync(
        string repository,
        string commit,
        IReadOnlyCollection<string>? parentMetadataHierarchy,
        GitMetadataTraversalMode traversalMode,
        Dictionary<string, HashSet<string>>? repositoryBindings = null)
    {
        repository = Path.GetFullPath(repository);
        var ownRoots = await GitMetadataRootsAsync(repository);
        GitMetadataBoundary.EnsureRepositoryRoots(
            repository,
            ownRoots,
            parentMetadataHierarchy,
            isTopLevel: parentMetadataHierarchy is null);

        if (repositoryBindings is not null)
        {
            if (!repositoryBindings.TryAdd(
                    repository,
                    ownRoots.ToHashSet(StringComparer.Ordinal)))
                throw new AgentException(
                    $"Duplicate repository metadata binding: {repository}");
        }

        var hierarchy = new HashSet<string>(
            parentMetadataHierarchy ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        hierarchy.UnionWith(ownRoots);

        var entries = await ReadTreeAsync(repository, commit);
        foreach (var entry in entries.Where(entry =>
                     entry.Mode == GIT_MODE_GITLINK &&
                     entry.Kind == GIT_OBJECT_COMMIT))
        {
            var submodulePath = GitTreePath.Resolve(
                repository,
                entry.RelativePath);
            if (!File.Exists(submodulePath) && !Directory.Exists(submodulePath))
            {
                if (traversalMode == GitMetadataTraversalMode.StrictTargetCommit)
                    throw new AgentException($"gitlink is not populated: {entry.RelativePath}");
                continue;
            }

            if (!Directory.Exists(submodulePath) ||
                new DirectoryInfo(submodulePath).LinkTarget is not null)
            {
                if (traversalMode == GitMetadataTraversalMode.StrictTargetCommit)
                    throw new AgentException($"gitlink is not a real directory: {entry.RelativePath}");
                continue;
            }

            var gitMarker = Path.Combine(submodulePath, GIT_METADATA_NAME);
            if (!File.Exists(gitMarker) && !Directory.Exists(gitMarker))
            {
                if (traversalMode == GitMetadataTraversalMode.StrictTargetCommit)
                    throw new AgentException($"gitlink metadata is missing: {entry.RelativePath}");
                continue;
            }

            if (new FileInfo(gitMarker).LinkTarget is not null)
            {
                if (traversalMode == GitMetadataTraversalMode.StrictTargetCommit)
                    throw new AgentException($"gitlink metadata marker is a symlink: {entry.RelativePath}");
                continue;
            }

            var childOwnRoots = await GitMetadataRootsAsync(submodulePath);
            GitMetadataBoundary.EnsureRepositoryRoots(
                submodulePath,
                childOwnRoots,
                hierarchy,
                isTopLevel: false);

            var childCommit = entry.ObjectId;
            if (traversalMode == GitMetadataTraversalMode.RecoveryCurrentCheckout)
            {
                var currentHead = await GitAtAsync(
                    submodulePath,
                    [GIT_SUBCOMMAND_REV_PARSE, "HEAD"],
                    required: false);
                if (!currentHead.Success || string.IsNullOrWhiteSpace(currentHead.StdOut))
                {
                    hierarchy.UnionWith(childOwnRoots);
                    continue;
                }
                childCommit = currentHead.StdOut.Trim();
            }
            else if (traversalMode == GitMetadataTraversalMode.RecoveryRollbackTarget)
            {
                var targetAvailable = await GitAtAsync(
                    submodulePath,
                    ["cat-file", "-e", $"{entry.ObjectId}^{{commit}}"],
                    required: false);
                if (!targetAvailable.Success)
                {
                    var currentHead = await GitAtAsync(
                        submodulePath,
                        [GIT_SUBCOMMAND_REV_PARSE, "HEAD"],
                        required: false);
                    if (!currentHead.Success || string.IsNullOrWhiteSpace(currentHead.StdOut))
                    {
                        hierarchy.UnionWith(childOwnRoots);
                        continue;
                    }
                    childCommit = currentHead.StdOut.Trim();
                }
            }

            var childRoots = await VerifiedGitMetadataRootsAsync(
                submodulePath,
                childCommit,
                hierarchy,
                traversalMode,
                repositoryBindings);
            hierarchy.UnionWith(childRoots);
        }

        return hierarchy.Order(StringComparer.Ordinal).ToList();
    }

    private async Task AssertNoRelatedGitAsync(IReadOnlySet<string> protectedRoots)
    {
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(procDir);
            if (!int.TryParse(name, out _))
                continue;

            string[] argv;
            try
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(procDir, "cmdline"));
                argv = StrictUtf8.GetString(bytes)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (UnauthorizedAccessException) { throw new AgentException($"Cannot inspect cmdline for process {name}"); }

            var argvExeName = argv.Length > 0 ? Path.GetFileName(argv[0]) : "";
            var exeName = argvExeName;
            try
            {
                var target = new FileInfo(Path.Combine(procDir, "exe")).LinkTarget;
                if (!string.IsNullOrEmpty(target))
                {
                    var procExeName = Path.GetFileName(target);
                    if (GitProcessIdentity.IsGitExecutable(procExeName) ||
                        !GitProcessIdentity.IsGitExecutable(argvExeName))
                        exeName = procExeName;
                }
            }
            catch { }

            if (!GitProcessIdentity.IsGitExecutable(exeName))
                continue;

            if (await GitProcessReferencesProtectedAsync(procDir, argv, protectedRoots))
                throw new AgentException($"Live git process {name} still references deployment checkout");
        }
    }

    private async Task<bool> GitProcessReferencesProtectedAsync(
        string procDir,
        string[] argv,
        IReadOnlySet<string> protectedRoots)
    {
        string cwd;
        try
        {
            cwd = ResolveProcLink(Path.Combine(procDir, "cwd"))
                ?? throw new AgentException($"Cannot resolve cwd for git process {Path.GetFileName(procDir)}");
        }
        catch (FileNotFoundException) { return false; }

        if (protectedRoots.Any(root => PathsIntersect(cwd, root)))
            return true;

        var environment = await ReadProcessEnvironmentAsync(procDir);
        foreach (var key in new[]
                 {
                     GIT_ENV_DIR, GIT_ENV_WORK_TREE, GIT_ENV_COMMON_DIR, GIT_ENV_INDEX_FILE,
                     GIT_ENV_OBJECT_DIRECTORY, GIT_ENV_CONFIG_SYSTEM, GIT_ENV_CONFIG_GLOBAL
                 })
        {
            if (environment.TryGetValue(key, out var value) && PointsIntoProtected(value, cwd, protectedRoots))
                return true;
        }

        if (environment.TryGetValue(GIT_ENV_ALTERNATE_OBJECT_DIRECTORIES, out var alternatives))
        {
            foreach (var value in alternatives.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                if (PointsIntoProtected(value, cwd, protectedRoots))
                    return true;
        }

        if (GitProcessArguments.ReferencesProtectedPath(
                argv,
                value => PointsIntoProtected(value, cwd, protectedRoots),
                name => environment.TryGetValue(name, out var value) ? value : null))
            return true;

        if (GitProcessEnvironment.ReferencesProtectedWorktree(
                environment,
                value => PointsIntoProtected(value, cwd, protectedRoots)))
            return true;

        var fdDir = Path.Combine(procDir, "fd");
        try
        {
            foreach (var fd in Directory.EnumerateFileSystemEntries(fdDir))
            {
                var target = ResolveProcLink(fd);
                if (target is not null && protectedRoots.Any(root => IsUnder(target, root)))
                    return true;
            }
        }
        catch (DirectoryNotFoundException) { return false; }
        catch (UnauthorizedAccessException)
        {
            throw new AgentException($"Cannot inspect fds for git process {Path.GetFileName(procDir)}");
        }

        // Delegate the complete Git config grammar to Git itself. Preserve only
        // selector/config environment from the live process and fail closed if
        // GIT_CONFIG_PARAMETERS exists but cannot be resolved.
        var probeEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
            ["LANG"] = "C",
            ["LC_ALL"] = "C"
        };
        foreach (var pair in environment)
        {
            if (pair.Key is "HOME" or "XDG_CONFIG_HOME" or GIT_ENV_DIR or GIT_ENV_WORK_TREE or
                GIT_ENV_COMMON_DIR or GIT_ENV_INDEX_FILE or GIT_ENV_OBJECT_DIRECTORY or
                GIT_ENV_ALTERNATE_OBJECT_DIRECTORIES or GIT_ENV_CONFIG_COUNT or GIT_ENV_CONFIG_PARAMETERS or
                GIT_ENV_CONFIG_SYSTEM or GIT_ENV_CONFIG_GLOBAL or GIT_ENV_CONFIG_NOSYSTEM or
                GIT_ENV_CEILING_DIRECTORIES or GIT_ENV_DISCOVERY_ACROSS_FILESYSTEM ||
                pair.Key.StartsWith(GIT_ENV_CONFIG_KEY_PREFIX, StringComparison.Ordinal) ||
                pair.Key.StartsWith(GIT_ENV_CONFIG_VALUE_PREFIX, StringComparison.Ordinal))
                probeEnv[pair.Key] = pair.Value;
        }

        var probeArgs = new List<string>();
        for (var i = 1; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (arg == "--")
                break;
            if (arg is GIT_FLAG_CHDIR or GIT_FLAG_CONFIG or GIT_FLAG_DIR or GIT_FLAG_WORK_TREE)
            {
                if (++i >= argv.Length)
                    throw new AgentException("Malformed Git global selector");
                probeArgs.Add(arg);
                probeArgs.Add(argv[i]);
                continue;
            }
            if ((arg.StartsWith(GIT_FLAG_CHDIR, StringComparison.Ordinal) && arg != GIT_FLAG_CHDIR) ||
                (arg.StartsWith(GIT_FLAG_CONFIG, StringComparison.Ordinal) && arg != GIT_FLAG_CONFIG) ||
                arg.StartsWith(GIT_FLAG_DIR + "=", StringComparison.Ordinal) ||
                arg.StartsWith(GIT_FLAG_WORK_TREE + "=", StringComparison.Ordinal))
            {
                probeArgs.Add(arg);
                continue;
            }
            if (arg.StartsWith(GIT_FLAG_CONFIG_ENV_PREFIX, StringComparison.Ordinal))
            {
                var spec = arg[GIT_FLAG_CONFIG_ENV_PREFIX.Length..];
                var eq = spec.IndexOf('=');
                if (eq <= 0)
                    throw new AgentException("Malformed --config-env");
                var envName = spec[(eq + 1)..];
                if (!environment.TryGetValue(envName, out var envValue))
                    throw new AgentException("Unresolvable --config-env");
                probeEnv[envName] = envValue;
                probeArgs.Add(arg);
                continue;
            }
            if (arg.StartsWith('-'))
                continue;
            break;
        }

        var resolvedAnyGitPath = false;
        var resolvedGitDir = false;
        var resolvedCommonDir = false;
        foreach (var selector in new[] { "--show-toplevel", GIT_FLAG_DIR, "--git-common-dir" })
        {
            var pathProbeArgs = new List<string>(probeArgs);
            pathProbeArgs.AddRange([
                GIT_FLAG_CONFIG,
                "safe.directory=*",
                GIT_SUBCOMMAND_REV_PARSE,
                "--path-format=absolute",
                selector
            ]);

            var probe = await ProcessRunner.RunAsync(
                COMMAND_GIT,
                pathProbeArgs,
                TimeSpan.FromSeconds(2),
                cwd,
                probeEnv,
                clearEnvironment: true);
            if (!probe.Success)
                continue;

            resolvedAnyGitPath = true;
            resolvedGitDir |= selector == GIT_FLAG_DIR;
            resolvedCommonDir |= selector == "--git-common-dir";

            var effective = probe.StdOut.Trim();
            if (string.IsNullOrEmpty(effective))
                throw new AgentException($"Git returned an empty effective path for {selector}");
            if (GitEffectivePathProbe.ReferencesProtected(
                    new[] { effective },
                    protectedRoots))
                return true;
        }

        if (resolvedAnyGitPath && (!resolvedGitDir || !resolvedCommonDir))
            throw new AgentException("Cannot resolve complete live Git metadata paths");

        if (!resolvedAnyGitPath &&
            (environment.ContainsKey(GIT_ENV_CONFIG_PARAMETERS) ||
             environment.ContainsKey(GIT_ENV_CONFIG_COUNT)))
            throw new AgentException("Cannot resolve live Git configuration");

        return false;
    }

    private static async Task<Dictionary<string, string>> ReadProcessEnvironmentAsync(string procDir)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(procDir, "environ"));
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (UnauthorizedAccessException)
        {
            throw new AgentException($"Cannot inspect environment for git process {Path.GetFileName(procDir)}");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in StrictUtf8.GetString(bytes).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq > 0)
                result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    private async Task<List<TreeEntry>> ReadTreeAsync(string repository, string commit)
    {
        var bytes = await ProcessRunner.RunBytesAsync(
            COMMAND_GIT,
            [GIT_FLAG_CHDIR, repository, GIT_SUBCOMMAND_LS_TREE, "-rz", "--full-tree", commit],
            TimeSpan.FromSeconds(30));

        var result = new List<TreeEntry>();
        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i != bytes.Length && bytes[i] != 0)
                continue;
            if (i == start)
            {
                start = i + 1;
                continue;
            }
            var record = bytes.AsSpan(start, i - start);
            var tab = record.IndexOf((byte)'\t');
            if (tab < 0)
                throw new AgentException("Malformed git ls-tree record");
            var meta = Encoding.ASCII.GetString(record[..tab]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length != 3)
                throw new AgentException("Malformed git ls-tree metadata");
            var relative = StrictUtf8.GetString(record[(tab + 1)..]);
            result.Add(new TreeEntry(meta[0], meta[1], meta[2], relative));
            start = i + 1;
        }
        return result;
    }

    private async Task<ProcessResult> GitAsync(IEnumerable<string> args)
        => await GitAtAsync(_config.AppDirectory, args);

    private async Task GitRequiredAsync(IEnumerable<string> args)
    {
        var result = await GitAsync(args);
        if (!result.Success)
            throw new AgentException($"Git command failed: {result.StdErr.Trim()}");
    }

    private static async Task<ProcessResult> GitAtAsync(string repository, IEnumerable<string> args, bool required = true)
    {
        var all = new List<string> { GIT_FLAG_CHDIR, repository };
        all.AddRange(args);
        var result = await ProcessRunner.RunAsync(COMMAND_GIT, all, TimeSpan.FromMinutes(2));
        if (required && !result.Success)
            throw new AgentException($"Git command failed in {repository}: {result.StdErr.Trim()}");
        return result;
    }

    private static RepositoryDiskTreeSnapshot EnumerateDiskTree(
        string repository,
        IEnumerable<string> submodules)
    {
        var submoduleSet = submodules.ToHashSet(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        var directories = new HashSet<string>(StringComparer.Ordinal);
        Walk(repository, "");
        return new RepositoryDiskTreeSnapshot(files, directories);

        void Walk(string directory, string relativeDirectory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                var relative = string.IsNullOrEmpty(relativeDirectory)
                    ? name
                    : $"{relativeDirectory}/{name}";
                if (relativeDirectory.Length == 0 && name == GIT_METADATA_NAME)
                    continue;
                if (submoduleSet.Contains(relative))
                    continue;

                var fileInfo = new FileInfo(entry);
                if (fileInfo.LinkTarget is not null)
                {
                    files.Add(relative);
                    continue;
                }

                if (Directory.Exists(entry))
                {
                    directories.Add(relative);
                    Walk(entry, relative);
                    continue;
                }

                files.Add(relative);
            }
        }
    }

    internal static void EnsureDiskTreeMatches(
        string repository,
        IReadOnlySet<string> expectedFiles,
        IReadOnlySet<string> expectedDirectories,
        RepositoryDiskTreeSnapshot actual,
        string context = "")
    {
        if (!actual.Files.SetEquals(expectedFiles))
        {
            var extra = actual.Files.Except(expectedFiles).Order(StringComparer.Ordinal);
            var missing = expectedFiles.Except(actual.Files).Order(StringComparer.Ordinal);
            throw new AgentException(
                $"Worktree file set{context} in {repository}; " +
                $"extra=[{string.Join(",", extra)}] missing=[{string.Join(",", missing)}]");
        }

        if (!actual.Directories.SetEquals(expectedDirectories))
        {
            var extra = actual.Directories.Except(expectedDirectories).Order(StringComparer.Ordinal);
            var missing = expectedDirectories.Except(actual.Directories).Order(StringComparer.Ordinal);
            throw new AgentException(
                $"Worktree directory set{context} in {repository}; " +
                $"extra=[{string.Join(",", extra)}] missing=[{string.Join(",", missing)}]");
        }
    }

    private static string GitBlobObjectId(byte[] data, string algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm == GIT_OBJECT_FORMAT_SHA1 ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes($"{GIT_OBJECT_BLOB} {data.Length}\0"));
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AddParentChain(HashSet<string> dirs, string path, string repository)
    {
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            dirs.Add(parent);
            if (Path.GetFullPath(parent) == Path.GetFullPath(repository))
                break;
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static bool PointsIntoProtected(string value, string cwd, IReadOnlySet<string> protectedRoots)
    {
        try
        {
            var path = Path.IsPathFullyQualified(value) ? value : Path.Combine(cwd, value);
            path = Path.GetFullPath(path);
            return protectedRoots.Any(root => PathsIntersect(path, root));
        }
        catch
        {
            return true;
        }
    }

    private static bool PathsIntersect(string a, string b) => IsUnder(a, b) || IsUnder(b, a);

    private static bool IsUnder(string child, string parent)
    {
        child = Path.TrimEndingDirectorySeparator(Path.GetFullPath(child));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return child == parent || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? ResolveProcLink(string path)
    {
        var info = new FileInfo(path);
        var target = info.LinkTarget;
        if (target is null)
            return null;
        if (Path.IsPathFullyQualified(target))
            return Path.GetFullPath(target.Replace(" (deleted)", "", StringComparison.Ordinal));
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, target));
    }

    private sealed record TreeEntry(string Mode, string Kind, string ObjectId, string RelativePath);
}

internal static class GitEffectivePathProbe
{
    internal static bool ReferencesProtected(
        IEnumerable<string> effectivePaths,
        IReadOnlySet<string> protectedRoots)
    {
        foreach (var path in effectivePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new AgentException("Git returned an empty effective path");

            var fullPath = Path.GetFullPath(path.Trim());
            if (protectedRoots.Any(root =>
                    IsSameOrDescendant(fullPath, root) ||
                    IsSameOrDescendant(root, fullPath)))
                return true;
        }

        return false;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

internal static class GitProcessEnvironment
{
    internal static bool ReferencesProtectedWorktree(
        IReadOnlyDictionary<string, string> environment,
        Func<string, bool> pointsIntoProtected)
    {
        if (!environment.TryGetValue(GIT_ENV_CONFIG_COUNT, out var rawCount))
            return false;

        if (!int.TryParse(rawCount, out var count) || count < 0 || count > 10_000)
            throw new AgentException("Malformed or unsafe GIT_CONFIG_COUNT");

        for (var index = 0; index < count; index++)
        {
            var keyName = $"{GIT_ENV_CONFIG_KEY_PREFIX}{index}";
            var valueName = $"{GIT_ENV_CONFIG_VALUE_PREFIX}{index}";
            if (!environment.TryGetValue(keyName, out var key) ||
                !environment.TryGetValue(valueName, out var value))
                throw new AgentException("Incomplete Git config environment");

            if (key.Equals(GIT_CONFIG_CORE_WORKTREE, StringComparison.OrdinalIgnoreCase) &&
                pointsIntoProtected(value))
                return true;
        }

        return false;
    }
}

internal static class GitProcessArguments
{
    internal static bool ReferencesProtectedPath(
        IReadOnlyList<string> argv,
        Func<string, bool> pointsIntoProtected,
        Func<string, string?>? getEnvironment = null)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            var argument = argv[index];

            if (argument.StartsWith(GIT_FLAG_CONFIG_ENV_PREFIX, StringComparison.Ordinal))
            {
                var spec = argument[GIT_FLAG_CONFIG_ENV_PREFIX.Length..];
                var equals = spec.IndexOf('=');
                if (equals <= 0 || equals == spec.Length - 1)
                    throw new AgentException("Malformed Git --config-env selector");

                var key = spec[..equals];
                var environmentName = spec[(equals + 1)..];
                var value = getEnvironment?.Invoke(environmentName)
                    ?? throw new AgentException("Unresolvable Git --config-env selector");

                if (key.Equals(GIT_CONFIG_CORE_WORKTREE, StringComparison.OrdinalIgnoreCase) &&
                    pointsIntoProtected(value))
                    return true;
                continue;
            }

            if (argument is GIT_FLAG_DIR or GIT_FLAG_WORK_TREE or GIT_FLAG_SEPARATE_GIT_DIR or GIT_FLAG_CHDIR)
            {
                if (++index >= argv.Count || string.IsNullOrEmpty(argv[index]))
                    throw new AgentException("Malformed Git path selector");
                if (pointsIntoProtected(argv[index]))
                    return true;
                continue;
            }

            if (TryGetEqualsPathSelector(argument, out var pathSelector))
            {
                if (pointsIntoProtected(pathSelector))
                    return true;
                continue;
            }

            if (argument.StartsWith(GIT_FLAG_CHDIR, StringComparison.Ordinal) &&
                argument.Length > GIT_FLAG_CHDIR.Length)
            {
                if (pointsIntoProtected(argument[GIT_FLAG_CHDIR.Length..]))
                    return true;
                continue;
            }

            if (argument == GIT_FLAG_CONFIG)
            {
                if (++index >= argv.Count)
                    throw new AgentException("Malformed Git config selector");
                if (CoreWorktreePointsIntoProtected(argv[index], pointsIntoProtected))
                    return true;
                continue;
            }

            if (argument.StartsWith(GIT_FLAG_CONFIG, StringComparison.Ordinal) &&
                argument.Length > GIT_FLAG_CONFIG.Length)
            {
                if (CoreWorktreePointsIntoProtected(
                        argument[GIT_FLAG_CONFIG.Length..],
                        pointsIntoProtected))
                    return true;
                continue;
            }

            if (TryGetEqualsOptionValue(argument, out var optionValue) &&
                pointsIntoProtected(optionValue))
                return true;

            if (!argument.StartsWith('-') && pointsIntoProtected(argument))
                return true;
        }

        return false;
    }

    private static bool TryGetEqualsOptionValue(string argument, out string value)
    {
        value = "";
        if (!argument.StartsWith('-'))
            return false;

        var equals = argument.IndexOf('=');
        if (equals < 0 || equals == argument.Length - 1)
            return false;

        value = argument[(equals + 1)..];
        return true;
    }

    private static bool TryGetEqualsPathSelector(string argument, out string value)
    {
        foreach (var flag in new[] { GIT_FLAG_DIR, GIT_FLAG_WORK_TREE, GIT_FLAG_SEPARATE_GIT_DIR })
        {
            var prefix = flag + "=";
            if (!argument.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            value = argument[prefix.Length..];
            if (value.Length == 0)
                throw new AgentException("Malformed Git path selector");
            return true;
        }

        value = "";
        return false;
    }

    private static bool CoreWorktreePointsIntoProtected(
        string config,
        Func<string, bool> pointsIntoProtected)
    {
        var equals = config.IndexOf('=');
        if (equals <= 0)
            return false;

        var key = config[..equals];
        var value = config[(equals + 1)..];
        return key.Equals(GIT_CONFIG_CORE_WORKTREE, StringComparison.OrdinalIgnoreCase) &&
               pointsIntoProtected(value);
    }
}
