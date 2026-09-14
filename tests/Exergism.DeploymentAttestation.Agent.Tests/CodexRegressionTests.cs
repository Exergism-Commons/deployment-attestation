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
    public async Task LinkedWorktreeCommonMetadataIsDetectedAsProtected()
    {
        using var environment = TestEnvironment.Create();
        var repository = Path.Combine(environment.Root, "repository");
        var linkedWorktree = Path.Combine(environment.Root, "linked-worktree");
        Directory.CreateDirectory(repository);

        async Task<string> GitAsync(string workingDirectory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                args,
                workingDirectory: workingDirectory);
            Assert.IsTrue(result.Success, result.StdErr);
            return result.StdOut.Trim();
        }

        await GitAsync(repository, "init");
        await GitAsync(repository, "config", "user.name", "Regression Test");
        await GitAsync(repository, "config", "user.email", "regression@example.test");
        await GitAsync(repository, "commit", "--allow-empty", "-m", "initial");
        await GitAsync(repository, "worktree", "add", "-b", "linked", linkedWorktree);

        var protectedMetadata = Path.GetFullPath(Path.Combine(repository, GIT_METADATA_NAME));
        var protectedRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            protectedMetadata
        };

        var topLevel = await GitAsync(
            linkedWorktree,
            "rev-parse",
            "--path-format=absolute",
            "--show-toplevel");
        var gitDir = await GitAsync(
            linkedWorktree,
            "rev-parse",
            "--path-format=absolute",
            "--git-dir");
        var commonDir = await GitAsync(
            linkedWorktree,
            "rev-parse",
            "--path-format=absolute",
            "--git-common-dir");

        Assert.IsFalse(
            GitEffectivePathProbe.ReferencesProtected(
                new[] { topLevel },
                protectedRoots));
        Assert.IsTrue(
            GitEffectivePathProbe.ReferencesProtected(
                new[] { gitDir },
                protectedRoots));
        Assert.IsTrue(
            GitEffectivePathProbe.ReferencesProtected(
                new[] { commonDir },
                protectedRoots));
        Assert.AreEqual(protectedMetadata, Path.GetFullPath(commonDir));
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
        TrackedFileDurability.FsyncRegularFile(tracked, "tracked", verified.Snapshot);
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
                verified.Snapshot));
    }

    [TestMethod]
    public void SameInodeContentMutationFailsSnapshotBoundFsyncBarrier()
    {
        using var environment = TestEnvironment.Create();
        var tracked = Path.Combine(environment.Root, "tracked");
        File.WriteAllText(tracked, "expected");

        var verified = TrackedFileDurability.ReadVerifiedRegularFile(tracked, "tracked");
        File.WriteAllText(tracked, "changed!");

        TestAssert.Throws<AgentException>(
            () => TrackedFileDurability.FsyncRegularFile(
                tracked,
                "tracked",
                verified.Snapshot));
    }

    [TestMethod]
    public void RestoredSameInodeBytesStillChangeSnapshot()
    {
        using var environment = TestEnvironment.Create();
        var tracked = Path.Combine(environment.Root, "tracked");
        File.WriteAllText(tracked, "expected");

        var before = TrackedFileDurability.ReadVerifiedRegularFile(tracked, "tracked");
        File.WriteAllText(tracked, "changed!");
        File.WriteAllText(tracked, "expected");
        var after = TrackedFileDurability.ReadVerifiedRegularFile(tracked, "tracked");

        CollectionAssert.AreEqual(before.Data, after.Data);
        Assert.AreNotEqual(before.Snapshot, after.Snapshot);
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
    public void GitMetadataBoundaryRejectsExternalAndSymlinkedRoots()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        var nested = Path.Combine(metadata, "modules", "child");
        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(external);

        GitMetadataBoundary.EnsureRootsWithin(
            new[] { nested },
            new[] { metadata });

        TestAssert.Throws<AgentException>(
            () => GitMetadataBoundary.EnsureRootsWithin(
                new[] { external },
                new[] { metadata }));

        var realModules = Path.Combine(environment.Root, "real-modules");
        Directory.CreateDirectory(realModules);
        var symlinkModules = Path.Combine(metadata, "linked-modules");
        Directory.CreateSymbolicLink(symlinkModules, realModules);
        var escapedViaSymlink = Path.Combine(symlinkModules, "child");
        Directory.CreateDirectory(Path.Combine(realModules, "child"));

        TestAssert.Throws<AgentException>(
            () => GitMetadataBoundary.EnsureRootsWithin(
                new[] { escapedViaSymlink },
                new[] { metadata }));
    }

    [TestMethod]
    public void VerifiedSubmoduleMetadataAllowsModernAndOldLayoutsOnly()
    {
        using var environment = TestEnvironment.Create();
        var top = Path.Combine(environment.Root, "top");
        var topMetadata = Path.Combine(top, GIT_METADATA_NAME);
        Directory.CreateDirectory(topMetadata);

        GitMetadataBoundary.EnsureRepositoryRoots(
            top,
            new[] { topMetadata },
            parentMetadataHierarchy: null,
            isTopLevel: true);

        var submodule = Path.Combine(top, "submodule");
        Directory.CreateDirectory(submodule);

        var modernMetadata = Path.Combine(topMetadata, "modules", "submodule");
        Directory.CreateDirectory(modernMetadata);
        GitMetadataBoundary.EnsureRepositoryRoots(
            submodule,
            new[] { modernMetadata },
            new[] { topMetadata },
            isTopLevel: false);

        var oldMetadata = Path.Combine(submodule, GIT_METADATA_NAME);
        Directory.CreateDirectory(oldMetadata);
        GitMetadataBoundary.EnsureRepositoryRoots(
            submodule,
            new[] { oldMetadata },
            new[] { topMetadata },
            isTopLevel: false);

        var externalMetadata = Path.Combine(environment.Root, "external", GIT_METADATA_NAME);
        Directory.CreateDirectory(externalMetadata);
        TestAssert.Throws<AgentException>(
            () => GitMetadataBoundary.EnsureRepositoryRoots(
                submodule,
                new[] { externalMetadata },
                new[] { topMetadata },
                isTopLevel: false));
    }

    [TestMethod]
    public void TopLevelRepositoryRejectsExternalGitMetadata()
    {
        using var environment = TestEnvironment.Create();
        var checkout = Path.Combine(environment.Root, "checkout");
        var embedded = Path.Combine(checkout, GIT_METADATA_NAME);
        var external = Path.Combine(environment.Root, "external-metadata");
        Directory.CreateDirectory(embedded);
        Directory.CreateDirectory(external);

        GitMetadataBoundary.EnsureRepositoryRoots(
            checkout,
            new[] { embedded },
            parentMetadataHierarchy: null,
            isTopLevel: true);

        TestAssert.Throws<AgentException>(
            () => GitMetadataBoundary.EnsureRepositoryRoots(
                checkout,
                new[] { external },
                parentMetadataHierarchy: null,
                isTopLevel: true));
    }

    [TestMethod]
    public async Task RecoveryDoesNotFollowUnsafeGitlinkRemnants()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var childSource = Path.Combine(environment.Root, "child-source");
        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(childSource);
        Directory.CreateDirectory(external);

        async Task<string> GitAsync(string workingDirectory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                args,
                workingDirectory: workingDirectory);
            Assert.IsTrue(result.Success, result.StdErr);
            return result.StdOut.Trim();
        }

        foreach (var repositoryPath in new[] { childSource, external, checkout })
        {
            await GitAsync(repositoryPath, "init");
            await GitAsync(repositoryPath, "config", "user.name", "Regression Test");
            await GitAsync(repositoryPath, "config", "user.email", "regression@example.test");
            await GitAsync(repositoryPath, "commit", "--allow-empty", "-m", "initial");
        }

        foreach (var name in new[] { "child-path", "child-marker" })
        {
            await GitAsync(
                checkout,
                "-c",
                "protocol.file.allow=always",
                "submodule",
                "add",
                childSource,
                name);
        }
        await GitAsync(checkout, "commit", "-am", "children");

        var symlinkedChild = Path.Combine(checkout, "child-path");
        Directory.Delete(symlinkedChild, recursive: true);
        Directory.CreateSymbolicLink(symlinkedChild, external);

        var markerChild = Path.Combine(checkout, "child-marker");
        var marker = Path.Combine(markerChild, GIT_METADATA_NAME);
        Assert.IsTrue(File.Exists(marker));
        File.Delete(marker);
        Directory.CreateSymbolicLink(marker, Path.Combine(external, GIT_METADATA_NAME));

        var deploymentRepository = new GitRepository(environment.Config);
        var roots = await deploymentRepository.RecoveryGitMetadataRootsAsync(
            await deploymentRepository.HeadAsync());

        var externalRoot = Path.GetFullPath(external);
        Assert.IsFalse(
            roots.Any(root =>
                GitMetadataBoundary.IsSameOrDescendant(root, externalRoot)));
    }

    [TestMethod]
    public async Task RecoveryTraversesNestedOldFormSubmoduleFromCurrentCheckout()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var grandSource = Path.Combine(environment.Root, "grand-source");
        var childSource = Path.Combine(environment.Root, "child-source");
        Directory.CreateDirectory(grandSource);
        Directory.CreateDirectory(childSource);

        async Task<string> GitAsync(string workingDirectory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                args,
                workingDirectory: workingDirectory);
            Assert.IsTrue(result.Success, result.StdErr);
            return result.StdOut.Trim();
        }

        foreach (var repository in new[] { grandSource, childSource, checkout })
        {
            await GitAsync(repository, "init");
            await GitAsync(repository, "config", "user.name", "Regression Test");
            await GitAsync(repository, "config", "user.email", "regression@example.test");
        }

        File.WriteAllText(Path.Combine(grandSource, "grand.txt"), "grand");
        await GitAsync(grandSource, "add", "grand.txt");
        await GitAsync(grandSource, "commit", "-m", "grand");

        await GitAsync(
            childSource,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            grandSource,
            "grand");
        await GitAsync(childSource, "commit", "-am", "child-with-grand");

        await GitAsync(
            checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            childSource,
            "child");
        await GitAsync(checkout, "commit", "-am", "top-with-child");
        await GitAsync(
            checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "update",
            "--init",
            "--recursive");

        var grand = Path.Combine(checkout, "child", "grand");
        var modernGrandGitDir = Path.GetFullPath(await GitAsync(
            grand,
            "rev-parse",
            "--path-format=absolute",
            "--git-dir"));
        var oldGrandGitDir = Path.Combine(grand, GIT_METADATA_NAME);
        Assert.IsTrue(File.Exists(oldGrandGitDir));
        File.Delete(oldGrandGitDir);
        Directory.Move(modernGrandGitDir, oldGrandGitDir);

        var unsetWorktree = await ProcessRunner.RunAsync(
            "git",
            new[] { "config", "--file", Path.Combine(oldGrandGitDir, "config"), "--unset", "core.worktree" },
            workingDirectory: grand);
        Assert.IsTrue(
            unsetWorktree.Success || unsetWorktree.ExitCode == 5,
            unsetWorktree.StdErr);

        Assert.AreEqual(
            Path.GetFullPath(oldGrandGitDir),
            Path.GetFullPath(await GitAsync(
                grand,
                "rev-parse",
                "--path-format=absolute",
                "--git-dir")));

        var missingChildCommit = new string('b', 40);
        await GitAsync(
            checkout,
            "update-index",
            "--cacheinfo",
            $"160000,{missingChildCommit},child");
        await GitAsync(checkout, "commit", "-m", "top-points-to-unfetched-child");

        var missingProbe = await ProcessRunner.RunAsync(
            "git",
            new[] { "cat-file", "-e", $"{missingChildCommit}^{{commit}}" },
            workingDirectory: Path.Combine(checkout, "child"));
        Assert.IsFalse(missingProbe.Success);

        var deploymentRepository = new GitRepository(environment.Config);
        var roots = await deploymentRepository.RecoveryGitMetadataRootsAsync(
            await deploymentRepository.HeadAsync());

        Assert.IsTrue(
            roots.Contains(
                Path.GetFullPath(oldGrandGitDir),
                StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task RecoveryToleratesUnfetchedVerifiedSubmoduleCommit()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var childSource = Path.Combine(environment.Root, "child-source");
        Directory.CreateDirectory(childSource);

        async Task GitAsync(string workingDirectory, params string[] args)
        {
            var result = await ProcessRunner.RunAsync(
                "git",
                args,
                workingDirectory: workingDirectory);
            Assert.IsTrue(result.Success, result.StdErr);
        }

        await GitAsync(childSource, "init");
        await GitAsync(childSource, "config", "user.name", "Regression Test");
        await GitAsync(childSource, "config", "user.email", "regression@example.test");
        File.WriteAllText(Path.Combine(childSource, "tracked.txt"), "old");
        await GitAsync(childSource, "add", "tracked.txt");
        await GitAsync(childSource, "commit", "-m", "child-old");

        await GitAsync(checkout, "init");
        await GitAsync(checkout, "config", "user.name", "Regression Test");
        await GitAsync(checkout, "config", "user.email", "regression@example.test");
        await GitAsync(
            checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            childSource,
            "child");
        await GitAsync(checkout, "commit", "-am", "super-old");

        var missingCommit = new string('a', 40);
        await GitAsync(
            checkout,
            "update-index",
            "--cacheinfo",
            $"160000,{missingCommit},child");
        await GitAsync(checkout, "commit", "-m", "super-new-unfetched-child");

        var child = Path.Combine(checkout, "child");
        var missingProbe = await ProcessRunner.RunAsync(
            "git",
            new[] { "cat-file", "-e", $"{missingCommit}^{{commit}}" },
            workingDirectory: child);
        Assert.IsFalse(missingProbe.Success);

        var childGitDir = await ProcessRunner.RunAsync(
            "git",
            new[] { "rev-parse", "--path-format=absolute", "--git-dir" },
            workingDirectory: child);
        Assert.IsTrue(childGitDir.Success, childGitDir.StdErr);
        Assert.IsTrue(Directory.Exists(Path.GetFullPath(childGitDir.StdOut.Trim())));

        var repository = new GitRepository(environment.Config);
        await repository.ReconcileStaleGitLocksAsync();
    }

    [TestMethod]
    public async Task UntrackedGitfileCannotRedirectStaleLockCleanup()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var init = await ProcessRunner.RunAsync(
            "git",
            new[] { "init" },
            workingDirectory: checkout);
        Assert.IsTrue(init.Success, init.StdErr);

        foreach (var config in new[]
                 {
                     new[] { "config", "user.name", "Regression Test" },
                     new[] { "config", "user.email", "regression@example.test" }
                 })
        {
            var configured = await ProcessRunner.RunAsync(
                "git",
                config,
                workingDirectory: checkout);
            Assert.IsTrue(configured.Success, configured.StdErr);
        }

        var commit = await ProcessRunner.RunAsync(
            "git",
            new[] { "commit", "--allow-empty", "-m", "fixture" },
            workingDirectory: checkout);
        Assert.IsTrue(commit.Success, commit.StdErr);

        var externalRepository = Path.Combine(environment.Root, "external-repository");
        var externalMetadata = Path.Combine(externalRepository, GIT_METADATA_NAME);
        Directory.CreateDirectory(externalMetadata);
        var externalLock = Path.Combine(externalMetadata, "unrelated.lock");
        File.WriteAllText(externalLock, "must-survive");

        var nested = Path.Combine(checkout, "untracked");
        Directory.CreateDirectory(nested);
        File.WriteAllText(
            Path.Combine(nested, GIT_METADATA_NAME),
            $"gitdir: {externalMetadata}\n");

        var repository = new GitRepository(environment.Config);
        await repository.ReconcileStaleGitLocksAsync();

        Assert.IsTrue(File.Exists(externalLock));
        Assert.AreEqual("must-survive", File.ReadAllText(externalLock));
    }

    [TestMethod]
    public void StaleLockEnumerationDoesNotFollowMetadataSymlinks()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(metadata);
        Directory.CreateDirectory(external);

        var internalLock = Path.Combine(metadata, "index.lock");
        var externalLock = Path.Combine(external, "unrelated.lock");
        File.WriteAllText(internalLock, "inside");
        File.WriteAllText(externalLock, "outside");
        Directory.CreateSymbolicLink(Path.Combine(metadata, "escape"), external);

        var locks = GitMetadataEnumeration.EnumerateLockFiles(metadata)
            .Select(Path.GetFullPath)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { Path.GetFullPath(internalLock) },
            locks);
        Assert.IsFalse(locks.Contains(Path.GetFullPath(externalLock), StringComparer.Ordinal));
    }

    [TestMethod]
    public void GitMetadataTreeEnumerationDoesNotFollowSymlinkDirectories()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(metadata);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "external-file"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(metadata, "escape"), external);

        Assert.IsFalse(
            GitMetadataEnumeration.EnumerateFiles(metadata)
                .Any(path => path.EndsWith("external-file", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void DirectoryChurnChangesSnapshotEvenWhenEntryIsRemoved()
    {
        using var environment = TestEnvironment.Create();
        var directory = Path.Combine(environment.Root, "checkout");
        Directory.CreateDirectory(directory);

        var before = Durability.ReadDirectorySnapshotNoFollow(directory, "checkout");
        var transient = Path.Combine(directory, "transient");
        File.WriteAllText(transient, "temporary");
        File.Delete(transient);
        var after = Durability.ReadDirectorySnapshotNoFollow(directory, "checkout");

        Assert.AreNotEqual(before, after);
        TestAssert.Throws<AgentException>(
            () => Durability.FsyncDirectorySnapshotNoFollow(
                directory,
                "checkout",
                before));
    }

    [TestMethod]
    public void VerifiedRuntimeDescriptorFsyncChecksDigestAndMode()
    {
        using var environment = TestEnvironment.Create();
        var runtime = Path.Combine(environment.Root, "runtime");
        File.WriteAllText(runtime, "runtime-content");
        File.SetUnixFileMode(
            runtime,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var expected = Durability.Sha256(runtime);
        var snapshot = Durability.VerifyAndFsyncRegularFileNoFollow(
            runtime,
            "runtime",
            expected,
            requireExecutable: true);

        Assert.AreEqual(expected, snapshot.Sha256);

        TestAssert.Throws<AgentException>(
            () => Durability.VerifyAndFsyncRegularFileNoFollow(
                runtime,
                "runtime",
                new string('0', 64),
                requireExecutable: true));
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
