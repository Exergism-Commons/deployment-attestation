using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class CodexRegressionTests
{
    [TestMethod]
    public void InactiveServiceWithRemovedCgroupIsQuiescent()
        => Assert.IsTrue(ServiceQuiescence.IsQuiescentSnapshot(
            SYSTEMD_STATE_LOADED, SYSTEMD_STATE_INACTIVE, "0", false, false));

    [TestMethod]
    public void FailedServiceWithEmptyExistingCgroupIsQuiescent()
        => Assert.IsTrue(ServiceQuiescence.IsQuiescentSnapshot(
            SYSTEMD_STATE_LOADED, SYSTEMD_STATE_FAILED, "0", true, false));

    [TestMethod]
    public void ExistingCgroupWithProcessesIsNotQuiescent()
        => Assert.IsFalse(ServiceQuiescence.IsQuiescentSnapshot(
            SYSTEMD_STATE_LOADED, SYSTEMD_STATE_INACTIVE, "0", true, true));

    [TestMethod]
    public void NestedCgroupTraversalFailureFailsClosedWhileRootStillExists()
    {
        Assert.IsFalse(ServiceQuiescence.TraversalFailureIsQuiescent(rootExists: true));
        Assert.IsTrue(ServiceQuiescence.TraversalFailureIsQuiescent(rootExists: false));
    }

    [TestMethod]
    public void SignedAttestationClientRejectsRedirectsWhileGetClientRetainsThem()
    {
        using var getHandler = HttpClientFactory.CreateGetHandler();
        using var attestationHandler = HttpClientFactory.CreateAttestationHandler();

        Assert.IsTrue(getHandler.AllowAutoRedirect);
        Assert.IsFalse(attestationHandler.AllowAutoRedirect);
    }

    [TestMethod]
    public async Task ProbeExceptionsBecomeFailedChecks()
    {
        var result = await HealthCheckRunner.RunAsync(
            () => Task.FromException<bool>(new TimeoutException("systemd timeout")));
        Assert.IsFalse(result);
    }

    [TestMethod]
    public void DeletedProcExeStillIdentifiesGit()
    {
        Assert.IsTrue(GitProcessIdentity.IsGitExecutable("git (deleted)"));
        Assert.IsTrue(GitProcessIdentity.IsGitExecutable("git-remote-https (deleted)"));
        Assert.IsFalse(GitProcessIdentity.IsGitExecutable("not-git (deleted)"));
    }

    [TestMethod]
    public void GitInitPositionalTargetIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "init", protectedRoot };
        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(argv, value => value == protectedRoot));
    }

    [TestMethod]
    public void GitCloneDestinationAfterSubcommandIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "clone", "https://example.invalid/repo.git", protectedRoot };
        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(argv, value => value == protectedRoot));
    }

    [TestMethod]
    public void SeparateGitDirEqualsPathIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "init", $"--separate-git-dir={protectedRoot}", "/tmp/work" };

        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(
            argv,
            value => value == protectedRoot));
    }

    [TestMethod]
    public void SeparateGitDirSeparateValueIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "init", "--separate-git-dir", protectedRoot, "/tmp/work" };

        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(
            argv,
            value => value == protectedRoot));
    }

    [TestMethod]
    public void EmptyEqualsPathSelectorFailsClosed()
        => TestAssert.Throws<AgentException>(
            () => GitProcessArguments.ReferencesProtectedPath(
                new[] { "git", "init", "--separate-git-dir=", "/tmp/work" },
                _ => false));

    [TestMethod]
    public void CheckoutIndexPrefixEqualsPathIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org/";
        var argv = new[] { "git", "checkout-index", "-a", $"--prefix={protectedRoot}" };

        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(
            argv,
            value => value == protectedRoot));
    }

    [TestMethod]
    public void ArbitraryEqualsOptionPathIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "future-command", $"--future-output={protectedRoot}" };

        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(
            argv,
            value => value == protectedRoot));
    }

    [TestMethod]
    public void CoreWorktreeConfigIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "-c", $"core.worktree={protectedRoot}", "status" };
        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(argv, value => value == protectedRoot));
    }

    [TestMethod]
    public void ConfigEnvCoreWorktreeIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var argv = new[] { "git", "--config-env=core.worktree=WORKTREE", "status" };
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WORKTREE"] = protectedRoot
        };

        Assert.IsTrue(GitProcessArguments.ReferencesProtectedPath(
            argv,
            value => value == protectedRoot,
            name => environment.TryGetValue(name, out var value) ? value : null));
    }

    [TestMethod]
    public void GitConfigCountCoreWorktreeIsDetected()
    {
        const string protectedRoot = "/srv/id.exergism.org";
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GIT_ENV_CONFIG_COUNT] = "1",
            [$"{GIT_ENV_CONFIG_KEY_PREFIX}0"] = GIT_CONFIG_CORE_WORKTREE,
            [$"{GIT_ENV_CONFIG_VALUE_PREFIX}0"] = protectedRoot
        };

        Assert.IsTrue(GitProcessEnvironment.ReferencesProtectedWorktree(
            environment,
            value => value == protectedRoot));
    }

    [TestMethod]
    public void MalformedGitConfigCountFailsClosed()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [GIT_ENV_CONFIG_COUNT] = "not-a-number"
        };

        TestAssert.Throws<AgentException>(
            () => GitProcessEnvironment.ReferencesProtectedWorktree(environment, _ => false));
    }


    [TestMethod]
    public async Task RollbackAttestationRunsBeforeDeploymentFailureIsPropagated()
    {
        var order = new List<string>();

        await TestAssert.ThrowsAsync<AgentException>(
            () => DeploymentFailureFlow.ThrowAfterRollbackAndReportAsync(
                () =>
                {
                    order.Add("rollback");
                    return Task.CompletedTask;
                },
                () =>
                {
                    order.Add("attest");
                    return Task.CompletedTask;
                },
                "activation failed",
                new InvalidOperationException("candidate")));

        CollectionAssert.AreEqual(new[] { "rollback", "attest" }, order);
    }

    [TestMethod]
    public void CanonicalFqdnWinsOverShortHostname()
    {
        Assert.AreEqual(
            "node.example.org",
            HostIdentity.SelectDefault("node.example.org\n", "node"));
        Assert.AreEqual(
            "node",
            HostIdentity.SelectDefault("   ", "node"));
    }


    [TestMethod]
    public void CommittedJournalCleanupFailureIsPropagatedWithoutRollbackSemantics()
    {
        var deleteCalls = 0;

        TestAssert.Throws<AgentException>(
            () => CommittedTransactionJournal.Cleanup(() =>
            {
                deleteCalls++;
                throw new IOException("fsync failed");
            }));

        Assert.AreEqual(1, deleteCalls);
    }

    [TestMethod]
    public void MissingTrackedRegularFileFailsVerification()
    {
        using var environment = TestEnvironment.Create();
        var missing = Path.Combine(environment.Root, "missing-tracked-file");

        TestAssert.Throws<AgentException>(
            () => TrackedFileDurability.ReadVerifiedRegularFile(missing, "missing-tracked-file"));
    }

    [TestMethod]
    public void RegularTrackedFilePassesIdentityBoundFsyncBarrier()
    {
        using var environment = TestEnvironment.Create();
        var tracked = Path.Combine(environment.Root, "tracked");
        File.WriteAllText(tracked, "content");

        var verified = TrackedFileDurability.ReadVerifiedRegularFile(tracked, "tracked");
        TrackedFileDurability.FsyncRegularFile(tracked, "tracked", verified.Identity);
    }

    [TestMethod]
    public void RegularFileSubstitutionFailsIdentityBoundFsyncBarrier()
    {
        using var environment = TestEnvironment.Create();
        var tracked = Path.Combine(environment.Root, "tracked");
        var original = Path.Combine(environment.Root, "original");
        File.WriteAllText(tracked, "expected");

        var verified = TrackedFileDurability.ReadVerifiedRegularFile(tracked, "tracked");
        File.Move(tracked, original);
        File.WriteAllText(tracked, "substitute");

        TestAssert.Throws<AgentException>(
            () => TrackedFileDurability.FsyncRegularFile(
                tracked,
                "tracked",
                verified.Identity));
    }

    [TestMethod]
    public void DirectorySubstitutionFailsTrackedRegularFsyncBarrier()
    {
        using var environment = TestEnvironment.Create();
        var directory = Path.Combine(environment.Root, "tracked");
        Directory.CreateDirectory(directory);

        TestAssert.Throws<AgentException>(
            () => TrackedFileDurability.ReadVerifiedRegularFile(directory, "tracked"));
    }

    [TestMethod]
    public void SymlinkSubstitutionFailsTrackedRegularFsyncBarrier()
    {
        using var environment = TestEnvironment.Create();
        var target = Path.Combine(environment.Root, "target");
        var link = Path.Combine(environment.Root, "tracked");
        File.WriteAllText(target, "content");
        File.CreateSymbolicLink(link, target);

        TestAssert.Throws<AgentException>(
            () => TrackedFileDurability.ReadVerifiedRegularFile(link, "tracked"));
    }

    [TestMethod]
    public void RequiredDirectoryFsyncRejectsMissingDirectory()
    {
        using var environment = TestEnvironment.Create();
        var missing = Path.Combine(environment.Root, "missing-directory");

        TestAssert.Throws<AgentException>(
            () => Durability.FsyncRequiredDirectory(missing, "missing-directory"));
    }

    [TestMethod]
    public void RequiredDirectoryFsyncAcceptsExistingDirectory()
    {
        using var environment = TestEnvironment.Create();
        var directory = Path.Combine(environment.Root, "required-directory");
        Directory.CreateDirectory(directory);

        Durability.FsyncRequiredDirectory(directory, "required-directory");
    }

    [TestMethod]
    public void BinaryHashIoFailureBecomesMissingProbeValue()
    {
        var digest = HealthCheckRunner.Try<string>(
            () => throw new IOException("binary disappeared"));

        Assert.IsNull(digest);
    }

}
