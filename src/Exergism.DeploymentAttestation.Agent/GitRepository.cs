using System.Security.Cryptography;
using System.Text;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class GitRepository(AgentConfig config)
{
    private readonly AgentConfig _config = config;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<string> HeadAsync()
        => (await GitAsync([GIT_SUBCOMMAND_REV_PARSE, "HEAD"])).StdOut.Trim();

    public async Task VerifySourceTreeExactAsync(string commit)
        => await VerifyRepositoryExactAsync(_config.AppDirectory, commit);

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
        await VerifySourceTreeExactAsync(commit);
        await FsyncRepositoryAsync(_config.AppDirectory, commit);
    }

    public async Task ReconcileStaleGitLocksAsync()
    {
        var roots = await GitMetadataRootsAsync(_config.AppDirectory);
        var protectedRoots = new HashSet<string>(roots, StringComparer.Ordinal) { Path.GetFullPath(_config.AppDirectory) };

        await AssertNoRelatedGitAsync(protectedRoots);

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
                throw new AgentException($"Git metadata root is not a directory: {root}");

            foreach (var lockFile in Directory.EnumerateFiles(root, "*.lock", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
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

    private async Task VerifyRepositoryExactAsync(string repository, string commit)
    {
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
        var submodules = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            var fullPath = Path.Combine(repository, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
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
                if (!File.Exists(fullPath) || info.LinkTarget is not null)
                    throw new AgentException($"Expected regular file: {entry.RelativePath}");
                var mode = File.GetUnixFileMode(fullPath);
                var executable = (mode & UnixFileMode.UserExecute) != 0;
                if (executable != (entry.Mode == GIT_MODE_EXECUTABLE))
                    throw new AgentException($"Executable bit mismatch: {entry.RelativePath}");
                data = await File.ReadAllBytesAsync(fullPath);
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

        foreach (var submodule in submodules)
        {
            var subPath = Path.Combine(repository, submodule.Key.Replace('/', Path.DirectorySeparatorChar));
            await VerifyRepositoryExactAsync(subPath, submodule.Value);
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

    private async Task FsyncRepositoryAsync(string repository, string commit)
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
                    Durability.FsyncDirectory(gitMarker);
                continue;
            }

            if (File.Exists(path) && new FileInfo(path).LinkTarget is null)
                Durability.FsyncFile(path);
        }

        foreach (var directory in directories.OrderByDescending(x => x.Count(c => c == Path.DirectorySeparatorChar)))
            Durability.FsyncDirectory(directory);

        foreach (var root in await GitMetadataRootsAsync(repository))
            FsyncDirectoryTree(root);

        foreach (var submodule in submodules)
            await FsyncRepositoryAsync(submodule.Path, submodule.Commit);
    }

    private static void FsyncDirectoryTree(string root)
    {
        if (!Directory.Exists(root))
            throw new AgentException($"Git metadata disappeared: {root}");

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.LinkTarget is null)
                Durability.FsyncFile(file);
        }
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Count(c => c == Path.DirectorySeparatorChar)))
            Durability.FsyncDirectory(dir);
        Durability.FsyncDirectory(root);
        Durability.FsyncDirectory(Path.GetDirectoryName(root)!);
    }

    private async Task<List<string>> GitMetadataRootsAsync(string repository)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in new[] { GIT_FLAG_DIR, "--git-common-dir" })
        {
            var result = await GitAtAsync(repository, ["rev-parse", "--path-format=absolute", flag]);
            roots.Add(Path.GetFullPath(result.StdOut.Trim()));
        }

        foreach (var marker in Directory.EnumerateFileSystemEntries(repository, GIT_METADATA_NAME, SearchOption.AllDirectories))
        {
            if (Directory.Exists(marker) && new DirectoryInfo(marker).LinkTarget is null)
            {
                roots.Add(Path.GetFullPath(marker));
                continue;
            }
            if (!File.Exists(marker) || new FileInfo(marker).LinkTarget is not null)
                continue;

            var text = (await File.ReadAllTextAsync(marker)).Trim();
            if (!text.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                throw new AgentException($"Malformed gitfile: {marker}");
            var raw = text[7..].Trim();
            var target = Path.IsPathFullyQualified(raw)
                ? raw
                : Path.Combine(Path.GetDirectoryName(marker)!, raw);
            roots.Add(Path.GetFullPath(target));
        }
        return roots.ToList();
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

            var exeName = argv.Length > 0 ? Path.GetFileName(argv[0]) : "";
            try
            {
                var target = new FileInfo(Path.Combine(procDir, "exe")).LinkTarget;
                if (!string.IsNullOrEmpty(target))
                    exeName = Path.GetFileName(target);
            }
            catch { }

            if (!(exeName == "git" || exeName.StartsWith("git-", StringComparison.Ordinal)))
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
        else if (environment.ContainsKey(GIT_ENV_CONFIG_PARAMETERS))
        {
            throw new AgentException("Cannot resolve GIT_CONFIG_PARAMETERS for live Git process");
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

internal static class GitProcessArguments
{
    internal static bool ReferencesProtectedPath(
        IReadOnlyList<string> argv,
        Func<string, bool> pointsIntoProtected)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            var argument = argv[index];

            if (argument is GIT_FLAG_DIR or GIT_FLAG_WORK_TREE or GIT_FLAG_CHDIR)
            {
                if (++index >= argv.Count)
                    throw new AgentException("Malformed Git path selector");
                if (pointsIntoProtected(argv[index]))
                    return true;
                continue;
            }

            if (argument.StartsWith(GIT_FLAG_DIR + "=", StringComparison.Ordinal) ||
                argument.StartsWith(GIT_FLAG_WORK_TREE + "=", StringComparison.Ordinal))
            {
                var value = argument[(argument.IndexOf('=') + 1)..];
                if (pointsIntoProtected(value))
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

            if (!argument.StartsWith('-') && pointsIntoProtected(argument))
                return true;
        }

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

