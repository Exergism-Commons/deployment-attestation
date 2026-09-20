using System.Text;
using System.Text.RegularExpressions;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal static class ServiceQuiescence
{
    internal static bool TraversalFailureIsQuiescent(bool rootExists)
        => !rootExists;

    internal static bool IsQuiescentSnapshot(
        string loadState,
        string activeState,
        string mainPid,
        bool cgroupExists,
        bool cgroupHasProcesses)
        => loadState == SYSTEMD_STATE_LOADED
           && activeState is SYSTEMD_STATE_INACTIVE or SYSTEMD_STATE_FAILED
           && mainPid == "0"
           && (!cgroupExists || !cgroupHasProcesses);

    internal static bool ResampledSnapshotRemainsQuiescent(
        string initialControlGroup,
        string resampledControlGroup,
        string loadState,
        string activeState,
        string mainPid)
        => string.Equals(initialControlGroup, resampledControlGroup, StringComparison.Ordinal)
           && IsQuiescentSnapshot(
               loadState,
               activeState,
               mainPid,
               cgroupExists: false,
               cgroupHasProcesses: false);
}

internal sealed class PreparedSmokeScript(string path) : IDisposable
{
    internal string Path { get; } = path;

    public void Dispose()
        => Durability.DurableDelete(Path);
}

internal static class SmokeScriptValidation
{
    private const string TRUSTED_RUNTIME_DIRECTORY =
        "/run/ec-deployment-attestation-smoke";

    internal static PreparedSmokeScript PrepareTrustedCopy(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new AgentException("Smoke script path is empty");

        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(path)
            ?? throw new AgentException($"Smoke script has no parent directory: {path}");

        // The original path is accepted only beneath an effective-UID-owned,
        // non-group/other-writable directory chain. The final file itself is
        // opened O_NOFOLLOW and read from that descriptor while owner, mode,
        // identity and ctime/size remain stable.
        Durability.ValidateExistingTrustedDirectoryChain(parent);
        var bytes = Durability.ReadTrustedRegularFileBytes(
            path,
            "smoke script",
            requireExecutable: true);

        // Execute the verified bytes from a root/effective-UID-owned runtime
        // namespace rather than the configurable source pathname. This removes
        // the validation-to-exec replacement window entirely for non-root
        // writers: changing the original after this point cannot change the
        // program systemd-run starts.
        Durability.EnsureTrustedDirectory(
            TRUSTED_RUNTIME_DIRECTORY,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);

        var staged = Path.Combine(
            TRUSTED_RUNTIME_DIRECTORY,
            $"smoke-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Durability.AtomicWrite(
            staged,
            bytes,
            UnixFileMode.UserRead |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);

        return new PreparedSmokeScript(staged);
    }
}

