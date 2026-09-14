using System.Text;

namespace Exergism.DeploymentAttestation.Agent;

internal static class SelfTest
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ec-agent-selftest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            TestConfig(root);
            TestProtocol(root);
            TestMountInfo();
            Console.WriteLine("Native AOT agent self-test passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Self-test failed: {ex}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void TestConfig(string root)
    {
        var configPath = Path.Combine(root, "service.env");
        File.WriteAllText(
            configPath,
            string.Join(
                "\n",
                "EC_SERVICE=id.exergism.org",
                "EC_REPOSITORY=Exergism-Commons/id",
                "EC_ENVIRONMENT=production",
                "EC_RELEASE_TAG=runtime-main",
                $"EC_APP_DIR={root}/checkout",
                $"EC_APP_BIN={root}/idresolver",
                "EC_SERVICE_UNIT=id-exergism",
                $"EC_SOURCE_REVISION_FILE={root}/source-revision.txt",
                "EC_LOCAL_URL=http://127.0.0.1:8080/",
                "EC_PUBLIC_URL=https://id.exergism.org/",
                "EC_DOWNLOAD_TIMEOUT=37",
                "EC_CHECK_PUBLIC=0",
                ""),
            Encoding.UTF8);

        var config = AgentConfig.Load(configPath);
        Assert(config.Service == "id.exergism.org", "config service");
        Assert(config.DownloadTimeout == TimeSpan.FromSeconds(37), "config timeout");
        Assert(!config.CheckPublic, "config public check");
        Assert(AgentConfig.ParseValue("\"hello\\nworld\"") == @"hello\nworld", "quoted config preserves non-special backslash");
        Assert(AgentConfig.ParseValue("\"hello\\\\world\"") == @"hello\world", "quoted config decodes Bash-special backslash");
        AssertThrows(() => AgentConfig.ParseValue("$HOME/runtime"), "shell variable expansion");
        AssertThrows(() => AgentConfig.ParseValue("\"$HOME/runtime\""), "double-quoted shell variable expansion");
        AssertThrows(() => AgentConfig.ParseValue("`id`"), "shell command expansion");
    }

    private static void TestProtocol(string root)
    {
        const string manifest = """
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
        var parsed = Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(manifest),
            "Exergism-Commons/id",
            "runtime-main",
            "amd64");
        Assert(parsed.SourceCommit == new string('a', 40), "manifest commit");
        Assert(parsed.AssetSha256 == new string('b', 64), "manifest digest");

        var arbitraryKey = manifest.Replace(
            "\"amd64\": {",
            "\"experimental/linux-x64\": {",
            StringComparison.Ordinal);
        _ = Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(arbitraryKey),
            "Exergism-Commons/id",
            "runtime-main",
            "experimental/linux-x64");

        AssertThrows(() => Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(manifest.Replace(
                "\"generated_at\": \"2026-09-13T10:00:00Z\",",
                "",
                StringComparison.Ordinal)),
            "Exergism-Commons/id",
            "runtime-main",
            "amd64"), "missing generated_at");

        AssertThrows(() => Protocol.ParseReleaseManifest(
            Encoding.UTF8.GetBytes(manifest.Replace(
                "\"schema_version\": \"0.1\",",
                "\"schema_version\": \"0.1\", \"unexpected\": true,",
                StringComparison.Ordinal)),
            "Exergism-Commons/id",
            "runtime-main",
            "amd64"), "extra top-level property");

        var state = new CurrentState(new string('a', 40), new string('b', 64), null, "runtime-main");
        var statePath = Path.Combine(root, "state.json");
        File.WriteAllBytes(statePath, Protocol.WriteCurrentState(state));
        Assert(Protocol.ReadCurrentState(statePath) == state, "current state roundtrip");

        var tx = new DeploymentTransaction(
            "activating",
            new string('a', 40),
            new string('b', 64),
            null,
            Path.Combine(root, "backup"),
            new string('c', 40),
            new string('d', 64),
            new string('e', 64));
        var txPath = Path.Combine(root, "transaction.json");
        File.WriteAllBytes(txPath, Protocol.WriteTransaction(tx));
        Assert(Protocol.ReadTransaction(txPath) == tx, "transaction roundtrip");
    }

    private static void TestMountInfo()
    {
        var mounts = RuntimeInspector.ParseMountInfo(
            "29 23 8:1 / / rw,relatime - ext4 /dev/root rw\n" +
            "30 29 8:1 /srv /srv/id.exergism.org ro,relatime - ext4 /dev/root rw\n" +
            "31 29 8:1 /usr/local/bin/idresolver /usr/local/bin/idresolver ro,relatime - ext4 /dev/root rw\n");
        Assert(mounts.Count == 3, "mountinfo count");
        Assert(mounts[1].Options.Contains("ro"), "mountinfo ro");
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {name}");
    }

    private static void AssertThrows(Action action, string name)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException($"Expected failure: {name}");
    }
}
