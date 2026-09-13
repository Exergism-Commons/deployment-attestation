using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class DomainAndConfigTests
{
    [TestMethod]
    [DataRow(ACTION_RUN)]
    [DataRow(ACTION_UPDATE)]
    [DataRow(ACTION_ATTEST)]
    [DataRow(ACTION_HEALTH)]
    [DataRow(ACTION_STATUS)]
    [DataRow(ACTION_RECOVER)]
    [DataRow(ACTION_VALIDATE_CONFIG)]
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
    public void StaticConfigParserRejectsInlineComments()
    {
        TestAssert.Throws<AgentException>(
            () => AgentConfig.ParseValue("/srv/id.exergism.org # checkout"));

        Assert.AreEqual(
            "literal # value",
            AgentConfig.ParseValue("'literal # value'"));
        Assert.AreEqual(
            "literal # value",
            AgentConfig.ParseValue("\"literal # value\""));
    }

    [TestMethod]
    public void ReleaseAssetNameMustBeSingleSafeName()
    {
        Assert.AreEqual(
            RELEASE_MANIFEST_DEFAULT,
            AgentConfig.RequireSafeAssetName(ENV_RELEASE_MANIFEST, RELEASE_MANIFEST_DEFAULT));

        foreach (var invalid in new[]
                 {
                     "../DEPLOYMENT_MANIFEST.json",
                     "/tmp/DEPLOYMENT_MANIFEST.json",
                     "nested/DEPLOYMENT_MANIFEST.json",
                     @"nested\DEPLOYMENT_MANIFEST.json",
                     "..",
                     ".hidden"
                 })
        {
            TestAssert.Throws<AgentException>(
                () => AgentConfig.RequireSafeAssetName(ENV_RELEASE_MANIFEST, invalid));
        }
    }

    [TestMethod]
    public void ConfigComputesSelfHealthPathsAndAge()
    {
        using var environment = TestEnvironment.Create();
        Assert.AreEqual(Path.Combine(environment.Config.StateDirectory, FILE_AGENT_HEALTH), environment.Config.AgentHealthFile);
        Assert.AreEqual("ec-deployment-attestation@id.exergism.org.timer", environment.Config.AgentTimerUnit);
        Assert.AreEqual(TimeSpan.FromSeconds(1500), environment.Config.AgentHealthMaxAge);
    }

    [TestMethod]
    public void ConfiguredHostIdDoesNotResolveFallback()
    {
        var fallbackCalls = 0;
        var hostId = HostIdentity.SelectConfiguredOrDefault(
            "configured.example.org",
            () =>
            {
                fallbackCalls++;
                return "fallback.example.org";
            });

        Assert.AreEqual("configured.example.org", hostId);
        Assert.AreEqual(0, fallbackCalls);
    }

    [TestMethod]
    public void MissingHostIdResolvesFallbackOnce()
    {
        var fallbackCalls = 0;
        var hostId = HostIdentity.SelectConfiguredOrDefault(
            null,
            () =>
            {
                fallbackCalls++;
                return "fallback.example.org";
            });

        Assert.AreEqual("fallback.example.org", hostId);
        Assert.AreEqual(1, fallbackCalls);
    }

    [TestMethod]
    public void ExtraCliArgumentsFailClosed()
        => TestAssert.Throws<AgentException>(
            () => AgentActionParser.ParseArgs(new[] { ACTION_STATUS, "--unexpected" }));

}
