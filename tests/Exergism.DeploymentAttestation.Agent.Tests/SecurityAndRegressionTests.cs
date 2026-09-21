using System.Text;
using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class SecurityAndRegressionTests
{
    [TestMethod]
    public void ResolverCommandLineMustBindConfiguredSource()
    {
        const string APP_DIR = "/srv/id.exergism.org";
        var registry = Path.Combine(APP_DIR, RESOLVER_REGISTRY_RELATIVE_PATH);

        static byte[] CommandLine(params string[] arguments)
            => Encoding.UTF8.GetBytes(string.Join('\0', arguments) + "\0");

        Assert.IsTrue(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                "-listen", "127.0.0.1:8080",
                RESOLVER_ARG_ROOT, APP_DIR,
                RESOLVER_ARG_REGISTRY, registry),
            APP_DIR));

        Assert.IsTrue(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                $"{RESOLVER_ARG_ROOT}={APP_DIR}",
                $"{RESOLVER_ARG_REGISTRY}={registry}"),
            APP_DIR));

        Assert.IsTrue(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                $"--root={APP_DIR}",
                "--registry", registry),
            APP_DIR));

        Assert.IsFalse(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                RESOLVER_ARG_ROOT, "/srv/unrelated",
                RESOLVER_ARG_REGISTRY, registry),
            APP_DIR));

        Assert.IsFalse(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                RESOLVER_ARG_ROOT, APP_DIR,
                RESOLVER_ARG_ROOT, APP_DIR,
                RESOLVER_ARG_REGISTRY, registry),
            APP_DIR));

        Assert.IsFalse(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                RESOLVER_ARG_ROOT, APP_DIR,
                $"--root=/srv/unrelated",
                RESOLVER_ARG_REGISTRY, registry),
            APP_DIR));

        Assert.IsFalse(RuntimeInspector.CommandLineUsesConfiguredSource(
            CommandLine(
                "/usr/local/bin/idresolver",
                RESOLVER_ARG_ROOT, APP_DIR),
            APP_DIR));
    }

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
    public void ResampledQuiescenceRejectsRestartedOrMovedService()
    {
        const string CONTROL_GROUP = "/system.slice/id-exergism.service";

        Assert.IsTrue(ServiceQuiescence.ResampledSnapshotRemainsQuiescent(
            CONTROL_GROUP,
            CONTROL_GROUP,
            SYSTEMD_STATE_LOADED,
            SYSTEMD_STATE_INACTIVE,
            "0"));

        Assert.IsFalse(ServiceQuiescence.ResampledSnapshotRemainsQuiescent(
            CONTROL_GROUP,
            CONTROL_GROUP,
            SYSTEMD_STATE_LOADED,
            SYSTEMD_STATE_ACTIVE,
            "1234"));

        Assert.IsFalse(ServiceQuiescence.ResampledSnapshotRemainsQuiescent(
            CONTROL_GROUP,
            "/system.slice/restarted.service",
            SYSTEMD_STATE_LOADED,
            SYSTEMD_STATE_INACTIVE,
            "0"));
    }

    [TestMethod]
    public void PathExistsNoFollowDetectsFilesAndDanglingSymlinks()
    {
        using var environment = TestEnvironment.Create();
        var regular = Path.Combine(environment.Root, "phase-file");
        var dangling = Path.Combine(environment.Root, "phase-link");
        var missingTarget = Path.Combine(environment.Root, "missing-target");
        var absent = Path.Combine(environment.Root, "absent");

        File.WriteAllText(regular, "x");
        File.CreateSymbolicLink(dangling, missingTarget);

        Assert.IsTrue(Durability.PathExistsNoFollow(regular));
        Assert.IsTrue(Durability.PathExistsNoFollow(dangling));
        Assert.IsFalse(Durability.PathExistsNoFollow(absent));
    }

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
    public void GitTreePathsAreConfinedToRepository()
    {
        using var environment = TestEnvironment.Create();
        var repository = Path.Combine(environment.Root, "tree-root");
        Directory.CreateDirectory(repository);

        Assert.AreEqual(
            Path.Combine(repository, "nested", "file.txt"),
            GitTreePath.Resolve(repository, "nested/file.txt"));

        foreach (var invalid in new[]
                 {
                     "",
                     ".",
                     "..",
                     "../escape",
                     "nested/../escape",
                     "nested/./file",
                     "/absolute",
                     ".git/config",
                     "nested/.git/config",
                     "nested//file"
                 })
        {
            TestAssert.Throws<AgentException>(
                () => GitTreePath.Resolve(repository, invalid));
        }
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
    public void EnsureDirectoryRejectsSymlinkAncestor()
    {
        using var environment = TestEnvironment.Create();
        var real = Path.Combine(environment.Root, "real");
        var link = Path.Combine(environment.Root, "link");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        TestAssert.Throws<AgentException>(() =>
            Durability.EnsureDirectory(
                Path.Combine(link, "state"),
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute));
    }

    [TestMethod]
    public void EnsureDirectoryDoesNotChmodExistingSharedDirectory()
    {
        using var environment = TestEnvironment.Create();
        var shared = Path.Combine(environment.Root, "shared");
        Directory.CreateDirectory(shared);
        var original =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute;
        File.SetUnixFileMode(shared, original);

        TestAssert.Throws<AgentException>(() =>
            Durability.EnsureDirectory(
                shared,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute));

        Assert.AreEqual(original, File.GetUnixFileMode(shared));
    }

    [TestMethod]
    public void RequiredDirectoryFsyncRejectsSymlink()
    {
        using var environment = TestEnvironment.Create();
        var real = Path.Combine(environment.Root, "real-dir");
        var link = Path.Combine(environment.Root, "dir-link");
        Directory.CreateDirectory(real);
        Directory.CreateSymbolicLink(link, real);

        TestAssert.Throws<AgentException>(() =>
            Durability.FsyncRequiredDirectory(link, "symlinked directory"));
    }

    [TestMethod]
    public void GitMetadataFsyncRejectsMutationAfterSnapshot()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        Directory.CreateDirectory(metadata);
        var head = Path.Combine(metadata, "HEAD");
        File.WriteAllText(head, "ref: refs/heads/main\n");

        var snapshot = GitMetadataDurability.Capture(metadata);
        File.WriteAllText(head, "ref: refs/heads/other\n");

        TestAssert.Throws<AgentException>(
            () => GitMetadataDurability.Fsync(metadata, snapshot));
    }

    [TestMethod]
    public void GitMetadataFsyncRejectsRestoredBytesWithChangedMetadata()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        Directory.CreateDirectory(metadata);
        var head = Path.Combine(metadata, "HEAD");
        const string EXPECTED = "ref: refs/heads/main\n";
        File.WriteAllText(head, EXPECTED);

        var snapshot = GitMetadataDurability.Capture(metadata);
        File.WriteAllText(head, "ref: refs/heads/evil\n");
        File.WriteAllText(head, EXPECTED);

        TestAssert.Throws<AgentException>(
            () => GitMetadataDurability.Fsync(metadata, snapshot));
    }

    [TestMethod]
    public void MetadataFsyncRejectsSymlink()
    {
        using var environment = TestEnvironment.Create();
        var real = Path.Combine(environment.Root, "real-metadata");
        var link = Path.Combine(environment.Root, "metadata-link");
        File.WriteAllText(real, "metadata");
        File.CreateSymbolicLink(link, real);

        TestAssert.Throws<AgentException>(
            () => Durability.FsyncRegularFileNoFollow(
                link,
                "symlinked metadata"));
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

        TestAssert.Throws<AgentException>(
            () => GitMetadataBoundary.EnsureRepositoryRoots(
                submodule,
                new[] { topMetadata },
                new[] { topMetadata },
                isTopLevel: false));

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
    public async Task RecoveryIncludesNestedOldFormMetadataRequiredByRollbackTarget()
    {
        using var environment = TestEnvironment.Create();
        var checkout = environment.Config.AppDirectory;
        var grandSource = Path.Combine(environment.Root, "grand-source-rollback");
        var childSource = Path.Combine(environment.Root, "child-source-rollback");
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

        foreach (var repositoryPath in new[] { grandSource, childSource, checkout })
        {
            await GitAsync(repositoryPath, "init");
            await GitAsync(repositoryPath, "config", "user.name", "Regression Test");
            await GitAsync(repositoryPath, "config", "user.email", "regression@example.test");
        }

        await GitAsync(grandSource, "commit", "--allow-empty", "-m", "grand");
        await GitAsync(
            childSource,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            grandSource,
            "grand");
        await GitAsync(childSource, "commit", "-am", "child-old");

        await GitAsync(
            checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "add",
            childSource,
            "child");
        await GitAsync(checkout, "commit", "-am", "top-old");
        await GitAsync(
            checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "update",
            "--init",
            "--recursive");

        var topOld = await GitAsync(checkout, "rev-parse", "HEAD");
        var child = Path.Combine(checkout, "child");
        await GitAsync(child, "config", "user.name", "Regression Test");
        await GitAsync(child, "config", "user.email", "regression@example.test");
        var grand = Path.Combine(child, "grand");
        var modernGrandGitDir = Path.GetFullPath(await GitAsync(
            grand,
            "rev-parse",
            "--path-format=absolute",
            "--git-dir"));
        var oldGrandGitDir = Path.Combine(grand, GIT_METADATA_NAME);
        File.Delete(oldGrandGitDir);
        Directory.Move(modernGrandGitDir, oldGrandGitDir);
        var unsetWorktree = await ProcessRunner.RunAsync(
            "git",
            new[] { "config", "--file", Path.Combine(oldGrandGitDir, "config"), "--unset", "core.worktree" },
            workingDirectory: environment.Root);
        Assert.IsTrue(
            unsetWorktree.Success || unsetWorktree.ExitCode == 5,
            unsetWorktree.StdErr);

        await GitAsync(child, "rm", "--cached", "-f", "grand");
        await GitAsync(child, "commit", "-m", "child-current-without-grand");
        await GitAsync(checkout, "add", "child");
        await GitAsync(checkout, "commit", "-m", "top-current");
        var topCurrent = await GitAsync(checkout, "rev-parse", "HEAD");

        var deploymentRepository = new GitRepository(environment.Config);
        var currentOnly = await deploymentRepository.RecoveryGitMetadataRootsAsync(topCurrent);
        Assert.IsFalse(
            currentOnly.Contains(
                Path.GetFullPath(oldGrandGitDir),
                StringComparer.Ordinal));

        var withRollback = await deploymentRepository.RecoveryGitMetadataRootsAsync(
            topCurrent,
            topOld);
        Assert.IsTrue(
            withRollback.Contains(
                Path.GetFullPath(oldGrandGitDir),
                StringComparer.Ordinal));
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
            workingDirectory: environment.Root);
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
    public async Task PinnedMetadataRootCannotBeRedirectedByReplacementSymlink()
    {
        using var environment = TestEnvironment.Create();
        var metadata = Path.Combine(environment.Root, "metadata");
        var displaced = Path.Combine(environment.Root, "metadata-original");
        var external = Path.Combine(environment.Root, "external");
        Directory.CreateDirectory(metadata);
        Directory.CreateDirectory(external);

        var internalLock = Path.Combine(metadata, "index.lock");
        var externalLock = Path.Combine(external, "unrelated.lock");
        File.WriteAllText(internalLock, "inside");
        File.WriteAllText(externalLock, "outside");

        using var pinned = PinnedGitMetadataRoot.Open(metadata);
        Directory.Move(metadata, displaced);
        Directory.CreateSymbolicLink(metadata, external);

        await pinned.CleanupLocksAsync(
            () => Task.CompletedTask,
            _ => Task.FromResult(false));

        Assert.IsFalse(File.Exists(Path.Combine(displaced, "index.lock")));
        Assert.IsTrue(File.Exists(externalLock));
        Assert.AreEqual("outside", File.ReadAllText(externalLock));
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


    [TestMethod]
    public void SubmoduleGitfileFsyncRejectsRestoredMutationAfterSnapshot()
    {
        using var environment = TestEnvironment.Create();
        var marker = Path.Combine(environment.Root, "submodule.git");
        const string ORIGINAL = "gitdir: ../.git/modules/child\n";

        File.WriteAllText(marker, ORIGINAL);
        var verified = new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        SubmoduleGitMarkerDurability.Capture(marker, "submodule gitfile", verified);

        Thread.Sleep(5);
        File.WriteAllText(marker, "gitdir: /tmp/intermediate\n");
        File.WriteAllText(marker, ORIGINAL);

        TestAssert.Throws<AgentException>(
            () => SubmoduleGitMarkerDurability.Fsync(
                marker,
                "submodule gitfile",
                verified));
    }

    [TestMethod]
    public void SubmoduleGitfileSnapshotRejectsFileToDirectoryReplacement()
    {
        using var environment = TestEnvironment.Create();
        var marker = Path.Combine(environment.Root, "submodule.git");

        File.WriteAllText(marker, "gitdir: ../.git/modules/child\n");
        var verified = new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        SubmoduleGitMarkerDurability.Capture(marker, "submodule gitfile", verified);

        File.Delete(marker);
        Directory.CreateDirectory(marker);

        TestAssert.Throws<AgentException>(
            () => SubmoduleGitMarkerDurability.Fsync(
                marker,
                "submodule gitfile",
                verified));
    }

    [TestMethod]
    public async Task FinalAttestationSourceValidationRechecksBytesAtStableHead()
    {
        var commit = new string('a', 40);
        var calls = 0;

        var exact = await AttestationSourceValidation.VerifyFinalExactAsync(
            commit,
            commit,
            _ =>
            {
                calls++;
                throw new AgentException("tracked bytes changed");
            });

        Assert.IsFalse(exact);
        Assert.AreEqual(1, calls);

        var skipped = await AttestationSourceValidation.VerifyFinalExactAsync(
            new string('b', 40),
            commit,
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            });

        Assert.IsFalse(skipped);
        Assert.AreEqual(1, calls);

        var healthy = await AttestationSourceValidation.VerifyFinalExactAsync(
            commit,
            commit,
            _ => Task.CompletedTask);
        Assert.IsTrue(healthy);
    }


    [TestMethod]
    public void ExactTreeSnapshotRejectsTrackedMutationAfterTraversal()
    {
        using var environment = TestEnvironment.Create();
        var root = Path.Combine(environment.Root, "snapshot-root");
        Directory.CreateDirectory(root);
        var tracked = Path.Combine(root, "tracked.txt");
        File.WriteAllText(tracked, "before");

        var snapshot = new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal)
            {
                [Path.GetFullPath(tracked)] =
                    Durability.ReadRegularFileNoFollow(tracked, tracked).Snapshot
            },
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal)
            {
                [Path.GetFullPath(root)] =
                    Durability.ReadDirectorySnapshotNoFollow(root, root)
            });

        File.WriteAllText(tracked, "after");

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureSnapshotObjectsCurrent(snapshot));
    }

    [TestMethod]
    public void ExactTreeSnapshotRejectsFileSetGrowthAfterTraversal()
    {
        using var environment = TestEnvironment.Create();
        var root = Path.Combine(environment.Root, "fileset-root");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "tracked.txt"), "tracked");

        var snapshot = new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        snapshot.FileSets[Path.GetFullPath(root)] = new RepositoryFileSetSnapshot(
            new HashSet<string>(StringComparer.Ordinal) { "tracked.txt" },
            new HashSet<string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));

        File.WriteAllText(Path.Combine(root, "late-untracked.txt"), "late");

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureSnapshotFileSetsCurrent(snapshot));
    }

    [TestMethod]
    public void ExactTreeSnapshotRejectsSymlinkRetargetAfterTraversal()
    {
        using var environment = TestEnvironment.Create();
        var root = Path.Combine(environment.Root, "symlink-root");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "tracked-link");
        File.CreateSymbolicLink(link, "first-target");

        var snapshot = new RepositoryDurabilitySnapshot(
            new Dictionary<string, FileSnapshot>(StringComparer.Ordinal),
            new Dictionary<string, DirectorySnapshot>(StringComparer.Ordinal));
        snapshot.Symlinks[Path.GetFullPath(link)] = "first-target";

        File.Delete(link);
        File.CreateSymbolicLink(link, "second-target");

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureSnapshotObjectsCurrent(snapshot));
    }


    [TestMethod]
    public void ExactTreeRejectsUntrackedEmptyDirectory()
    {
        var expectedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            "tracked.txt"
        };
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var actual = new RepositoryDiskTreeSnapshot(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "tracked.txt"
            },
            new HashSet<string>(StringComparer.Ordinal)
            {
                "late-empty-directory"
            });

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureDiskTreeMatches(
                "/checkout",
                expectedFiles,
                expectedDirectories,
                actual));
    }

    [TestMethod]
    public void CheckoutMetadataBarrierRejectsMutationAfterInitialSnapshot()
    {
        using var environment = TestEnvironment.Create();
        var metadataRoot = Path.Combine(environment.Root, "git-metadata");
        Directory.CreateDirectory(metadataRoot);
        var head = Path.Combine(metadataRoot, "HEAD");
        File.WriteAllText(head, "ref: refs/heads/main\n");

        var snapshots = GitRepository.CaptureGitMetadataSnapshots(
            new[] { metadataRoot });

        File.WriteAllText(head, "ref: refs/heads/intermediate\n");

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureGitMetadataSnapshotsUnchanged(snapshots));
    }


    [TestMethod]
    public void GitMetadataSnapshotRejectsSymlinkedLooseRef()
    {
        using var environment = TestEnvironment.Create();
        var metadataRoot = Path.Combine(environment.Root, "metadata-symlink");
        var refs = Path.Combine(metadataRoot, "refs", "heads");
        Directory.CreateDirectory(refs);

        var external = Path.Combine(environment.Root, "external-ref");
        File.WriteAllText(external, new string('a', 40) + "\n");
        File.CreateSymbolicLink(Path.Combine(refs, "main"), external);

        TestAssert.Throws<AgentException>(
            () => GitMetadataDurability.Capture(metadataRoot));
    }

    [TestMethod]
    public void CheckoutWriteExclusionDetectsWritableProcFdModes()
    {
        Assert.IsFalse(
            CheckoutWriteExclusion.IsWritableDescriptor("flags:\t0100000"));
        Assert.IsTrue(
            CheckoutWriteExclusion.IsWritableDescriptor("flags:\t0100001"));
        Assert.IsTrue(
            CheckoutWriteExclusion.IsWritableDescriptor("flags:\t0100002"));
    }

    [TestMethod]
    public void CheckoutWriteExclusionRejectsMalformedProcFdModes()
    {
        TestAssert.Throws<AgentException>(
            () => CheckoutWriteExclusion.IsWritableDescriptor("flags:\tnot-octal"));
    }


    [TestMethod]
    public async Task CheckoutSealRejectsMultiplyLinkedRegularFile()
    {
        using var environment = TestEnvironment.Create();
        var original = Path.Combine(environment.Root, "tracked-hardlink-source");
        var alias = Path.Combine(environment.Root, "external-hardlink-alias");
        File.WriteAllText(original, "verified");

        var link = await ProcessRunner.RunAsync(
            "ln",
            new[] { original, alias },
            TimeSpan.FromSeconds(10));
        Assert.IsTrue(link.Success, link.StdErr);

        TestAssert.Throws<AgentException>(
            () => CheckoutWriteExclusion.EnsureSingleLinkNoFollow(original));
    }

    [TestMethod]
    public void CheckoutSealStateDetectsPartialCrashState()
    {
        Assert.AreEqual(
            CheckoutSealState.Unsealed,
            CheckoutWriteExclusion.ClassifySealState(new[] { false, false, false }));
        Assert.AreEqual(
            CheckoutSealState.FullySealed,
            CheckoutWriteExclusion.ClassifySealState(new[] { true, true, true }));
        Assert.AreEqual(
            CheckoutSealState.PartiallySealed,
            CheckoutWriteExclusion.ClassifySealState(new[] { true, false, true }));
    }

    [TestMethod]
    public void CheckoutSealRejectsUnexpectedLinkCountBeforeMutation()
    {
        TestAssert.Throws<AgentException>(
            () => CheckoutWriteExclusion.EnsureSingleLinkCount(
                2,
                "/checkout/tracked.txt"));
    }


    [TestMethod]
    public void MetadataBindingRejectsRepositoryRootSubstitution()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "/checkout/.git/modules/one"
        };
        var actual = new HashSet<string>(StringComparer.Ordinal)
        {
            "/checkout/.git/modules/two"
        };

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureMetadataBindingUnchanged(
                "/checkout/submodule",
                expected,
                actual));
    }


    [TestMethod]
    public void CheckoutSealRejectsPathIdentitySubstitution()
    {
        var opened = new FileIdentity(8, 1, 100);
        var named = new FileIdentity(8, 1, 101);

        TestAssert.Throws<AgentException>(
            () => CheckoutWriteExclusion.EnsureSameIdentity(
                opened,
                named,
                "/checkout/tracked.txt"));
    }


    [TestMethod]
    public void AgentGitEnvironmentDropsAmbientGitSelectors()
    {
        var environment = AgentGitEnvironment.Create();

        Assert.AreEqual("1", environment[AgentConstants.GIT_ENV_CONFIG_NOSYSTEM]);
        Assert.AreEqual("/dev/null", environment[AgentConstants.GIT_ENV_CONFIG_GLOBAL]);
        Assert.AreEqual("0", environment[AgentConstants.GIT_ENV_TERMINAL_PROMPT]);
        Assert.AreEqual("0", environment[AgentConstants.GIT_ENV_OPTIONAL_LOCKS]);

        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_DIR));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_WORK_TREE));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_COMMON_DIR));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_INDEX_FILE));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_OBJECT_DIRECTORY));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_ALTERNATE_OBJECT_DIRECTORIES));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_CONFIG_PARAMETERS));
        Assert.IsFalse(environment.ContainsKey(AgentConstants.GIT_ENV_CONFIG_COUNT));
    }


    [TestMethod]
    public void AgentGitInvocationInjectsNonExecutablePolicy()
    {
        var args = AgentGitInvocation.BuildArguments(
            "/checkout",
            new[] { "status" });

        var joined = string.Join("\n", args);
        StringAssert.Contains(joined, "core.hooksPath=/dev/null");
        StringAssert.Contains(joined, "core.fsmonitor=false");
        StringAssert.Contains(joined, "core.attributesFile=/dev/null");
        StringAssert.Contains(joined, "credential.helper=");
        StringAssert.Contains(joined, "protocol.allow=never");
        StringAssert.Contains(joined, "protocol.https.allow=always");
        StringAssert.Contains(joined, "protocol.ext.allow=never");
        StringAssert.Contains(joined, "protocol.file.allow=never");
        StringAssert.Contains(joined, "protocol.ssh.allow=never");
    }

    [TestMethod]
    public void AgentGitInvocationRejectsExecutableFilterKeys()
    {
        Assert.IsTrue(
            AgentGitInvocation.IsExecutableFilterConfigKey("filter.demo.clean"));
        Assert.IsTrue(
            AgentGitInvocation.IsExecutableFilterConfigKey("filter.demo.smudge"));
        Assert.IsTrue(
            AgentGitInvocation.IsExecutableFilterConfigKey("filter.demo.process"));
        Assert.IsFalse(
            AgentGitInvocation.IsExecutableFilterConfigKey("filter.demo.required"));
        Assert.IsFalse(
            AgentGitInvocation.IsExecutableFilterConfigKey("core.hooksPath"));
    }


    [TestMethod]
    public void GitConfigPolicyRejectsExecutableFiltersAndIncludes()
    {
        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureNoUnsafeRepositoryConfigKeys(
                "/checkout/.git/config",
                "filter.demo.process\nremote.origin.url\n"));

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureNoUnsafeRepositoryConfigKeys(
                "/checkout/.git/config",
                "include.path\nremote.origin.url\n"));

        TestAssert.Throws<AgentException>(
            () => GitRepository.EnsureNoUnsafeRepositoryConfigKeys(
                "/checkout/.git/config",
                "includeif.gitdir:/tmp/other.path\nremote.origin.url\n"));

        GitRepository.EnsureNoUnsafeRepositoryConfigKeys(
            "/checkout/.git/config",
            "remote.origin.url\ncore.repositoryformatversion\n");
    }


    [TestMethod]
    public void GitMetadataConfigPathIgnoresRefsNamedConfig()
    {
        Assert.IsTrue(
            GitRepository.IsGitMetadataConfigPath(
                "/checkout/.git",
                "/checkout/.git/config"));
        Assert.IsTrue(
            GitRepository.IsGitMetadataConfigPath(
                "/checkout/.git",
                "/checkout/.git/modules/child/config"));
        Assert.IsTrue(
            GitRepository.IsGitMetadataConfigPath(
                "/checkout/.git",
                "/checkout/.git/worktrees/site/config.worktree"));
        Assert.IsFalse(
            GitRepository.IsGitMetadataConfigPath(
                "/checkout/.git",
                "/checkout/.git/refs/heads/config"));
    }


    [TestMethod]
    public void TrustedStateDirectoryRejectsWrongOwner()
    {
        TestAssert.Throws<AgentException>(
            () => Durability.EnsureTrustedDirectoryAttributes(
                ownerUid: 1000,
                mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                effectiveUid: 0,
                path: "/var/lib/ec-deployment-attestation"));
    }

    [TestMethod]
    public void TrustedStateDirectoryRejectsGroupOrOtherWrite()
    {
        TestAssert.Throws<AgentException>(
            () => Durability.EnsureTrustedDirectoryAttributes(
                ownerUid: 0,
                mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                      UnixFileMode.GroupWrite,
                effectiveUid: 0,
                path: "/var/lib/ec-deployment-attestation"));

        Durability.EnsureTrustedDirectoryAttributes(
            ownerUid: 0,
            mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            effectiveUid: 0,
            path: "/var/lib/ec-deployment-attestation");
    }


    [TestMethod]
    public void GitConfigPolicyRejectsAlternateRefsAndCommandDrivers()
    {
        foreach (var key in new[]
                 {
                     "core.alternateRefsCommand",
                     "core.sshCommand",
                     "core.gitProxy",
                     "core.sharedRepository",
                     "diff.external",
                     "diff.demo.textconv",
                     "merge.demo.driver",
                     "submodule.child.update"
                 })
        {
            Assert.IsTrue(
                AgentGitInvocation.IsUnsafeRepositoryConfigKey(key),
                $"Expected unsafe Git config key: {key}");
            TestAssert.Throws<AgentException>(
                () => GitRepository.EnsureNoUnsafeRepositoryConfigKeys(
                    "/checkout/.git/config",
                    key + "\n"));
        }
    }

    [TestMethod]
    public void CheckoutMutationTrustRejectsGroupWritableFile()
    {
        using var environment = TestEnvironment.Create();
        var root = Path.Combine(environment.Root, "trusted-mutation-tree");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "config");
        File.WriteAllText(file, "safe");
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            file,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite);

        TestAssert.Throws<AgentException>(
            () => CheckoutWriteExclusion.EnsureMutationTreeTrusted(root));
    }

    [TestMethod]
    public void CheckoutMutationTrustAcceptsOwnerOnlyTree()
    {
        using var environment = TestEnvironment.Create();
        var root = Path.Combine(environment.Root, "owner-only-mutation-tree");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "config");
        File.WriteAllText(file, "safe");
        File.SetUnixFileMode(
            root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(
            file,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        CheckoutWriteExclusion.EnsureMutationTreeTrusted(root);
    }


    [TestMethod]
    public void TrustedRegularFileBytesRequireExecutableWhenRequested()
    {
        using var environment = TestEnvironment.Create();
        var script = Path.Combine(environment.Root, "smoke-script");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        TestAssert.Throws<AgentException>(
            () => Durability.ReadTrustedRegularFileBytes(
                script,
                "smoke script",
                requireExecutable: true));

        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute);

        var bytes = Durability.ReadTrustedRegularFileBytes(
            script,
            "smoke script",
            requireExecutable: true);
        CollectionAssert.AreEqual(
            System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\nexit 0\n"),
            bytes);
    }

    [TestMethod]
    public void TrustedSmokeSourceRejectsWorldWritableParentAttributes()
    {
        TestAssert.Throws<AgentException>(
            () => Durability.EnsureTrustedDirectoryAttributes(
                ownerUid: Native.geteuid(),
                mode: UnixFileMode.UserRead |
                      UnixFileMode.UserWrite |
                      UnixFileMode.UserExecute |
                      UnixFileMode.OtherWrite |
                      UnixFileMode.OtherExecute,
                effectiveUid: Native.geteuid(),
                path: "/untrusted-smoke-parent"));
    }


    [TestMethod]
    public void SmokeSnapshotStagingAvoidsRunNoexecMount()
    {
        Assert.IsFalse(
            SmokeScriptValidation.TRUSTED_RUNTIME_DIRECTORY.StartsWith(
                "/run/",
                StringComparison.Ordinal));
        Assert.AreEqual(
            "/usr/local/libexec/ec-deployment-attestation-smoke",
            SmokeScriptValidation.TRUSTED_RUNTIME_DIRECTORY);
    }

    [TestMethod]
    public void ArtifactFenceMountAuditAdoptsTargetRoot()
    {
        var args = RuntimeInspector.BuildMountInfoNsenterArguments(1234);

        CollectionAssert.AreEqual(
            new[]
            {
                "--target", "1234",
                "--mount",
                "--root",
                "--",
                "cat", "/proc/self/mountinfo"
            },
            args);

        TestAssert.Throws<AgentException>(
            () => RuntimeInspector.BuildMountInfoNsenterArguments(0));
    }

    [TestMethod]
    public void ArtifactFenceMountContainmentHandlesFilesystemRoot()
    {
        Assert.IsTrue(RuntimeInspector.Contains("/", "/"));
        Assert.IsTrue(RuntimeInspector.Contains("/", "/srv/id.exergism.org"));
        Assert.IsTrue(RuntimeInspector.Contains("/srv", "/srv/id.exergism.org"));
        Assert.IsTrue(RuntimeInspector.Contains("/srv/id.exergism.org", "/srv/id.exergism.org"));

        Assert.IsFalse(RuntimeInspector.Contains("/srv/id.exergism.org", "/srv/id.exergism.org-evil"));
        Assert.IsFalse(RuntimeInspector.Contains("/srv/id.exergism.org", "/srv/id.exergism"));
    }

}
