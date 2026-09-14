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
}

internal static class SmokeScriptValidation
{
    internal static bool IsUsable(string path)
    {
        if (string.IsNullOrEmpty(path))
            return true;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
                return false;

            var mode = File.GetUnixFileMode(path);
            return (mode & (
                UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
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
            return true;

        var root = Path.Combine("/sys/fs/cgroup", controlGroup.TrimStart('/'));
        if (!Directory.Exists(root))
            return true;

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
            // Accept the race only when the sampled service cgroup root itself is gone.
            return ServiceQuiescence.TraversalFailureIsQuiescent(Directory.Exists(root));
        }
        catch
        {
            return false;
        }

        return ServiceQuiescence.IsQuiescentSnapshot(
            loadState,
            activeState,
            mainPid,
            cgroupExists: true,
            cgroupHasProcesses: false);
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
            if (!SmokeScriptValidation.IsUsable(_config.SmokeScript))
                return false;
            if (string.IsNullOrEmpty(_config.SmokeScript))
                return true;

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
                _config.SmokeScript
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
        await VerifyArtifactFenceAsync(pid);
        var digest = await BoundRuntimeSha256Async(pid);
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
