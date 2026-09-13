using System.Text;
using Exergism.DeploymentAttestation.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent.Tests;

[TestClass]
public sealed class ProtocolTests
{
    private const string VALID_MANIFEST = """
    {
      "schema_version": "0.1",
      "repository": "Exergism-Commons/id",
      "release_tag": "runtime-main",
      "source_commit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "generated_at": "2026-09-13T10:00:00Z",
      "assets": {
        "amd64": {
          "name": "idresolver-linux-amd64",
          "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        }
      }
    }
    """;

    [TestMethod]
    public void ManifestParsesExpectedArchitecture()
    {
        var snapshot = Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(VALID_MANIFEST), "Exergism-Commons/id", "runtime-main", "amd64");
        Assert.AreEqual(new string('a', 40), snapshot.SourceCommit);
        Assert.AreEqual(new string('b', 64), snapshot.AssetSha256);
    }

    [TestMethod]
    public void ManifestRejectsUnexpectedProperty()
    {
        var invalid = VALID_MANIFEST.Replace(
            $"\"{JSON_SCHEMA_VERSION}\": \"{SCHEMA_VERSION}\",",
            $"\"{JSON_SCHEMA_VERSION}\": \"{SCHEMA_VERSION}\", \"unexpected\": true,",
            StringComparison.Ordinal);
        TestAssert.Throws<AgentException>(() => Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(invalid), "Exergism-Commons/id", "runtime-main", "amd64"));
    }

    [TestMethod]
    public void StateAndTransactionRoundTrip()
    {
        using var environment = TestEnvironment.Create();
        var state = new CurrentState(new string('a', 40), new string('b', 64), new string('c', 64), "runtime-main");
        File.WriteAllBytes(environment.Config.CurrentStateFile, Protocol.WriteCurrentState(state));
        Assert.AreEqual(state, Protocol.ReadCurrentState(environment.Config.CurrentStateFile));

        var transaction = new DeploymentTransaction(
            PHASE_ACTIVATING,
            new string('a', 40),
            new string('b', 64),
            new string('c', 64),
            Path.Combine(environment.Root, "backup"),
            new string('d', 40),
            new string('e', 64),
            new string('f', 64));
        File.WriteAllBytes(environment.Config.TransactionFile, Protocol.WriteTransaction(transaction));
        Assert.AreEqual(transaction, Protocol.ReadTransaction(environment.Config.TransactionFile));
    }

    [TestMethod]
    public void MountInfoParserPreservesReadOnlyOptions()
    {
        var mounts = RuntimeInspector.ParseMountInfo(
            "29 23 8:1 / / rw,relatime - ext4 /dev/root rw\n" +
            "30 29 8:1 /srv /srv/id.exergism.org ro,relatime - ext4 /dev/root rw\n");
        Assert.AreEqual(2, mounts.Count);
        Assert.IsTrue(mounts[1].Options.Contains("ro"));
    }
}
