using System.Text;
using Exergism.DeploymentAttestation.Agent;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    AgentHealthStore? healthStore = null;

    try
    {
        var action = AgentActionParser.ParseArgs(args);
        if (action == AgentAction.SelfTest)
            return SelfTest.Run();

        var configPath = Environment.GetEnvironmentVariable(ENV_ATTESTATION_CONFIG) ?? DEFAULT_CONFIG_PATH;
        var config = AgentConfig.Load(configPath);

        if (action == AgentAction.ValidateConfig)
            return 0;

        if (action is AgentAction.Health or AgentAction.Status)
        {
            var health = new AgentSelfHealthService(config);
            var report = await health.EvaluateAsync();
            Console.WriteLine(Encoding.UTF8.GetString(health.WriteJson(report)));
            return action == AgentAction.Status || report.Status == STATUS_HEALTHY ? 0 : 1;
        }

        using var coordination = AgentLock.Acquire(
            config.CoordinationLockPath,
            alreadyHeld: Environment.GetEnvironmentVariable(ENV_AGENT_COORDINATION_LOCK_HELD) == CONFIG_BOOLEAN_TRUE,
            recovery: action == AgentAction.Recover);
        if (coordination is null)
            return 0;

        Durability.EnsureTrustedDirectory(
            config.StateDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Durability.EnsureTrustedDirectory(
            config.BackupDirectory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var stateLock = AgentLock.Acquire(
            config.StateLockPath,
            alreadyHeld: false,
            recovery: action == AgentAction.Recover);
        if (stateLock is null)
            return 0;

        foreach (var phase in new[] { PHASE_PENDING, PHASE_VALIDATED, PHASE_RECOVERING, PHASE_RECOVERED })
        {
            var path = Path.Combine(config.InstallTransactionRoot, $"{config.Service}.{phase}");
            if (Durability.PathExistsNoFollow(path))
                throw new AgentException(
                    $"Installer transaction phase '{phase}' is still actionable or malformed; run installer recovery before deployment-agent work");
        }

        healthStore = new AgentHealthStore(config);
        healthStore.BeginCycle(action);

        using var getHttp = HttpClientFactory.CreateGet();
        using var attestationHttp = HttpClientFactory.CreateAttestation();
        var agent = new DeploymentAgent(config, getHttp, attestationHttp, healthStore);
        var result = await agent.ExecuteAsync(action);

        AgentCycleLifecycle.Complete(healthStore, result);
        return result.ExitCode;
    }
    catch (AgentException ex)
    {
        RecordHealthFailure(healthStore, ex.Message);
        Console.Error.WriteLine($"ERROR: {ex.Message}");
        return 1;
    }
    catch (Exception ex)
    {
        RecordHealthFailure(healthStore, ex.ToString());
        Console.Error.WriteLine($"ERROR: unhandled agent failure: {ex}");
        return 1;
    }
}

static void RecordHealthFailure(AgentHealthStore? healthStore, string error)
{
    if (healthStore is null)
        return;

    try
    {
        healthStore.CompleteFailure(error);
    }
    catch (Exception healthError)
    {
        Console.Error.WriteLine($"ERROR: could not persist agent health failure: {healthError.Message}");
    }
}
