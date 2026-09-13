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
    public async Task ProbeExceptionsBecomeFailedChecks()
    {
        var result = await HealthCheckRunner.RunAsync(
            () => Task.FromException<bool>(new TimeoutException("systemd timeout")));
        Assert.IsFalse(result);
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

}
