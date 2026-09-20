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
    public void HttpClientsDoNotImposeFrameworkDefaultTimeout()
    {
        using var get = HttpClientFactory.CreateGet();
        using var attestation = HttpClientFactory.CreateAttestation();

        Assert.AreEqual(Timeout.InfiniteTimeSpan, get.Timeout);
        Assert.AreEqual(Timeout.InfiniteTimeSpan, attestation.Timeout);
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
    public void SingleQuotedConfigRejectsBashConcatenation()
    {
        Assert.AreEqual(
            "/srv/id resolver",
            AgentConfig.ParseValue("'/srv/id resolver'"));

        TestAssert.Throws<AgentException>(
            () => AgentConfig.ParseValue("'/srv/id'/'resolver'"));
        TestAssert.Throws<AgentException>(
            () => AgentConfig.ParseValue("'prefix'\''suffix'"));
    }

    [TestMethod]
    public void DoubleQuotedConfigMatchesBashBackslashSemantics()
    {
        Assert.AreEqual(
            @"/srv/id\nresolver",
            AgentConfig.ParseValue("\"/srv/id\\nresolver\""));
        Assert.AreEqual(
            @"/srv/id\qresolver",
            AgentConfig.ParseValue("\"/srv/id\\qresolver\""));
        Assert.AreEqual(
            @"/srv/id\resolver",
            AgentConfig.ParseValue("\"/srv/id\\\\resolver\""));
        Assert.AreEqual(
            "/srv/id\"resolver",
            AgentConfig.ParseValue("\"/srv/id\\\"resolver\""));

        TestAssert.Throws<AgentException>(
            () => AgentConfig.ParseValue("\"$HOME/runtime\""));
    }

    [TestMethod]
    public void StaticConfigParserRejectsUnquotedShellEscapesAndQuoting()
    {
        foreach (var value in new[]
                 {
                     @"/srv/id\ resolver",
                     "/srv/'id resolver'",
                     "/srv/\"id resolver\"",
                     "/srv/id resolver",
                     "/srv/id;echo"
                 })
        {
            TestAssert.Throws<AgentException>(() => AgentConfig.ParseValue(value));
        }
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
    public void RepositoryIdentifierMatchesReleaseManifestContract()
    {
        foreach (var valid in new[]
                 {
                     "Exergism-Commons/deployment-attestation",
                     "owner/repo",
                     "owner.name/repo_name-1"
                 })
        {
            Assert.AreEqual(
                valid,
                AgentConfig.RequireRepositoryIdentifier(ENV_REPOSITORY, valid));
            Assert.IsTrue(Protocol.IsValidRepositoryIdentifier(valid));
        }

        foreach (var invalid in new[]
                 {
                     "owner/repo/extra",
                     "owner",
                     "/repo",
                     "owner/",
                     "owner repo/name",
                     "owner/repo@tag"
                 })
        {
            TestAssert.Throws<AgentException>(
                () => AgentConfig.RequireRepositoryIdentifier(
                    ENV_REPOSITORY,
                    invalid));
            Assert.IsFalse(Protocol.IsValidRepositoryIdentifier(invalid));
        }
    }

    [TestMethod]
    public void DownloadBaseMustUseHttps()
    {
        Assert.AreEqual(
            Uri.UriSchemeHttps,
            AgentConfig.RequireHttpsUri(
                ENV_GITHUB_DOWNLOAD_BASE,
                "https://github.com/owner/repo/releases/download/tag/").Scheme);

        foreach (var invalid in new[]
                 {
                     "http://mirror.example.test/releases/",
                     "file:///tmp/releases/",
                     "ftp://mirror.example.test/releases/",
                     "/relative/releases/"
                 })
        {
            TestAssert.Throws<AgentException>(
                () => AgentConfig.RequireHttpsUri(
                    ENV_GITHUB_DOWNLOAD_BASE,
                    invalid));
        }
    }

    [TestMethod]
    public void HealthAndAttestationTransportBoundariesFailClosed()
    {
        Assert.IsTrue(
            AgentConfig.RequireLoopbackHttpUri(
                ENV_LOCAL_URL,
                "http://127.0.0.1:8080/").IsLoopback);
        Assert.IsTrue(
            AgentConfig.RequireLoopbackHttpUri(
                ENV_LOCAL_URL,
                "http://[::1]:8080/").IsLoopback);
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireLoopbackHttpUri(
                ENV_LOCAL_URL,
                "https://example.test/health"));

        Assert.AreEqual(
            Uri.UriSchemeHttps,
            AgentConfig.RequireHttpsUri(
                ENV_PUBLIC_URL,
                "https://id.exergism.org/").Scheme);
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireHttpsUri(
                ENV_PUBLIC_URL,
                "http://id.exergism.org/"));

        Assert.AreEqual(
            "https://health.example.test/v1/attest",
            AgentConfig.RequireOptionalAttestationUri(
                ENV_ATTESTATION_ENDPOINT,
                "https://health.example.test/v1/attest"));
        Assert.AreEqual(
            "http://127.0.0.1:8081/attest",
            AgentConfig.RequireOptionalAttestationUri(
                ENV_ATTESTATION_ENDPOINT,
                "http://127.0.0.1:8081/attest"));
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireOptionalAttestationUri(
                ENV_ATTESTATION_ENDPOINT,
                "http://health.example.test/v1/attest"));
    }

    [TestMethod]
    public void TrustedConfigReaderRejectsSymlinksAndWritableFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ec-config-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var config = Path.Combine(root, "service.env");
            File.WriteAllText(config, "EC_SERVICE=id.exergism.org\n");
            File.SetUnixFileMode(
                config,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            Assert.AreEqual(
                "EC_SERVICE=id.exergism.org\n",
                Durability.ReadTrustedConfigText(config));

            File.SetUnixFileMode(
                config,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            TestAssert.Throws<AgentException>(
                () => Durability.ReadTrustedConfigText(config));

            File.SetUnixFileMode(
                config,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var link = Path.Combine(root, "service-link.env");
            File.CreateSymbolicLink(link, config);
            TestAssert.Throws<AgentException>(
                () => Durability.ReadTrustedConfigText(link));

            TestAssert.Throws<AgentException>(
                () => Durability.ReadTrustedConfigText("relative.env"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void SystemdShowUsesSupportedPropertyArgumentForm()
    {
        CollectionAssert.AreEqual(
            new[] { "show", "id-exergism.service", "--property", SYSTEMD_ACTIVE_STATE, "--value" },
            SystemdController.BuildShowArguments("id-exergism.service", SYSTEMD_ACTIVE_STATE));
    }

    [TestMethod]
    public void ReceiverConfigurationRequiresUsableHmacSecret()
    {
        using var environment = TestEnvironment.Create();
        var endpoint = "https://health.example.test/v1/attest";
        var secret = Path.Combine(environment.Root, "receiver-secret");
        File.WriteAllText(secret, "secret-value\n");

        Assert.AreEqual(
            Path.GetFullPath(secret),
            AgentConfig.RequireAttestationSecret(endpoint, secret));

        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireAttestationSecret(endpoint, string.Empty));
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireAttestationSecret(endpoint, "relative-secret"));

        File.WriteAllText(secret, " \t\r\n");
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireAttestationSecret(endpoint, secret));

        File.WriteAllText(secret, "secret-value");
        var symlink = Path.Combine(environment.Root, "secret-link");
        File.CreateSymbolicLink(symlink, secret);
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireAttestationSecret(endpoint, symlink));

        Assert.AreEqual(
            string.Empty,
            AgentConfig.RequireAttestationSecret(string.Empty, string.Empty));
    }

    [TestMethod]
    public void PublicCheckBooleanFailsClosedOnTypos()
    {
        Assert.IsTrue(AgentConfig.RequireBoolean(ENV_CHECK_PUBLIC, "1"));
        Assert.IsFalse(AgentConfig.RequireBoolean(ENV_CHECK_PUBLIC, "0"));

        foreach (var invalid in new[] { "", "true", "false", "yes", "2", "01" })
            TestAssert.Throws<AgentException>(
                () => AgentConfig.RequireBoolean(ENV_CHECK_PUBLIC, invalid));
    }

    [TestMethod]
    public void SmokeScriptConfigRequiresOnlyAbsolutePathWhileRuntimeRequiresTrustedExecutable()
    {
        using var environment = TestEnvironment.Create();
        var missing = Path.Combine(environment.Root, "not-installed-yet.sh");

        Assert.AreEqual(
            Path.GetFullPath(missing),
            AgentConfig.RequireOptionalAbsolutePath(ENV_SMOKE_SCRIPT, missing));
        Assert.AreEqual(
            string.Empty,
            AgentConfig.RequireOptionalAbsolutePath(ENV_SMOKE_SCRIPT, string.Empty));
        TestAssert.Throws<AgentException>(
            () => AgentConfig.RequireOptionalAbsolutePath(
                ENV_SMOKE_SCRIPT,
                "relative-smoke.sh"));

        TestAssert.Throws<AgentException>(
            () => Durability.ReadTrustedRegularFileBytes(
                missing,
                "smoke script",
                requireExecutable: true));

        var script = Path.Combine(environment.Root, "smoke.sh");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            script,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CollectionAssert.AreEqual(
            System.Text.Encoding.UTF8.GetBytes("#!/bin/sh\nexit 0\n"),
            Durability.ReadTrustedRegularFileBytes(
                script,
                "smoke script",
                requireExecutable: true));

        var nonExecutable = Path.Combine(environment.Root, "not-executable.sh");
        File.WriteAllText(nonExecutable, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(
            nonExecutable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        TestAssert.Throws<AgentException>(
            () => Durability.ReadTrustedRegularFileBytes(
                nonExecutable,
                "smoke script",
                requireExecutable: true));

        var symlink = Path.Combine(environment.Root, "smoke-link");
        File.CreateSymbolicLink(symlink, script);
        TestAssert.Throws<AgentException>(
            () => Durability.ReadTrustedRegularFileBytes(
                symlink,
                "smoke script",
                requireExecutable: true));
    }

    [TestMethod]
    public void AttestationEndpointMustBeHttpOrHttpsWhenConfigured()
    {
        Assert.AreEqual(
            string.Empty,
            AgentConfig.RequireOptionalAttestationUri(ENV_ATTESTATION_ENDPOINT, string.Empty));
        Assert.AreEqual(
            "https://health.example.test/v1/attest",
            AgentConfig.RequireOptionalAttestationUri(
                ENV_ATTESTATION_ENDPOINT,
                "https://health.example.test/v1/attest"));
        Assert.AreEqual(
            "http://127.0.0.1:8081/attest",
            AgentConfig.RequireOptionalAttestationUri(
                ENV_ATTESTATION_ENDPOINT,
                "http://127.0.0.1:8081/attest"));

        foreach (var invalid in new[]
                 {
                     "file:///tmp/receiver",
                     "ftp://example.test/attest",
                     "mailto:ops@example.test",
                     "/relative/attest"
                 })
        {
            TestAssert.Throws<AgentException>(
                () => AgentConfig.RequireOptionalHttpUri(
                    ENV_ATTESTATION_ENDPOINT,
                    invalid));
        }
    }

    [TestMethod]
    public void DeploymentPathTopologyRejectsGitCleanAndStateOverlaps()
    {
        using var environment = TestEnvironment.Create();
        var root = environment.Root;
        var app = Path.Combine(root, "app");
        var runtime = Path.Combine(root, "bin", "runtime");
        var revision = Path.Combine(root, "doc", "revision");
        var state = Path.Combine(root, "state");

        AgentConfig.ValidateDeploymentPathTopology(
            app,
            runtime,
            revision,
            state,
            string.Empty,
            string.Empty);

        TestAssert.Throws<AgentException>(() =>
            AgentConfig.ValidateDeploymentPathTopology(
                app,
                runtime,
                revision,
                Path.Combine(app, ".state"),
                string.Empty,
                string.Empty));

        TestAssert.Throws<AgentException>(() =>
            AgentConfig.ValidateDeploymentPathTopology(
                app,
                Path.Combine(app, "runtime"),
                revision,
                state,
                string.Empty,
                string.Empty));

        TestAssert.Throws<AgentException>(() =>
            AgentConfig.ValidateDeploymentPathTopology(
                app,
                runtime,
                Path.Combine(app, "revision"),
                state,
                string.Empty,
                string.Empty));

        TestAssert.Throws<AgentException>(() =>
            AgentConfig.ValidateDeploymentPathTopology(
                app,
                runtime,
                revision,
                state,
                Path.Combine(app, "secret"),
                string.Empty));

        TestAssert.Throws<AgentException>(() =>
            AgentConfig.ValidateDeploymentPathTopology(
                app,
                runtime,
                revision,
                state,
                string.Empty,
                Path.Combine(app, "smoke.sh")));
    }

    [TestMethod]
    public void ServiceAndUnitIdentifiersRejectOptionInjection()
    {
        Assert.AreEqual(
            "id.exergism.org",
            AgentConfig.RequireSafeServiceIdentifier(
                ENV_SERVICE,
                "id.exergism.org"));
        Assert.AreEqual(
            "id-exergism.service",
            AgentConfig.RequireSafeSystemdUnit(
                ENV_SERVICE_UNIT,
                "id-exergism.service"));

        foreach (var invalid in new[]
                 {
                     "-id",
                     "../id",
                     "id/name",
                     "id name"
                 })
            TestAssert.Throws<AgentException>(() =>
                AgentConfig.RequireSafeServiceIdentifier(
                    ENV_SERVICE,
                    invalid));

        foreach (var invalid in new[]
                 {
                     "--now",
                     "../id.service",
                     "id/service",
                     "id service"
                 })
            TestAssert.Throws<AgentException>(() =>
                AgentConfig.RequireSafeSystemdUnit(
                    ENV_SERVICE_UNIT,
                    invalid));
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
