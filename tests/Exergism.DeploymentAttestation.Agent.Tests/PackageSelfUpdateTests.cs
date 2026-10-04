using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class PackageSelfUpdateTests
{
    [TestMethod]
    [DataRow("self-update")]
    [DataRow("package-version")]
    public void PackageCommandsAreSeparateActions(string command)
    {
        var action = command == "self-update" ? AgentAction.SelfUpdate : AgentAction.PackageVersion;
        Assert.AreEqual(action, AgentActionParser.ParseArgs([command]));
        Assert.AreEqual(command, AgentActionParser.ToWireValue(action));
    }

    [TestMethod]
    public void PackageVersionComesFromVersionFileAndIsReportedInHealth()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../VERSION"));
        var expected = File.ReadAllText(path).Trim();
        Assert.AreEqual(expected, PackageSelfUpdate.Version);
        Assert.AreEqual(expected, AgentConstants.AGENT_VERSION);
    }
}