internal sealed class SystemdController(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    internal static string[] BuildShowArguments(string serviceUnit, string property)
        => ["show", serviceUnit, "--property", property, "--value"];

    public async Task<string> ShowAsync(string property)
    {
        var result = await ProcessRunner.RunAsync(
            COMMAND_SYSTEMCTL,
            BuildShowArguments(_config.ServiceUnit, property),
            TimeSpan.FromSeconds(10));
        if (!result.Success)
            throw new AgentException($"Could not read systemd {property}: {result.StdErr.Trim()}");
        return result.StdOut.Trim();
    }

    public async Task<int> MainPidAsync()
    {
        var value = await ShowAsync(SYSTEMD_MAIN_PID);
        return int.TryParse(value, out var pid) && pid > 0
            ? pid
            : throw new AgentException("Target service has no live MainPID");
    }

    public Task<bool> IsActiveAsync()
        => HealthCheckRunner.RunAsync(async () =>
        {
            var result = await ProcessRunner.RunAsync(
                COMMAND_SYSTEMCTL,
                [SYSTEMD_COMMAND_IS_ACTIVE, SYSTEMD_FLAG_QUIET, _config.ServiceUnit],
                TimeSpan.FromSeconds(10));
            return result.Success;
        });

    public async Task StartAsync()
    {
        var result = await ProcessRunner.RunAsync(
            COMMAND_SYSTEMCTL,
            ["start", _config.ServiceUnit],
            TimeSpan.FromMinutes(2));
        if (!result.Success)
            throw new AgentException($"Could not start {_config.ServiceUnit}: {result.StdErr.Trim()}");
    }

    public async Task StopAsync()
    {
        _ = await ProcessRunner.RunAsync(
            COMMAND_SYSTEMCTL,
            ["stop", _config.ServiceUnit],
            TimeSpan.FromMinutes(2));
    }

    public async Task<bool> IsQuiescentAsync()
    {
        string loadState;
        string activeState;
        string mainPid;
        string controlGroup;

        try
        {
            loadState = await ShowAsync(SYSTEMD_LOAD_STATE);
            activeState = await ShowAsync(SYSTEMD_ACTIVE_STATE);
            mainPid = await ShowAsync(SYSTEMD_MAIN_PID);
            controlGroup = await ShowAsync(SYSTEMD_CONTROL_GROUP);
        }
        catch
        {
            return false;
        }

        if (!ServiceQuiescence.IsQuiescentSnapshot(
                loadState,
                activeState,
                mainPid,
                cgroupExists: false,
                cgroupHasProcesses: false))
            return false;

        if (string.IsNullOrWhiteSpace(controlGroup))
            return await ResampleQuiescentTerminalStateAsync(controlGroup);

        var root = Path.Combine("/sys/fs/cgroup", controlGroup.TrimStart('/'));
        if (!Directory.Exists(root))
            return await ResampleQuiescentTerminalStateAsync(controlGroup);

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "cgroup.procs", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(file)))
                    return false;
            }
        }
        catch (DirectoryNotFoundException)
        {
            // A nested cgroup can disappear while a sibling still contains processes.
            // Accept the traversal race only when the sampled service cgroup root itself
            // is gone, then resample systemd terminal state before declaring quiescence.
            if (!ServiceQuiescence.TraversalFailureIsQuiescent(Directory.Exists(root)))
                return false;
        }
        catch
        {
            return false;
        }

        return await ResampleQuiescentTerminalStateAsync(controlGroup);
    }

    private async Task<bool> ResampleQuiescentTerminalStateAsync(string initialControlGroup)
    {
        try
        {
            var loadState = await ShowAsync(SYSTEMD_LOAD_STATE);
            var activeState = await ShowAsync(SYSTEMD_ACTIVE_STATE);
            var mainPid = await ShowAsync(SYSTEMD_MAIN_PID);
            var controlGroup = await ShowAsync(SYSTEMD_CONTROL_GROUP);
            return ServiceQuiescence.ResampledSnapshotRemainsQuiescent(
                initialControlGroup,
                controlGroup,
                loadState,
                activeState,
                mainPid);
        }
        catch
        {
            return false;
        }
    }

    public async Task StopQuiescentAsync()
    {
        await StopAsync();
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (await IsQuiescentAsync())
                return;
            await Task.Delay(1000);
        }

        throw new AgentException("Target service did not become provably quiescent");
    }

    public Task<bool> RunSmokeAsync()
        => HealthCheckRunner.RunAsync(async () =>
        {
            if (string.IsNullOrEmpty(_config.SmokeScript))
                return true;

            using var prepared = SmokeScriptValidation.PrepareTrustedCopy(
                _config.SmokeScript);

            var unit = $"ec-smoke-{Sanitize(_config.Service)}-{Environment.ProcessId}-{Guid.NewGuid():N}.service";
            var arguments = new[]
            {
                "--quiet",
                "--wait",
                "--collect",
                $"--unit={unit}",
                "--property=Type=exec",
                "--property=ExitType=cgroup",
                "--property=KillMode=control-group",
                "--property=DynamicUser=yes",
                "--property=NoNewPrivileges=yes",
                "--property=ProtectSystem=strict",
                "--property=ProtectHome=yes",
                "--property=ProtectControlGroups=yes",
                "--property=ProtectKernelTunables=yes",
                "--property=ProtectKernelModules=yes",
                "--property=PrivateDevices=yes",
                "--property=RestrictSUIDSGID=yes",
                $"--property=RuntimeMaxSec={(int)_config.SmokeTimeout.TotalSeconds}s",
                $"--property=ReadOnlyPaths={_config.AppDirectory}",
                $"--property=ReadOnlyPaths={_config.AppBinary}",
                $"--setenv={ENV_PUBLIC_URL}={_config.PublicUrl}",
                $"--setenv={ENV_LOCAL_URL}={_config.LocalUrl}",
                prepared.Path
            };

            var result = await ProcessRunner.RunAsync(
                COMMAND_SYSTEMD_RUN,
                arguments,
                _config.SmokeTimeout + TimeSpan.FromSeconds(15));
            return result.Success;
        });

    private static string Sanitize(string value)
        => new(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '-').ToArray());
}

