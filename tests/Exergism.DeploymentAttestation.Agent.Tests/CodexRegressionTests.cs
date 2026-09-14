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
    public async Task UntrackedGitfileCannotRedirectStaleLockCleanup()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var init = await ProcessRunner.RunAsync(
            "git",
            new[] { "init" },
            workingDirectory: checkout);
        Assert.IsTrue(init.Success, init.StdErr);

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
