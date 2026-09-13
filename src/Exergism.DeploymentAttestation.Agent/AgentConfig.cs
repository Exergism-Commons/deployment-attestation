using System.Net;
using System.Text;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class AgentConfig
{
    private AgentConfig(Dictionary<string, string> values)
    {
        string Required(string key)
            => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : throw new AgentException($"Missing required configuration: {key}");

        string Optional(string key, string fallback = "")
            => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

        Service = Required("EC_SERVICE");
        Repository = Required("EC_REPOSITORY");
        EnvironmentName = Required("EC_ENVIRONMENT");
        ReleaseTag = Required("EC_RELEASE_TAG");
        AppDirectory = RequireAbsolutePath("EC_APP_DIR", Required("EC_APP_DIR"));
        AppBinary = RequireAbsolutePath("EC_APP_BIN", Required("EC_APP_BIN"));
        ServiceUnit = Required("EC_SERVICE_UNIT");
        SourceRevisionFile = RequireAbsolutePath("EC_SOURCE_REVISION_FILE", Required("EC_SOURCE_REVISION_FILE"));
        LocalUrl = RequireHttpUri("EC_LOCAL_URL", Required("EC_LOCAL_URL"));
        PublicUrl = RequireHttpUri("EC_PUBLIC_URL", Required("EC_PUBLIC_URL"));

        HostId = Optional("EC_HOST_ID", Dns.GetHostName());
        GitHubDownloadBase = new Uri(Optional(
            "EC_GITHUB_DOWNLOAD_BASE",
            $"https://github.com/{Repository}/releases/download/{ReleaseTag}").TrimEnd('/') + "/", UriKind.Absolute);
        ReleaseManifestName = Optional("EC_RELEASE_MANIFEST", "DEPLOYMENT_MANIFEST.json");
        AttestationEndpoint = Optional("EC_ATTESTATION_ENDPOINT");
        HmacSecretFile = Optional("EC_HMAC_SECRET_FILE");
        SmokeScript = Optional("EC_SMOKE_SCRIPT");
        SmokeTimeout = PositiveSeconds(Optional("EC_SMOKE_TIMEOUT", "60"), "EC_SMOKE_TIMEOUT");
        DownloadTimeout = PositiveSeconds(Optional("EC_DOWNLOAD_TIMEOUT", "120"), "EC_DOWNLOAD_TIMEOUT");
        CheckPublic = Optional("EC_CHECK_PUBLIC", "1") == "1";
        StateDirectory = RequireAbsolutePath(
            "EC_STATE_DIR",
            Optional("EC_STATE_DIR", $"/var/lib/ec-deployment-attestation/{Service}"));

        CurrentStateFile = Path.Combine(StateDirectory, "current-state.json");
        TransactionFile = Path.Combine(StateDirectory, "transaction.json");
        BackupDirectory = Path.Combine(StateDirectory, "backups");
        StateLockPath = Path.Combine(StateDirectory, "agent.lock");
        CoordinationLockPath = $"/run/lock/ec-deployment-attestation-{Sanitize(Service)}.agent.lock";
    }

    public string Service { get; }
    public string Repository { get; }
    public string EnvironmentName { get; }
    public string ReleaseTag { get; }
    public string AppDirectory { get; }
    public string AppBinary { get; }
    public string ServiceUnit { get; }
    public string SourceRevisionFile { get; }
    public Uri LocalUrl { get; }
    public Uri PublicUrl { get; }
    public string HostId { get; }
    public Uri GitHubDownloadBase { get; }
    public string ReleaseManifestName { get; }
    public string AttestationEndpoint { get; }
    public string HmacSecretFile { get; }
    public string SmokeScript { get; }
    public TimeSpan SmokeTimeout { get; }
    public TimeSpan DownloadTimeout { get; }
    public bool CheckPublic { get; }
    public string StateDirectory { get; }
    public string CurrentStateFile { get; }
    public string TransactionFile { get; }
    public string BackupDirectory { get; }
    public string StateLockPath { get; }
    public string CoordinationLockPath { get; }
    public string InstallTransactionRoot { get; } = "/var/lib/ec-deployment-attestation/install";

    public Uri ReleaseUri(string name) => new(GitHubDownloadBase, Uri.EscapeDataString(name));

    public static AgentConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new AgentException($"Configuration not readable: {path}");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                throw new AgentException($"Invalid configuration line in {path}: {raw}");

            var key = line[..eq].Trim();
            if (!IsName(key))
                throw new AgentException($"Invalid configuration key: {key}");
            values[key] = ParseValue(line[(eq + 1)..].Trim());
        }

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.StartsWith("EC_", StringComparison.Ordinal) && !values.ContainsKey(key))
                values[key] = entry.Value?.ToString() ?? "";
        }

        return new AgentConfig(values);
    }

    internal static string ParseValue(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            return value[1..^1];

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var sb = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i + 1 >= inner.Length)
                {
                    sb.Append(inner[i]);
                    continue;
                }
                var next = inner[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => throw new AgentException("Unsupported escape in configuration value")
                });
            }
            var parsed = sb.ToString();
            if (parsed.Contains('$') || parsed.Contains('`'))
                throw new AgentException("Shell expansion is not supported in agent configuration");
            return parsed;
        }

        if (value.Contains('$') || value.Contains('`'))
            throw new AgentException("Shell expansion is not supported in agent configuration");
        return value;
    }
    private static bool IsName(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
            return false;
        return key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string Sanitize(string value)
        => new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray());

    private static string RequireAbsolutePath(string key, string value)
        => Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : throw new AgentException($"{key} must be an absolute path");

    private static Uri RequireHttpUri(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AgentException($"{key} must be an absolute HTTP(S) URL");
        return uri;
    }

    private static TimeSpan PositiveSeconds(string value, string key)
    {
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
            throw new AgentException($"{key} must be a positive integer number of seconds");
        return TimeSpan.FromSeconds(seconds);
    }
}
) || value.Contains('`'))
            throw new AgentException("Shell expansion is not supported in agent configuration");
        return value;
    }

    private static bool IsName(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
            return false;
        return key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static string Sanitize(string value)
        => new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray());

    private static string RequireAbsolutePath(string key, string value)
        => Path.IsPathFullyQualified(value)
            ? Path.GetFullPath(value)
            : throw new AgentException($"{key} must be an absolute path");

    private static Uri RequireHttpUri(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AgentException($"{key} must be an absolute HTTP(S) URL");
        return uri;
    }

    private static TimeSpan PositiveSeconds(string value, string key)
    {
        if (!int.TryParse(value, out var seconds) || seconds <= 0)
            throw new AgentException($"{key} must be a positive integer number of seconds");
        return TimeSpan.FromSeconds(seconds);
    }
}