internal sealed class RuntimeInspector(AgentConfig config, SystemdController systemd)
{
    private readonly AgentConfig _config = config;
    private readonly SystemdController _systemd = systemd;

    public async Task<string> LiveRuntimeSnapshotSha256Async()
    {
        var pid = await _systemd.MainPidAsync();
        await VerifySourceBindingAsync(pid);
        await VerifyArtifactFenceAsync(pid);
        var digest = await BoundRuntimeSha256Async(pid);
        await VerifySourceBindingAsync(pid);
        var pidAfter = await _systemd.MainPidAsync();
        if (pidAfter != pid)
            throw new AgentException("Production resolver changed PID during live runtime snapshot");
        return digest;
    }

    public async Task<string> RunningRuntimeSha256Async()
    {
        var pid = await _systemd.MainPidAsync();
        var digest = Durability.Sha256($"/proc/{pid}/exe");
        var pidAfter = await _systemd.MainPidAsync();
        if (pidAfter != pid)
            throw new AgentException("Production resolver changed PID while hashing its executable");
        return digest;
    }

    public async Task VerifyLiveRuntimeInvariantsAsync()
        => _ = await LiveRuntimeSnapshotSha256Async();

    private async Task VerifySourceBindingAsync(int pid)
    {
        byte[] commandLine;
        try
        {
            commandLine = await File.ReadAllBytesAsync($"/proc/{pid}/cmdline");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            throw new AgentException("Could not read production resolver command line", exception);
        }

        if (!CommandLineUsesConfiguredSource(commandLine, _config.AppDirectory))
            throw new AgentException("MainPID is not using the configured source/registry paths");

        var pidAfter = await _systemd.MainPidAsync();
        if (pidAfter != pid)
            throw new AgentException("Production resolver changed PID during source-binding audit");
    }

    internal static bool CommandLineUsesConfiguredSource(
        byte[] commandLine,
        string appDirectory)
    {
        string text;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(commandLine);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var arguments = text.Split(
            '\0',
            StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length == 0)
            return false;

        var expectedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(appDirectory));
        var expectedRegistry = Path.Combine(
            expectedRoot,
            RESOLVER_REGISTRY_RELATIVE_PATH);

