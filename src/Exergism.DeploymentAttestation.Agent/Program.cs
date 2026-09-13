namespace Exergism.DeploymentAttestation.Agent;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var action = args.Length == 0 ? AgentAction.Run : AgentAction.Parse(args[0]);
            if (action == AgentAction.SelfTest)
                return SelfTest.Run();

            var configPath = Environment.GetEnvironmentVariable("EC_ATTESTATION_CONFIG")
                ?? "/etc/ec-deployment-attestation/service.env";
            var config = AgentConfig.Load(configPath);

            using var coordination = AgentLock.Acquire(
                config.CoordinationLockPath,
                alreadyHeld: Environment.GetEnvironmentVariable("EC_AGENT_COORDINATION_LOCK_HELD") == "1",
                recovery: action == AgentAction.Recover);
            if (coordination is null)
                return 0;

            Durability.EnsureDirectory(config.StateDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Durability.EnsureDirectory(config.BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var stateLock = AgentLock.Acquire(config.StateLockPath, alreadyHeld: false, recovery: action == AgentAction.Recover);
            if (stateLock is null)
                return 0;

            foreach (var phase in new[] { "pending", "validated", "recovering", "recovered" })
            {
                var path = Path.Combine(config.InstallTransactionRoot, $"{config.Service}.{phase}");
                if (Directory.Exists(path))
                    throw new AgentException($"Installer transaction phase '{phase}' is still actionable; run installer recovery before deployment-agent work");
            }

            using var http = HttpClientFactory.Create();
            var agent = new DeploymentAgent(config, http);
            return await agent.ExecuteAsync(action);
        }
        catch (AgentException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: unhandled agent failure: {ex}");
            return 1;
        }
    }
}

internal sealed class AgentException(string message, Exception? inner = null) : Exception(message, inner);

internal readonly record struct AgentAction(string Value)
{
    public static readonly AgentAction Run = new("run");
    public static readonly AgentAction Update = new("update");
    public static readonly AgentAction Attest = new("attest");
    public static readonly AgentAction Health = new("health");
    public static readonly AgentAction Recover = new("recover");
    public static readonly AgentAction SelfTest = new("self-test");

    public static AgentAction Parse(string value) => value switch
    {
        "run" => Run,
        "update" => Update,
        "attest" => Attest,
        "health" => Health,
        "recover" => Recover,
        "self-test" => SelfTest,
        _ => throw new AgentException("Usage: ec-deployment-agent [run|update|attest|health|recover|self-test]")
    };
}
