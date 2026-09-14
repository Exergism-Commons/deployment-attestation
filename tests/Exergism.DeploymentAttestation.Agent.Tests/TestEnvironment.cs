using System.Text;
using Exergism.DeploymentAttestation.Agent;

namespace Exergism.DeploymentAttestation.Agent.Tests;

internal sealed class TestEnvironment : IDisposable
{
    private TestEnvironment(string root, AgentConfig config)
    {
        Root = root;
        Config = config;
    }

    internal string Root { get; }
    internal AgentConfig Config { get; }

    internal static TestEnvironment Create(bool withAttestationEndpoint = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ec-agent-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var appDirectory = Path.Combine(root, "checkout");
        Directory.CreateDirectory(appDirectory);

        var configPath = Path.Combine(root, "service.env");
        var lines = new List<string>
        {
            "EC_SERVICE=id.exergism.org",
            "EC_REPOSITORY=Exergism-Commons/id",
            "EC_ENVIRONMENT=test",
            "EC_RELEASE_TAG=runtime-main",
            $"EC_APP_DIR={appDirectory}",
            $"EC_APP_BIN={Path.Combine(root, "idresolver")}",
            "EC_SERVICE_UNIT=id-exergism",
            $"EC_SOURCE_REVISION_FILE={Path.Combine(root, "source-revision.txt")}",
            "EC_LOCAL_URL=http://127.0.0.1:8080/",
            "EC_PUBLIC_URL=https://id.exergism.org/",
            $"EC_STATE_DIR={Path.Combine(root, "state")}",
            "EC_AGENT_HEALTH_MAX_AGE=1500",
            "EC_CHECK_PUBLIC=0"
        };

        if (withAttestationEndpoint)
        {
            var secretPath = Path.Combine(root, "secret");
            File.WriteAllText(secretPath, "test-secret\n", Encoding.UTF8);
            lines.Add("EC_ATTESTATION_ENDPOINT=https://health.example.test/v1/attest");
            lines.Add($"EC_HMAC_SECRET_FILE={secretPath}");
        }

        File.WriteAllText(configPath, string.Join('\n', lines) + "\n", Encoding.UTF8);
        var config = AgentConfig.Load(configPath);
        Directory.CreateDirectory(config.StateDirectory);
        Directory.CreateDirectory(config.BackupDirectory);
        return new TestEnvironment(root, config);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