        return HasExactOption(
                   arguments,
                   RESOLVER_ARG_ROOT,
                   expectedRoot) &&
               HasExactOption(
                   arguments,
                   RESOLVER_ARG_REGISTRY,
                   expectedRegistry);
    }

    private static bool HasExactOption(
        IReadOnlyList<string> arguments,
        string option,
        string expectedValue)
    {
        string? found = null;
        var count = 0;

        for (var index = 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            string? value = null;
            var longOption = "-" + option;

            if (argument == "--")
                break;

            if (argument == option || argument == longOption)
            {
                if (index + 1 >= arguments.Count)
                    return false;
                value = arguments[++index];
            }
            else
            {
                var prefix = option + "=";
                var longPrefix = longOption + "=";
                if (argument.StartsWith(prefix, StringComparison.Ordinal))
                    value = argument[prefix.Length..];
                else if (argument.StartsWith(longPrefix, StringComparison.Ordinal))
                    value = argument[longPrefix.Length..];
            }

            if (value is null)
                continue;

            count++;
            if (count > 1)
                return false;
            found = value;
        }

        return count == 1 &&
               string.Equals(found, expectedValue, StringComparison.Ordinal);
    }

    private async Task<string> BoundRuntimeSha256Async(int pid)
    {
        var result = await ProcessRunner.RunAsync(
            COMMAND_STAT,
            [STAT_FLAG_DEREFERENCE_FORMAT, STAT_FORMAT_DEVICE_INODE, $"/proc/{pid}/exe", _config.AppBinary],
            TimeSpan.FromSeconds(5));
        if (!result.Success)
            throw new AgentException("Could not stat live/configured runtime");

        var identities = result.StdOut.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (identities.Length != 2 || identities[0] != identities[1])
            throw new AgentException("MainPID executable does not match EC_APP_BIN");

        var digest = Durability.Sha256($"/proc/{pid}/exe");
        var verify = await ProcessRunner.RunAsync(
            COMMAND_STAT,
            [STAT_FLAG_DEREFERENCE_FORMAT, STAT_FORMAT_DEVICE_INODE, $"/proc/{pid}/exe", _config.AppBinary],
            TimeSpan.FromSeconds(5));
        var after = verify.StdOut.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!verify.Success ||
            after.Length != 2 ||
            after[0] != identities[0] ||
            after[1] != identities[1])
            throw new AgentException("Live runtime object changed while hashing");

        return digest;
    }

    private async Task VerifyArtifactFenceAsync(int pid)
    {
        var result = await ProcessRunner.RunAsync(
            COMMAND_NSENTER,
            ["--target", pid.ToString(), "--mount", "--", "cat", "/proc/self/mountinfo"],
            TimeSpan.FromSeconds(5));
        if (!result.Success)
            throw new AgentException("Could not inspect production mount namespace");

        var mounts = ParseMountInfo(result.StdOut);
        AssertReadOnly(mounts, _config.AppDirectory, includeChildren: true, "source");
        AssertReadOnly(mounts, _config.AppBinary, includeChildren: false, "runtime");

        var pidAfter = await _systemd.MainPidAsync();
        if (pidAfter != pid)
            throw new AgentException("Production resolver changed PID during artifact-fence audit");
    }

    internal static List<MountEntry> ParseMountInfo(string text)
    {
        var result = new List<MountEntry>();
        foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = raw.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                throw new AgentException("Malformed mountinfo");

            var fields = raw[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 6)
                throw new AgentException("Malformed mountinfo fields");

            result.Add(new MountEntry(
                Path.GetFullPath(UnescapeMountPath(fields[4])),
                fields[5]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .ToHashSet(StringComparer.Ordinal)));
        }

        return result;
    }

    private static void AssertReadOnly(
        IReadOnlyList<MountEntry> mounts,
        string path,
        bool includeChildren,
        string label)
    {
        path = Path.GetFullPath(path);
        var covering = mounts
            .Where(mount => Contains(mount.Target, path))
            .OrderByDescending(mount => mount.Target.Length)
            .FirstOrDefault()
            ?? throw new AgentException($"No mount covers {label} path {path}");

        if (!covering.Options.Contains(MOUNT_OPTION_READ_ONLY) || covering.Options.Contains(MOUNT_OPTION_READ_WRITE))
            throw new AgentException($"{label} is writable via {covering.Target}");

        if (!includeChildren)
            return;

        foreach (var mount in mounts.Where(mount => Contains(path, mount.Target)))
        {
            if (!mount.Options.Contains(MOUNT_OPTION_READ_ONLY) || mount.Options.Contains(MOUNT_OPTION_READ_WRITE))
                throw new AgentException($"Writable source submount: {mount.Target}");
        }
    }

    private static bool Contains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path == root ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string UnescapeMountPath(string value)
        => Regex.Replace(
            value,
            @"\\([0-7]{3})",
            static match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());
}

internal sealed record MountEntry(string Target, HashSet<string> Options);
