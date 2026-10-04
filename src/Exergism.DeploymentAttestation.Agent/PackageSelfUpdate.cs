namespace Exergism.DeploymentAttestation.Agent;

internal static class PackageSelfUpdate
{
    internal static string Version
        => typeof(PackageSelfUpdate).Assembly.GetName().Version?.ToString(3)
            ?? throw new AgentException("Package version is unavailable");

    internal static async Task<int> RunAsync()
    {
        const string helper = "/usr/local/libexec/ec-deployment-agent-self-update";
        Durability.ReadTrustedRegularFileBytes(helper, "package self-update helper");
        var result = await ProcessRunner.RunAsync(
            "/usr/bin/python3", ["-I", helper], TimeSpan.FromHours(1),
            environment: new Dictionary<string, string>
            {
                ["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin"
            },
            clearEnvironment: true);
        Console.Write(result.StdOut);
        Console.Error.Write(result.StdErr);
        return result.ExitCode;
    }
}

