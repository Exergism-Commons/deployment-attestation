namespace Exergism.DeploymentAttestation.Agent;

internal static class PackageSelfUpdate
{
    internal static string Version
        => typeof(PackageSelfUpdate).Assembly.GetName().Version?.ToString(3)
            ?? throw new AgentException("Package version is unavailable");

    internal static async Task<int> RunAsync(string? admissionTag = null)
    {
        using var snapshot = CreateHelperSnapshot(
            "/usr/local/libexec/ec-deployment-agent-self-update",
            "/var/lib/ec-deployment-attestation/helper-execution");
        var arguments = new List<string> { "-I", snapshot.Path };
        if (admissionTag is not null)
            arguments.AddRange(["--admit", admissionTag]);
        var result = await ProcessRunner.RunAsync(
            "/usr/bin/python3", arguments, TimeSpan.FromHours(1),
            environment: new Dictionary<string, string>
            {
                ["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"
            },
            clearEnvironment: true);
        Console.Write(result.StdOut);
        Console.Error.Write(result.StdErr);
        return result.ExitCode;
    }

    internal static HelperSnapshot CreateHelperSnapshot(string helper, string stagingRoot)
    {
        Durability.ValidateExistingTrustedDirectoryChain(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(helper))!);
        var bytes = Durability.ReadTrustedRegularFileBytes(helper, "package self-update helper");
        var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Durability.EnsureTrustedDirectory(stagingRoot, privateMode);
        var directory = System.IO.Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Durability.EnsureTrustedDirectory(directory, privateMode);
        var snapshot = new HelperSnapshot(directory);
        try
        {
            Durability.AtomicWrite(snapshot.Path, bytes, UnixFileMode.UserRead);
            return snapshot;
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    internal sealed class HelperSnapshot(string directory) : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(directory, "helper.py");

        public void Dispose()
        {
            File.Delete(Path);
            Directory.Delete(directory);
        }
    }
}
