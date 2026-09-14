using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

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

    internal static bool IsValidRepositoryIdentifier(string value)
        => RepositoryPattern.IsMatch(value);
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
            JSON_SCHEMA_VERSION, JSON_REPOSITORY, JSON_RELEASE_TAG, JSON_SOURCE_COMMIT, JSON_GENERATED_AT, JSON_ASSETS
        };
        RequireExactProperties(root, allowed, "$");

        var schema = RequireString(root, JSON_SCHEMA_VERSION);
        var repo = RequireString(root, JSON_REPOSITORY);
        var tag = RequireString(root, JSON_RELEASE_TAG);
        var commit = RequireString(root, JSON_SOURCE_COMMIT);
        var generated = RequireString(root, JSON_GENERATED_AT);
        var assets = root.GetProperty(JSON_ASSETS);

        if (schema != SCHEMA_VERSION)
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
            RequireExactProperties(asset, new HashSet<string>(StringComparer.Ordinal) { JSON_NAME, JSON_SHA256 }, $"$.assets.{property.Name}");
            var name = RequireString(asset, JSON_NAME);
            var digest = RequireString(asset, JSON_SHA256);
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
                JSON_SOURCE_COMMIT, JSON_BINARY_SHA256, JSON_RELEASE_MANIFEST_SHA256, JSON_RELEASE_TAG
            },
            "$");

        var commit = RequireString(root, JSON_SOURCE_COMMIT);
        var binary = RequireString(root, JSON_BINARY_SHA256);
        var tag = RequireString(root, JSON_RELEASE_TAG);
        var manifest = OptionalNullableString(root, JSON_RELEASE_MANIFEST_SHA256);
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
            writer.WriteString(JSON_BINARY_SHA256, state.BinarySha256);
            if (state.ReleaseManifestSha256 is null)
                writer.WriteNull(JSON_RELEASE_MANIFEST_SHA256);
            else
                writer.WriteString(JSON_RELEASE_MANIFEST_SHA256, state.ReleaseManifestSha256);
            writer.WriteString(JSON_RELEASE_TAG, state.ReleaseTag);
            writer.WriteString(JSON_SOURCE_COMMIT, state.SourceCommit);
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
                JSON_SCHEMA_VERSION, JSON_PHASE,
                JSON_OLD_SOURCE_COMMIT, JSON_OLD_BINARY_SHA256, JSON_OLD_RELEASE_MANIFEST_SHA256,
                JSON_BACKUP_BINARY, JSON_NEW_SOURCE_COMMIT, JSON_NEW_BINARY_SHA256, JSON_NEW_RELEASE_MANIFEST_SHA256
            },
            "$");

        if (RequireString(root, JSON_SCHEMA_VERSION) != SCHEMA_VERSION)
            throw new AgentException("Invalid transaction schema");
        var tx = new DeploymentTransaction(
            RequireString(root, JSON_PHASE),
            RequireString(root, JSON_OLD_SOURCE_COMMIT),
            RequireString(root, JSON_OLD_BINARY_SHA256),
            OptionalNullableString(root, JSON_OLD_RELEASE_MANIFEST_SHA256),
            RequireString(root, JSON_BACKUP_BINARY),
            RequireString(root, JSON_NEW_SOURCE_COMMIT),
            RequireString(root, JSON_NEW_BINARY_SHA256),
            OptionalNullableString(root, JSON_NEW_RELEASE_MANIFEST_SHA256));

        if (tx.Phase is not (PHASE_ACTIVATING or PHASE_COMMITTED))
            throw new AgentException("Invalid transaction phase");
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
            writer.WriteString(JSON_BACKUP_BINARY, tx.BackupBinary);
            writer.WriteString(JSON_NEW_BINARY_SHA256, tx.NewBinarySha256);
            if (tx.NewReleaseManifestSha256 is null) writer.WriteNull(JSON_NEW_RELEASE_MANIFEST_SHA256);
            else writer.WriteString(JSON_NEW_RELEASE_MANIFEST_SHA256, tx.NewReleaseManifestSha256);
            writer.WriteString(JSON_NEW_SOURCE_COMMIT, tx.NewSourceCommit);
            writer.WriteString(JSON_OLD_BINARY_SHA256, tx.OldBinarySha256);
            if (tx.OldReleaseManifestSha256 is null) writer.WriteNull(JSON_OLD_RELEASE_MANIFEST_SHA256);
            else writer.WriteString(JSON_OLD_RELEASE_MANIFEST_SHA256, tx.OldReleaseManifestSha256);
            writer.WriteString(JSON_OLD_SOURCE_COMMIT, tx.OldSourceCommit);
            writer.WriteString(JSON_PHASE, tx.Phase);
            writer.WriteString(JSON_SCHEMA_VERSION, SCHEMA_VERSION);
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
            writer.WriteString(JSON_AGENT_VERSION, agentVersion);
            writer.WritePropertyName(JSON_CHECKS);
            writer.WriteStartObject();
            foreach (var pair in checks.OrderBy(x => x.Key, StringComparer.Ordinal))
                writer.WriteBoolean(pair.Key, pair.Value);
            writer.WriteEndObject();
            writer.WriteString(JSON_DEPLOYED_COMMIT, deployed);
            if (actualRuntime is null) writer.WriteNull(JSON_DEPLOYED_RUNTIME_SHA256);
            else writer.WriteString(JSON_DEPLOYED_RUNTIME_SHA256, actualRuntime);
            writer.WriteString(JSON_ENVIRONMENT, config.EnvironmentName);
            writer.WriteString(JSON_EXPECTED_COMMIT, expected);
            writer.WriteString(JSON_EXPECTED_RUNTIME_SHA256, expectedRuntime);
            writer.WriteString(JSON_HOST_ID, config.HostId);
            if (observationId is not null)
                writer.WriteString(JSON_OBSERVATION_ID, observationId);
            writer.WriteString(JSON_OBSERVED_AT, observedAt);
            writer.WriteString(JSON_RELEASE_MANIFEST_SHA256, manifest);
            writer.WriteString(JSON_RELEASE_TAG, config.ReleaseTag);
            writer.WriteString(JSON_REPOSITORY, config.Repository);
            writer.WriteString(JSON_SCHEMA_VERSION, SCHEMA_VERSION);
            writer.WriteString(JSON_SERVICE, config.Service);
            writer.WriteString(JSON_STATUS, status);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    public static string ReadObservationId(byte[] attestation)
    {
        using var doc = JsonDocument.Parse(attestation);
        return RequireString(doc.RootElement, JSON_OBSERVATION_ID);
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
