using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed record ReleaseAsset(string Name, string Sha256);
internal sealed record ReleaseSnapshot(string SourceCommit, string AssetName, string AssetSha256, string ManifestSha256);
internal sealed record CurrentState(string SourceCommit, string BinarySha256, string? ReleaseManifestSha256, string ReleaseTag);
internal sealed record DeploymentTransaction(
    string Phase,
    string OldSourceCommit,
    string OldBinarySha256,
    string? OldReleaseManifestSha256,
    string BackupBinary,
    string NewSourceCommit,
    string NewBinarySha256,
    string? NewReleaseManifestSha256);

internal static partial class Protocol
{
    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex DigestPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex AssetNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Rfc3339Pattern = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    public static ReleaseSnapshot ParseReleaseManifest(
        byte[] bytes,
        string repository,
        string releaseTag,
        string architecture)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        var root = document.RootElement;
        RequireObject(root, "$");
        EnsureNoDuplicateProperties(root, "$");

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema_version", "repository", "release_tag", "source_commit", "generated_at", "assets"
        };
        RequireExactProperties(root, allowed, "$");

        var schema = RequireString(root, "schema_version");
        var repo = RequireString(root, "repository");
        var tag = RequireString(root, "release_tag");
        var commit = RequireString(root, "source_commit");
        var generated = RequireString(root, "generated_at");
        var assets = root.GetProperty("assets");

        if (schema != "0.1")
            throw new AgentException("Release manifest schema_version must be 0.1");
        if (!RepositoryPattern.IsMatch(repo) || repo != repository)
            throw new AgentException("Release manifest repository mismatch");
        if (string.IsNullOrEmpty(tag) || tag != releaseTag)
            throw new AgentException("Release manifest release_tag mismatch");
        if (!CommitPattern.IsMatch(commit))
            throw new AgentException("Release manifest source_commit is invalid");
        if (!Rfc3339Pattern.IsMatch(generated) ||
            !DateTimeOffset.TryParse(generated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new AgentException("Release manifest generated_at is not RFC3339");

        RequireObject(assets, "$.assets");
        if (!assets.EnumerateObject().Any())
            throw new AgentException("Release manifest assets must not be empty");

        ReleaseAsset? selected = null;
        foreach (var property in assets.EnumerateObject())
        {
            // The schema deliberately allows arbitrary architecture/channel keys;
            // only the selected host architecture is semantically bound below.
            var asset = property.Value;
            RequireObject(asset, $"$.assets.{property.Name}");
            EnsureNoDuplicateProperties(asset, $"$.assets.{property.Name}");
            RequireExactProperties(asset, new HashSet<string>(StringComparer.Ordinal) { "name", "sha256" }, $"$.assets.{property.Name}");
            var name = RequireString(asset, "name");
            var digest = RequireString(asset, "sha256");
            if (!AssetNamePattern.IsMatch(name))
                throw new AgentException($"Invalid release asset name for {property.Name}");
            if (!DigestPattern.IsMatch(digest))
                throw new AgentException($"Invalid release asset digest for {property.Name}");
            if (property.Name == architecture)
                selected = new ReleaseAsset(name, digest);
        }

        if (selected is null)
            throw new AgentException($"Release manifest has no asset for architecture {architecture}");

        var manifestDigest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new ReleaseSnapshot(commit, selected.Name, selected.Sha256, manifestDigest);
    }

    public static CurrentState ReadCurrentState(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        RequireObject(root, "$");
        EnsureNoDuplicateProperties(root, "$");
        RequireExactProperties(
            root,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "source_commit", "binary_sha256", "release_manifest_sha256", "release_tag"
            },
            "$");

        var commit = RequireString(root, "source_commit");
        var binary = RequireString(root, "binary_sha256");
        var tag = RequireString(root, "release_tag");
        var manifest = OptionalNullableString(root, "release_manifest_sha256");
        if (!CommitPattern.IsMatch(commit) || !DigestPattern.IsMatch(binary))
            throw new AgentException("Invalid current-state source/runtime digest");
        if (manifest is not null && !DigestPattern.IsMatch(manifest))
            throw new AgentException("Invalid current-state manifest digest");
        return new CurrentState(commit, binary, manifest, tag);
    }

    public static byte[] WriteCurrentState(CurrentState state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("binary_sha256", state.BinarySha256);
            if (state.ReleaseManifestSha256 is null)
                writer.WriteNull("release_manifest_sha256");
            else
                writer.WriteString("release_manifest_sha256", state.ReleaseManifestSha256);
            writer.WriteString("release_tag", state.ReleaseTag);
            writer.WriteString("source_commit", state.SourceCommit);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static DeploymentTransaction ReadTransaction(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        RequireObject(root, "$");
        EnsureNoDuplicateProperties(root, "$");
        RequireExactProperties(
            root,
            new HashSet<string>(StringComparer.Ordinal)
            {
                "schema_version", "phase",
                "old_source_commit", "old_binary_sha256", "old_release_manifest_sha256",
                "backup_binary", "new_source_commit", "new_binary_sha256", "new_release_manifest_sha256"
            },
            "$");

        if (RequireString(root, "schema_version") != "0.1")
            throw new AgentException("Invalid transaction schema");
        var tx = new DeploymentTransaction(
            RequireString(root, "phase"),
            RequireString(root, "old_source_commit"),
            RequireString(root, "old_binary_sha256"),
            OptionalNullableString(root, "old_release_manifest_sha256"),
            RequireString(root, "backup_binary"),
            RequireString(root, "new_source_commit"),
            RequireString(root, "new_binary_sha256"),
            OptionalNullableString(root, "new_release_manifest_sha256"));

        if (!CommitPattern.IsMatch(tx.OldSourceCommit) ||
            !CommitPattern.IsMatch(tx.NewSourceCommit) ||
            !DigestPattern.IsMatch(tx.OldBinarySha256) ||
            !DigestPattern.IsMatch(tx.NewBinarySha256))
            throw new AgentException("Invalid transaction source/runtime digest");
        if (tx.OldReleaseManifestSha256 is not null && !DigestPattern.IsMatch(tx.OldReleaseManifestSha256))
            throw new AgentException("Invalid transaction old manifest digest");
        if (tx.NewReleaseManifestSha256 is not null && !DigestPattern.IsMatch(tx.NewReleaseManifestSha256))
            throw new AgentException("Invalid transaction new manifest digest");
        return tx;
    }

    public static byte[] WriteTransaction(DeploymentTransaction tx)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("backup_binary", tx.BackupBinary);
            writer.WriteString("new_binary_sha256", tx.NewBinarySha256);
            if (tx.NewReleaseManifestSha256 is null) writer.WriteNull("new_release_manifest_sha256");
            else writer.WriteString("new_release_manifest_sha256", tx.NewReleaseManifestSha256);
            writer.WriteString("new_source_commit", tx.NewSourceCommit);
            writer.WriteString("old_binary_sha256", tx.OldBinarySha256);
            if (tx.OldReleaseManifestSha256 is null) writer.WriteNull("old_release_manifest_sha256");
            else writer.WriteString("old_release_manifest_sha256", tx.OldReleaseManifestSha256);
            writer.WriteString("old_source_commit", tx.OldSourceCommit);
            writer.WriteString("phase", tx.Phase);
            writer.WriteString("schema_version", "0.1");
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static byte[] BuildAttestation(
        AgentConfig config,
        string deployed,
        string expected,
        string status,
        IReadOnlyDictionary<string, bool> checks,
        string manifest,
        string? actualRuntime,
        string expectedRuntime,
        string observedAt,
        string agentVersion)
    {
        var withoutId = WriteAttestationCore(
            config, deployed, expected, status, checks, manifest, actualRuntime,
            expectedRuntime, observedAt, agentVersion, observationId: null);
        var observationId = Convert.ToHexStringLower(SHA256.HashData(withoutId));
        return WriteAttestationCore(
            config, deployed, expected, status, checks, manifest, actualRuntime,
            expectedRuntime, observedAt, agentVersion, observationId);
    }

    private static byte[] WriteAttestationCore(
        AgentConfig config,
        string deployed,
        string expected,
        string status,
        IReadOnlyDictionary<string, bool> checks,
        string manifest,
        string? actualRuntime,
        string expectedRuntime,
        string observedAt,
        string agentVersion,
        string? observationId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("agent_version", agentVersion);
            writer.WritePropertyName("checks");
            writer.WriteStartObject();
            foreach (var pair in checks.OrderBy(x => x.Key, StringComparer.Ordinal))
                writer.WriteBoolean(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteString("deployed_commit", deployed);
            if (actualRuntime is null) writer.WriteNull("deployed_runtime_sha256");
            else writer.WriteString("deployed_runtime_sha256", actualRuntime);
            writer.WriteString("environment", config.EnvironmentName);
            writer.WriteString("expected_commit", expected);
            writer.WriteString("expected_runtime_sha256", expectedRuntime);
            writer.WriteString("host_id", config.HostId);
            if (observationId is not null)
                writer.WriteString("observation_id", observationId);
            writer.WriteString("observed_at", observedAt);
            writer.WriteString("release_manifest_sha256", manifest);
            writer.WriteString("release_tag", config.ReleaseTag);
            writer.WriteString("repository", config.Repository);
            writer.WriteString("schema_version", "0.1");
            writer.WriteString("service", config.Service);
            writer.WriteString("status", status);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static string ReadObservationId(byte[] attestation)
    {
        using var doc = JsonDocument.Parse(attestation);
        return RequireString(doc.RootElement, "observation_id");
    }

    private static void EnsureNoDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new AgentException($"Duplicate JSON property at {path}: {property.Name}");
                EnsureNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var item in element.EnumerateArray())
                EnsureNoDuplicateProperties(item, $"{path}[{i++}]");
        }
    }

    private static void RequireExactProperties(JsonElement element, HashSet<string> expected, string path)
    {
        var actual = element.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            var missing = expected.Except(actual).OrderBy(x => x, StringComparer.Ordinal);
            var extra = actual.Except(expected).OrderBy(x => x, StringComparer.Ordinal);
            throw new AgentException(
                $"JSON properties differ at {path}; missing=[{string.Join(",", missing)}], extra=[{string.Join(",", extra)}]");
        }
    }

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new AgentException($"Expected object at {path}");
    }

    private static string RequireString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new AgentException($"Missing/invalid string property: {property}");
        return value.GetString()!;
    }

    private static string? OptionalNullableString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            throw new AgentException($"Missing property: {property}");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw new AgentException($"Invalid nullable string property: {property}")
        };
    }
}
