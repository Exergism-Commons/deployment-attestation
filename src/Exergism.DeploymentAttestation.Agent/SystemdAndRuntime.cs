using System.Text.RegularExpressions;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class SystemdController(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    public async Task<string> ShowAsync(string property)
    {
        var result = await ProcessRunner.RunAsync(
            "systemctl",
            ["show", _config.ServiceUnit, $"-p={property}", "--value"],
            TimeSpan.FromSeconds(10));
        if (!result.Success)
            throw new AgentException($"Could not read systemd {property}: {result.StdErr.Trim()}");
        return result.StdOut.Trim();
    }

    public async Task<int> MainPidAsync()
    {
        var value = await ShowAsync("MainPID");
        return int.TryParse(value, out var pid) && pid > 0
            ? pid
            : throw new AgentException("Target service has no live MainPID");
    }

    public async Task<bool> IsActiveAsync()
    {
        var result = await ProcessRunner.RunAsync(
            "systemctl", ["is-active", "--quiet", _config.ServiceUnit], TimeSpan.FromSeconds(10));
        return result.Success;
    }

    public async Task StartAsync()
    {
        var result = await ProcessRunner.RunAsync("systemctl", ["start", _config.ServiceUnit], TimeSpan.FromMinutes(2));
        if (!result.Success)
            throw new AgentException($"Could not start {_config.ServiceUnit}: {result.StdErr.Trim()}");
    }

    public async Task StopAsync()
    {
        _ = await ProcessRunner.RunAsync("systemctl", ["stop", _config.ServiceUnit], TimeSpan.FromMinutes(2));
    }

    public async Task<bool> IsQuiescentAsync()
    {
        string load;
        string active;
        string mainPid;
        string cgroup;
        try
        {
            load = await ShowAsync("LoadState");
            active = await ShowAsync("ActiveState");
            mainPid = await ShowAsync("MainPID");
            cgroup = await ShowAsync("ControlGroup");
        }
        catch
        {
            return false;
        }

        if (load != "loaded" || (active != "inactive" && active != "failed") || mainPid != "0" || string.IsNullOrWhiteSpace(cgroup))
            return false;

        var root = Path.Combine("/sys/fs/cgroup", cgroup.TrimStart('/'));
        if (!Directory.Exists(root))
            return false;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "cgroup.procs", SearchOption.AllDirectories))
            {
                if (!string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(file)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
        return true;
    }

    public async Task StopQuiescentAsync()
    {
        await StopAsync();
        for (var i = 0; i < 30; i++)
        {
            if (await IsQuiescentAsync())
                return;
            await Task.Delay(1000);
        }
        throw new AgentException("Target service did not become provably quiescent");
    }

    public async Task<bool> RunSmokeAsync()
    {
        if (string.IsNullOrEmpty(_config.SmokeScript))
            return true;
        if (!File.Exists(_config.SmokeScript))
            return false;

        var unit = $"ec-smoke-{Sanitize(_config.Service)}-{Environment.ProcessId}-{Guid.NewGuid():N}.service";
        var args = new[]
        {
            "--quiet", "--wait", "--collect", $"--unit={unit}",
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
            $"--setenv=EC_PUBLIC_URL={_config.PublicUrl}",
            $"--setenv=EC_LOCAL_URL={_config.LocalUrl}",
            _config.SmokeScript
        };

        var result = await ProcessRunner.RunAsync(
            "systemd-run",
            args,
            _config.SmokeTimeout + TimeSpan.FromSeconds(15));
        return result.Success;
    }

    private static string Sanitize(string value)
        => new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray());
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
            "stat",
            ["-Lc", "%d:%i", $"/proc/{pid}/exe", _config.AppBinary],
            TimeSpan.FromSeconds(5));
        if (!result.Success)
            throw new AgentException("Could not stat live/configured runtime");

        var identities = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (identities.Length != 2 || identities[0] != identities[1])
            throw new AgentException("MainPID executable does not match EC_APP_BIN");

        var digest = Durability.Sha256($"/proc/{pid}/exe");
        var verify = await ProcessRunner.RunAsync(
            "stat",
            ["-Lc", "%d:%i", $"/proc/{pid}/exe", _config.AppBinary],
            TimeSpan.FromSeconds(5));
        var after = verify.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!verify.Success || after.Length != 2 || after[0] != identities[0] || after[1] != identities[1])
            throw new AgentException("Live runtime object changed while hashing");

        return digest;
    }

    private async Task VerifyArtifactFenceAsync(int pid)
    {
        var result = await ProcessRunner.RunAsync(
            "nsenter",
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
                fields[5].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)));
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
            .Where(m => Contains(m.Target, path))
            .OrderByDescending(m => m.Target.Length)
            .FirstOrDefault()
            ?? throw new AgentException($"No mount covers {label} path {path}");

        if (!covering.Options.Contains("ro") || covering.Options.Contains("rw"))
            throw new AgentException($"{label} is writable via {covering.Target}");

        if (!includeChildren)
            return;

        foreach (var mount in mounts.Where(m => Contains(path, m.Target)))
        {
            if (!mount.Options.Contains("ro") || mount.Options.Contains("rw"))
                throw new AgentException($"Writable source submount: {mount.Target}");
        }
    }

    private static bool Contains(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return path == root || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string UnescapeMountPath(string value)
        => Regex.Replace(value, @"\\([0-7]{3})", static m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
}

internal sealed record MountEntry(string Target, HashSet<string> Options);
