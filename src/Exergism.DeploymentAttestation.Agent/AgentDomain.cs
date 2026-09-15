using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class AgentException(string message, Exception? inner = null) : Exception(message, inner);

internal enum AgentAction
{
    Run,
    Update,
    Attest,
    Health,
    Status,
    Recover,
    ValidateConfig,
    SelfTest
}



internal readonly record struct AttestationResult(bool Produced, bool Healthy)
{
    internal static readonly AttestationResult FAILED = new(false, false);
    internal static readonly AttestationResult HEALTHY = new(true, true);
    internal static readonly AttestationResult UNHEALTHY = new(true, false);
}

internal readonly record struct AgentExecutionResult(int ExitCode, bool CycleSucceeded, string? FailureReason)
{
    internal static readonly AgentExecutionResult SUCCESS = new(0, true, null);

    internal static AgentExecutionResult FromAttestation(AttestationResult result)
        => result switch
        {
            { Produced: true, Healthy: true } => SUCCESS,
            { Produced: true, Healthy: false } => new(1, true, null),
            _ => new(1, false, "Attestation could not be produced")
        };
}

internal static class AgentCycleLifecycle
{
    internal static void Complete(AgentHealthStore store, AgentExecutionResult result)
    {
        if (result.CycleSucceeded)
            store.CompleteSuccess();
        else
            store.CompleteFailure(result.FailureReason ?? "Agent cycle failed");
    }
}

internal static class AgentActionParser
{
    internal static AgentAction ParseArgs(IReadOnlyList<string> args)
        => args.Count switch
        {
            0 => AgentAction.Run,
            1 => Parse(args[0]),
            _ => throw new AgentException(
                $"Usage: ec-deployment-agent [{ACTION_RUN}|{ACTION_UPDATE}|{ACTION_ATTEST}|{ACTION_HEALTH}|{ACTION_STATUS}|{ACTION_RECOVER}|{ACTION_VALIDATE_CONFIG}|{ACTION_SELF_TEST}]")
        };

    internal static AgentAction Parse(string value) => value switch
    {
        ACTION_RUN => AgentAction.Run,
        ACTION_UPDATE => AgentAction.Update,
        ACTION_ATTEST => AgentAction.Attest,
        ACTION_HEALTH => AgentAction.Health,
        ACTION_STATUS => AgentAction.Status,
        ACTION_RECOVER => AgentAction.Recover,
        ACTION_VALIDATE_CONFIG => AgentAction.ValidateConfig,
        ACTION_SELF_TEST => AgentAction.SelfTest,
        _ => throw new AgentException(
            $"Usage: ec-deployment-agent [{ACTION_RUN}|{ACTION_UPDATE}|{ACTION_ATTEST}|{ACTION_HEALTH}|{ACTION_STATUS}|{ACTION_RECOVER}|{ACTION_VALIDATE_CONFIG}|{ACTION_SELF_TEST}]")
    };

    internal static string ToWireValue(AgentAction action) => action switch
    {
        AgentAction.Run => ACTION_RUN,
        AgentAction.Update => ACTION_UPDATE,
        AgentAction.Attest => ACTION_ATTEST,
        AgentAction.Health => ACTION_HEALTH,
        AgentAction.Status => ACTION_STATUS,
        AgentAction.Recover => ACTION_RECOVER,
        AgentAction.ValidateConfig => ACTION_VALIDATE_CONFIG,
        AgentAction.SelfTest => ACTION_SELF_TEST,
        _ => throw new AgentException("Unsupported agent action")
    };
}

internal static class DeploymentFailureFlow
{
    internal static async Task ThrowAfterRollbackAndReportAsync(
        Func<Task> rollback,
        Func<Task> report,
        string context,
        Exception original)
    {
        try
        {
            await rollback();
        }
        catch (Exception rollbackFailure)
        {
            throw new AgentException(
                $"{context} and rollback failed",
                new AggregateException(original, rollbackFailure));
        }

        await report();
        throw new AgentException(context, original);
    }
}

internal static class CommittedTransactionJournal
{
    internal static void Cleanup(Action delete)
    {
        try
        {
            delete();
        }
        catch (Exception exception)
        {
            throw new AgentException(
                "Verified committed candidate is healthy but transaction journal cleanup failed; retaining committed recovery state for retry",
                exception);
        }
    }
}

internal static class AttestationReceiverIdentity
{
    internal static string? FromConfiguredEndpoint(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return null;
        return FromUri(uri);
    }

    internal static string FromUri(Uri endpoint)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(endpoint.AbsoluteUri)));
}

