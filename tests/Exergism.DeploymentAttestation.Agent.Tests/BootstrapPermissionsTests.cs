using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class BootstrapPermissionsTests
{
    [TestMethod]
    [TestCategory("PrivilegedLinux")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BootstrapArtifactsMigrateLegacyModesWithoutRecordedState(bool groupReadable)
    {
        using var fixture = await BootstrapFixture.CreateAsync(groupReadable);
        using var getHttp = new HttpClient();
        using var attestationHttp = new HttpClient();
        var agent = new DeploymentAgent(
            fixture.Environment.Config,
            getHttp,
            attestationHttp,
            new AgentHealthStore(fixture.Environment.Config));

        Assert.IsFalse(File.Exists(fixture.Environment.Config.CurrentStateFile));
        var binary = await agent.PrepareBootstrapArtifactsAsync(fixture.Commit);

        Assert.AreEqual(fixture.BinarySha256, binary);
        Assert.AreEqual(
            PublishedWorktreePermissions.REGULAR_FILE_MODE,
            File.GetUnixFileMode(fixture.Registry));
        Assert.AreEqual(
            PublishedWorktreePermissions.EXECUTABLE_FILE_MODE,
            File.GetUnixFileMode(fixture.Executable));
        foreach (var directory in fixture.PublishedDirectories)
            Assert.AreEqual(
                PublishedWorktreePermissions.DIRECTORY_MODE,
                File.GetUnixFileMode(directory));
        Assert.AreEqual(
            CheckoutSealState.FullySealed,
            CheckoutWriteExclusion.InspectTreeSealState(fixture.Environment.Config.AppDirectory));
        await new GitRepository(fixture.Environment.Config)
            .VerifySourceTreeExactAsync(fixture.Commit);
        Assert.IsFalse(File.Exists(fixture.Environment.Config.CurrentStateFile),
            "Preparing artifacts must not publish state before final live-runtime verification.");
    }

    [TestMethod]
    [TestCategory("PrivilegedLinux")]
    [DataRow("source")]
    [DataRow("runtime")]
    [DataRow("revision")]
    public async Task BootstrapRejectsInvalidBaselineBeforeNormalizingPermissions(string invalidArtifact)
    {
        using var fixture = await BootstrapFixture.CreateAsync(groupReadable: false);
        using var getHttp = new HttpClient();
        using var attestationHttp = new HttpClient();
        var agent = new DeploymentAgent(
            fixture.Environment.Config,
            getHttp,
            attestationHttp,
            new AgentHealthStore(fixture.Environment.Config));

        var commit = fixture.Commit;
        switch (invalidArtifact)
        {
            case "source":
                File.WriteAllText(fixture.Registry, "{\"tampered\":true}");
                break;
            case "runtime":
                File.SetUnixFileMode(
                    fixture.Environment.Config.AppBinary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
                break;
            case "revision":
                commit = new string('0', 40);
                break;
        }

        await TestAssert.ThrowsAsync<AgentException>(
            () => agent.PrepareBootstrapArtifactsAsync(commit));

        Assert.AreEqual(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(fixture.Registry));
        foreach (var directory in fixture.PublishedDirectories)
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(directory));
        Assert.IsFalse(File.Exists(fixture.Environment.Config.CurrentStateFile));
    }

    [TestMethod]
    [TestCategory("PrivilegedLinux")]
    public async Task MissingTargetIsFetchedBeforeTargetTraversalAndFetchFailureCanRollback()
    {
        using var fixture = await BootstrapFixture.CreateAsync(groupReadable: false);
        var repository = new GitRepository(fixture.Environment.Config);
        var configure = await ProcessRunner.RunAsync("git",
            ["remote", "add", "origin", "/unavailable-test-origin.git"],
            workingDirectory: fixture.Environment.Config.AppDirectory);
        Assert.IsTrue(configure.Success, configure.StdErr);
        await repository.NormalizeVerifiedPublishedPermissionsAsync(fixture.Commit);
        var unavailableTarget = new string('1', 40);
        var error = await Assert.ThrowsExactlyAsync<AgentException>(() =>
            repository.SwitchSourceAsync(unavailableTarget, fetchFirst: true));
        StringAssert.Contains(error.Message, "transport 'file' not allowed",
            "The target must reach the restricted fetch instead of premature ls-tree.");
        await repository.SwitchSourceAsync(fixture.Commit, fetchFirst: false);
        Assert.AreEqual(fixture.Commit, await repository.HeadAsync());
        await repository.VerifySourceTreeExactAsync(fixture.Commit);
        Assert.AreEqual(CheckoutSealState.FullySealed,
            CheckoutWriteExclusion.InspectTreeSealState(fixture.Environment.Config.AppDirectory));
    }

    private sealed class BootstrapFixture : IDisposable
    {
        private BootstrapFixture(
            TestEnvironment environment,
            string commit,
            string registry,
            string executable,
            string[] publishedDirectories)
        {
            Environment = environment;
            Commit = commit;
            Registry = registry;
            Executable = executable;
            PublishedDirectories = publishedDirectories;
            BinarySha256 = Durability.Sha256(environment.Config.AppBinary);
        }

        internal TestEnvironment Environment { get; }
        internal string Commit { get; }
        internal string Registry { get; }
        internal string Executable { get; }
        internal string[] PublishedDirectories { get; }
        internal string BinarySha256 { get; }

        internal static async Task<BootstrapFixture> CreateAsync(bool groupReadable)
        {
            var environment = TestEnvironment.Create();
            try
            {
                var checkout = environment.Config.AppDirectory;
                async Task<string> GitAsync(params string[] arguments)
                {
                    var result = await ProcessRunner.RunAsync(
                        "git", arguments, workingDirectory: checkout);
                    Assert.IsTrue(result.Success, result.StdErr);
                    return result.StdOut.Trim();
                }

                await GitAsync("init");
                await GitAsync("config", "user.name", "Bootstrap Regression");
                await GitAsync("config", "user.email", "bootstrap@example.test");
                var resolverDirectory = Path.Combine(checkout, "resolver");
                var deployDirectory = Path.Combine(checkout, "deploy");
                Directory.CreateDirectory(resolverDirectory);
                Directory.CreateDirectory(deployDirectory);
                var registry = Path.Combine(resolverDirectory, "registry.json");
                var executable = Path.Combine(deployDirectory, "setup.sh");
                File.WriteAllText(registry, "{}");
                File.WriteAllText(executable, "#!/bin/sh\n");
                File.SetUnixFileMode(executable, PublishedWorktreePermissions.EXECUTABLE_FILE_MODE);
                await GitAsync("add", ".");
                await GitAsync("commit", "-m", "bootstrap fixture");
                var commit = await GitAsync("rev-parse", "HEAD");

                var fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                var directoryMode = fileMode | UnixFileMode.UserExecute;
                if (groupReadable)
                {
                    fileMode |= UnixFileMode.GroupRead;
                    directoryMode |= UnixFileMode.GroupRead | UnixFileMode.GroupExecute;
                }
                File.SetUnixFileMode(registry, fileMode);
                File.SetUnixFileMode(executable, directoryMode);
                string[] directories = [checkout, resolverDirectory, deployDirectory];
                foreach (var directory in directories)
                    File.SetUnixFileMode(directory, directoryMode);

                File.Copy("/bin/true", environment.Config.AppBinary);
                File.SetUnixFileMode(
                    environment.Config.AppBinary,
                    PublishedWorktreePermissions.EXECUTABLE_FILE_MODE);
                return new BootstrapFixture(environment, commit, registry, executable, directories);
            }
            catch
            {
                environment.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                CheckoutWriteExclusion.SetTreeImmutable(
                    Environment.Config.AppDirectory, immutable: false);
            }
            finally
            {
                Environment.Dispose();
            }
        }
    }
}
