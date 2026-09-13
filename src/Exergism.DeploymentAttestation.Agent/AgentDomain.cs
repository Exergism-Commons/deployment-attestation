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
    SelfTest
}

internal static class AgentActionParser
{
    internal static AgentAction Parse(string value) => value switch
    {
        ACTION_RUN => AgentAction.Run,
        ACTION_UPDATE => AgentAction.Update,
        ACTION_ATTEST => AgentAction.Attest,
        ACTION_HEALTH => AgentAction.Health,
        ACTION_STATUS => AgentAction.Status,
        ACTION_RECOVER => AgentAction.Recover,
        ACTION_SELF_TEST => AgentAction.SelfTest,
        _ => throw new AgentException(
            $"Usage: ec-deployment-agent [{ACTION_RUN}|{ACTION_UPDATE}|{ACTION_ATTEST}|{ACTION_HEALTH}|{ACTION_STATUS}|{ACTION_RECOVER}|{ACTION_SELF_TEST}]")
    };
}
