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
    Dictionary<string, DirectorySnapshot> Directories);

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

        var allowed = new HashSet<string>(
            parentMetadataHierarchy.Select(Path.GetFullPath),
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
        => _ = await VerifyRepositoryExactAsync(_config.AppDirectory, commit);

    public async Task SwitchSourceAsync(string commit, bool fetchFirst)
    {
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
        var verified = await VerifyRepositoryExactAsync(_config.AppDirectory, commit);
        var metadataHierarchy = await VerifiedGitMetadataRootsAsync(
            _config.AppDirectory,
            commit,
            parentMetadataHierarchy: null);
        await FsyncRepositoryAsync(
            _config.AppDirectory,
            commit,
            verified,
            metadataHierarchy);
        var final = await VerifyRepositoryExactAsync(_config.AppDirectory, commit);
        EnsureSnapshotsUnchanged(verified, final);
    }

    public async Task ReconcileStaleGitLocksAsync()
    {
        var head = await HeadAsync();
        var roots = await VerifiedGitMetadataRootsAsync(
            _config.AppDirectory,
            head,
            parentMetadataHierarchy: null);
        var protectedRoots = new HashSet<string>(roots, StringComparer.Ordinal) { Path.GetFullPath(_config.AppDirectory) };

        await AssertNoRelatedGitAsync(protectedRoots);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                throw new AgentException($"Git metadata root is not a directory: {root}");

            foreach (var lockFile in GitMetadataEnumeration.EnumerateLockFiles(root).Order(StringComparer.Ordinal))
            {
                await AssertNoRelatedGitAsync(protectedRoots);
                if (await AnyProcessHasOpenPathAsync(lockFile))
                    throw new AgentException($"Refusing to remove open git lock: {lockFile}");
                File.Delete(lockFile);
                Durability.FsyncDirectory(Path.GetDirectoryName(lockFile)!);
            }
        }

        await AssertNoRelatedGitAsync(protectedRoots);
    }

    private async Task<RepositoryDurabilitySnapshot> VerifyRepositoryExactAsync(
        string repository,
        string commit,
        RepositoryDurabilitySnapshot? verified = null)
    {
        verified ??= new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        repository = Path.GetFullPath(repository);
        if (!Directory.Exists(repository) || new DirectoryInfo(repository).LinkTarget is not null)
            throw new AgentException($"Repository path is not a real directory: {repository}");

        var head = (await GitAtAsync(repository, [GIT_SUBCOMMAND_REV_PARSE, "HEAD"])).StdOut.Trim();
        if (head != commit)
            throw new AgentException($"HEAD mismatch in {repository}: {head} != {commit}");

        var algorithm = (await GitAtAsync(repository, [GIT_SUBCOMMAND_REV_PARSE, "--show-object-format"])).StdOut.Trim();
        if (algorithm is not (GIT_OBJECT_FORMAT_SHA1 or GIT_OBJECT_FORMAT_SHA256))
            throw new AgentException($"Unsupported Git object format: {algorithm}");

        var entries = await ReadTreeAsync(repository, commit);
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal) { repository };
        var submodules = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var fullPath = Path.Combine(repository, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            AddParentChain(expectedDirectories, fullPath, repository);
            if (entry.Mode == GIT_MODE_GITLINK && entry.Kind == GIT_OBJECT_COMMIT)
            {
                if (!Directory.Exists(fullPath) || new DirectoryInfo(fullPath).LinkTarget is not null)
                    throw new AgentException($"gitlink is not a real directory: {entry.RelativePath}");
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

        var actualFiles = EnumerateDiskFiles(repository, submodules.Keys);
        if (!actualFiles.SetEquals(expectedFiles))
        {
            var extra = actualFiles.Except(expectedFiles).Order(StringComparer.Ordinal);
            var missing = expectedFiles.Except(actualFiles).Order(StringComparer.Ordinal);
            throw new AgentException($"Worktree file set differs; extra=[{string.Join(",", extra)}] missing=[{string.Join(",", missing)}]");
        }

        foreach (var directory in expectedDirectories)
        {
            var fullDirectory = Path.GetFullPath(directory);
            verified.Directories[fullDirectory] =
                Durability.ReadDirectorySnapshotNoFollow(fullDirectory, fullDirectory);
        }

        foreach (var submodule in submodules)
        {
            var subPath = Path.Combine(repository, submodule.Key.Replace('/', Path.DirectorySeparatorChar));
            await VerifyRepositoryExactAsync(subPath, submodule.Value, verified);
        }

        return verified;
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
            var path = Path.Combine(_config.AppDirectory, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
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
        IReadOnlyCollection<string> metadataHierarchy)
    {
        var entries = await ReadTreeAsync(repository, commit);
        var directories = new HashSet<string>(StringComparer.Ordinal) { repository };
        var submodules = new List<(string Path, string Commit)>();

        foreach (var entry in entries)
        {
            var path = Path.Combine(repository, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            AddParentChain(directories, path, repository);

            if (entry.Mode == GIT_MODE_GITLINK && entry.Kind == GIT_OBJECT_COMMIT)
            {
                submodules.Add((path, entry.ObjectId));
                var gitMarker = Path.Combine(path, GIT_METADATA_NAME);
                if (!File.Exists(gitMarker) && !Directory.Exists(gitMarker))
                    throw new AgentException($"Submodule gitfile missing during fsync: {entry.RelativePath}");
                if (File.Exists(gitMarker))
                    Durability.FsyncFile(gitMarker);
                else
                    Durability.FsyncRequiredDirectory(gitMarker, $"submodule git metadata {entry.RelativePath}");
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
            FsyncDirectoryTree(root);

        foreach (var submodule in submodules)
            await FsyncRepositoryAsync(
                submodule.Path,
                submodule.Commit,
                verified,
                metadataHierarchy);
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

    private static void FsyncDirectoryTree(string root)
    {
        if (!Directory.Exists(root))
            throw new AgentException($"Git metadata disappeared: {root}");

        foreach (var file in GitMetadataEnumeration.EnumerateFiles(root))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is null)
                Durability.FsyncFile(file);
        }
        foreach (var dir in GitMetadataEnumeration.EnumerateDirectories(root)
                     .OrderByDescending(x => x.Count(c => c == Path.DirectorySeparatorChar)))
            Durability.FsyncRequiredDirectory(dir, dir);
        Durability.FsyncRequiredDirectory(root, root);
        var parent = Path.GetDirectoryName(root)
            ?? throw new AgentException($"Git metadata root has no parent: {root}");
        Durability.FsyncRequiredDirectory(parent, parent);
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

    private async Task<List<string>> VerifiedGitMetadataRootsAsync(
        string repository,
        string commit,
        IReadOnlyCollection<string>? parentMetadataHierarchy)
    {
        repository = Path.GetFullPath(repository);
        var ownRoots = await GitMetadataRootsAsync(repository);
        GitMetadataBoundary.EnsureRepositoryRoots(
            repository,
            ownRoots,
            parentMetadataHierarchy,
            isTopLevel: parentMetadataHierarchy is null);

        var hierarchy = new HashSet<string>(
            parentMetadataHierarchy ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        hierarchy.UnionWith(ownRoots);

        var entries = await ReadTreeAsync(repository, commit);
        foreach (var entry in entries.Where(entry =>
                     entry.Mode == GIT_MODE_GITLINK &&
                     entry.Kind == GIT_OBJECT_COMMIT))
        {
            var submodulePath = Path.GetFullPath(Path.Combine(
                repository,
                entry.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(submodulePath) ||
                new DirectoryInfo(submodulePath).LinkTarget is not null)
                throw new AgentException($"gitlink is not a real directory: {entry.RelativePath}");

            var childRoots = await VerifiedGitMetadataRootsAsync(
                submodulePath,
                entry.ObjectId,
                hierarchy);
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

        probeArgs.AddRange([GIT_FLAG_CONFIG, "safe.directory=*", GIT_SUBCOMMAND_REV_PARSE, "--path-format=absolute", "--show-toplevel"]);
        var probe = await ProcessRunner.RunAsync(
            COMMAND_GIT, probeArgs, TimeSpan.FromSeconds(2), cwd, probeEnv, clearEnvironment: true);
        if (probe.Success)
        {
            var effective = probe.StdOut.Trim();
            if (string.IsNullOrEmpty(effective))
                throw new AgentException("Git returned an empty effective worktree");
            if (protectedRoots.Any(root => PathsIntersect(effective, root)))
                return true;
        }
        else if (environment.ContainsKey(GIT_ENV_CONFIG_PARAMETERS) ||
                 environment.ContainsKey(GIT_ENV_CONFIG_COUNT))
        {
            throw new AgentException("Cannot resolve live Git configuration");
        }

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

    private static async Task<bool> AnyProcessHasOpenPathAsync(string path)
    {
        path = Path.GetFullPath(path);
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out _))
                continue;
            var fdDir = Path.Combine(procDir, "fd");
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(fdDir))
                {
                    var target = ResolveProcLink(fd);
                    if (target is not null && Path.GetFullPath(target) == path)
                        return true;
                }
            }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException)
            {
                throw new AgentException($"Cannot inspect process {Path.GetFileName(procDir)} fds before lock cleanup");
            }
        }
        await Task.CompletedTask;
        return false;
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

    private static HashSet<string> EnumerateDiskFiles(string repository, IEnumerable<string> submodules)
    {
        var submoduleSet = submodules.ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        Walk(repository, "");
        return found;

        void Walk(string directory, string relativeDirectory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                var relative = string.IsNullOrEmpty(relativeDirectory) ? name : $"{relativeDirectory}/{name}";
                if (relativeDirectory.Length == 0 && name == GIT_METADATA_NAME)
                    continue;
                if (submoduleSet.Contains(relative))
                    continue;

                var fileInfo = new FileInfo(entry);
                if (fileInfo.LinkTarget is not null)
                {
                    found.Add(relative);
                    continue;
                }
                if (Directory.Exists(entry))
                {
                    Walk(entry, relative);
                    continue;
                }
                found.Add(relative);
            }
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
