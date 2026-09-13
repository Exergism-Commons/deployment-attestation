using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class DomainAndConfigTests
{
    [DataTestMethod]
    [DataRow(ACTION_RUN, AgentAction.Run)]
    [DataRow(ACTION_UPDATE, AgentAction.Update)]
    [DataRow(ACTION_ATTEST, AgentAction.Attest)]
    [DataRow(ACTION_HEALTH, AgentAction.Health)]
    [DataRow(ACTION_STATUS, AgentAction.Status)]
    [DataRow(ACTION_RECOVER, AgentAction.Recover)]
    [DataRow(ACTION_SELF_TEST, AgentAction.SelfTest)]
    public void ActionsRoundTripWithoutMagicStrings(string wireValue, AgentAction expected)
    {
        var action = AgentActionParser.Parse(wireValue);
        Assert.AreEqual(expected, action);
        Assert.AreEqual(wireValue, AgentActionParser.ToWireValue(action));
    }

    [TestMethod]
    public void InvalidActionFailsClosed()
        => Assert.ThrowsException<AgentException>(() => AgentActionParser.Parse("deploy"));

    [TestMethod]
    public void StaticConfigParserRejectsShellExpansion()
    {
        Assert.ThrowsException<AgentException>(() => AgentConfig.ParseValue("$HOME/runtime"));
        Assert.ThrowsException<AgentException>(() => AgentConfig.ParseValue("\"$HOME/runtime\""));
        Assert.ThrowsException<AgentException>(() => AgentConfig.ParseValue("\u0060id\u0060"));
    }

    [TestMethod]
    public void ConfigComputesSelfHealthPathsAndAge()
    {
        using var environment = TestEnvironment.Create();
        Assert.AreEqual(Path.Combine(environment.Config.StateDirectory, FILE_AGENT_HEALTH), environment.Config.AgentHealthFile);
        Assert.AreEqual("ec-deployment-attestation@id.exergism.org.timer", environment.Config.AgentTimerUnit);
        Assert.AreEqual(TimeSpan.FromSeconds(1500), environment.Config.AgentHealthMaxAge);
    }
}
