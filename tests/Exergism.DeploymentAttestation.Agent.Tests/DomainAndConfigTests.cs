using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class DomainAndConfigTests
{
    [DataTestMethod]
    [DataRow(ACTION_RUN)]
    [DataRow(ACTION_UPDATE)]
    [DataRow(ACTION_ATTEST)]
    [DataRow(ACTION_HEALTH)]
    [DataRow(ACTION_STATUS)]
    [DataRow(ACTION_RECOVER)]
    [DataRow(ACTION_SELF_TEST)]
    public void ActionsRoundTripWithoutMagicStrings(string wireValue)
    {
        var action = AgentActionParser.Parse(wireValue);
        Assert.AreEqual(wireValue, AgentActionParser.ToWireValue(action));
    }

    [TestMethod]
    public void InvalidActionFailsClosed()
        => TestAssert.Throws<AgentException>(() => AgentActionParser.Parse("deploy"));

    [TestMethod]
    public void StaticConfigParserRejectsShellExpansion()
    {
        TestAssert.Throws<AgentException>(() => AgentConfig.ParseValue("$HOME/runtime"));
        TestAssert.Throws<AgentException>(() => AgentConfig.ParseValue("\"$HOME/runtime\""));
        TestAssert.Throws<AgentException>(() => AgentConfig.ParseValue("\u0060id\u0060"));
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
