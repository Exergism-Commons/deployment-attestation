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

    [TestMethod]
    public void InstallerAdmissionHasAnExplicitArgumentShape()
    {
        Assert.AreEqual(AgentAction.SelfUpdate,
            AgentActionParser.ParseArgs(["self-update", "--admit", "v0.1.10"]));
        Assert.ThrowsExactly<AgentException>(() =>
            AgentActionParser.ParseArgs(["self-update", "--unexpected", "v0.1.10"]));
    }

    [TestMethod]
    [TestCategory("PrivilegedLinux")]
    public async Task ExecutionUsesVerifiedSnapshotAfterSourceReplacement()
    {
        var root = CreatePrivateFixture();
        try
        {
            var helper = System.IO.Path.Combine(root, "installed.py");
            File.WriteAllText(helper, "print('verified-generation')");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var snapshot = PackageSelfUpdate.CreateHelperSnapshot(helper, System.IO.Path.Combine(root, "snapshots"));
            File.Delete(helper);
            File.WriteAllText(helper, "print('replacement-generation')");
            var result = await ProcessRunner.RunAsync(
                "/usr/bin/python3", ["-I", snapshot.Path], TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, result.ExitCode);
            Assert.AreEqual("verified-generation", result.StdOut.Trim());
            Assert.AreEqual(UnixFileMode.UserRead, File.GetUnixFileMode(snapshot.Path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("PrivilegedLinux")]
    public void WritableOrLinkedHelperParentIsRejected()
    {
        var root = CreatePrivateFixture();
        try
        {
            var parent = System.IO.Path.Combine(root, "untrusted");
            Directory.CreateDirectory(parent);
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
            var helper = System.IO.Path.Combine(parent, "helper.py");
            File.WriteAllText(helper, "print('untrusted')");
            File.SetUnixFileMode(helper, UnixFileMode.UserRead);
            var staging = System.IO.Path.Combine(root, "snapshots");
            Assert.ThrowsExactly<AgentException>(() => PackageSelfUpdate.CreateHelperSnapshot(helper, staging));
            File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var link = System.IO.Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(link, parent);
            Assert.ThrowsExactly<AgentException>(() =>
                PackageSelfUpdate.CreateHelperSnapshot(System.IO.Path.Combine(link, "helper.py"), staging));
            Assert.IsFalse(Directory.Exists(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreatePrivateFixture()
    {
        var parent = Environment.GetEnvironmentVariable("EC_SELF_UPDATE_TEST_ROOT") ?? "/root";
        var root = System.IO.Path.Combine(parent, "helper-snapshot-" + Guid.NewGuid().ToString("N"));
        Durability.EnsureTrustedDirectory(root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }
}

