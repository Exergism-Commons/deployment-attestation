using System.Diagnostics;
using System.Net;
using System.Text;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed class AgentConfig
{
    private AgentConfig(Dictionary<string, string> values)
    {
        string Required(string key)
            => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new AgentException($"Missing required configuration: {key}");

        string Optional(string key, string fallback = "")
            => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

        Service = Required(ENV_SERVICE);
        Repository = Required(ENV_REPOSITORY);
        EnvironmentName = Required(ENV_ENVIRONMENT);
        ReleaseTag = Required(ENV_RELEASE_TAG);
        AppDirectory = RequireAbsolutePath(ENV_APP_DIR, Required(ENV_APP_DIR));
        AppBinary = RequireAbsolutePath(ENV_APP_BIN, Required(ENV_APP_BIN));
        ServiceUnit = Required(ENV_SERVICE_UNIT);
        SourceRevisionFile = RequireAbsolutePath(ENV_SOURCE_REVISION_FILE, Required(ENV_SOURCE_REVISION_FILE));
        LocalUrl = RequireHttpUri(ENV_LOCAL_URL, Required(ENV_LOCAL_URL));
        PublicUrl = RequireHttpUri(ENV_PUBLIC_URL, Required(ENV_PUBLIC_URL));

        values.TryGetValue(ENV_HOST_ID, out var configuredHostId);
        HostId = HostIdentity.SelectConfiguredOrDefault(configuredHostId, HostIdentity.ResolveDefault);
        GitHubDownloadBase = new Uri(Optional(
            ENV_GITHUB_DOWNLOAD_BASE,
            $"https://github.com/{Repository}/releases/download/{ReleaseTag}").TrimEnd('/') + "/", UriKind.Absolute);
        ReleaseManifestName = Optional(ENV_RELEASE_MANIFEST, RELEASE_MANIFEST_DEFAULT);
        AttestationEndpoint = Optional(ENV_ATTESTATION_ENDPOINT);
        HmacSecretFile = Optional(ENV_HMAC_SECRET_FILE);
        SmokeScript = Optional(ENV_SMOKE_SCRIPT);
        SmokeTimeout = PositiveSeconds(Optional(ENV_SMOKE_TIMEOUT, "60"), ENV_SMOKE_TIMEOUT);
        DownloadTimeout = PositiveSeconds(Optional(ENV_DOWNLOAD_TIMEOUT, "120"), ENV_DOWNLOAD_TIMEOUT);
        AgentHealthMaxAge = PositiveSeconds(Optional(ENV_AGENT_HEALTH_MAX_AGE, "1500"), ENV_AGENT_HEALTH_MAX_AGE);
        CheckPublic = Optional(ENV_CHECK_PUBLIC, CONFIG_BOOLEAN_TRUE) == CONFIG_BOOLEAN_TRUE;
        StateDirectory = RequireAbsolutePath(
            ENV_STATE_DIR,
            Optional(ENV_STATE_DIR, $"/var/lib/ec-deployment-attestation/{Service}"));

        CurrentStateFile = Path.Combine(StateDirectory, FILE_CURRENT_STATE);
        TransactionFile = Path.Combine(StateDirectory, FILE_TRANSACTION);
        AgentHealthFile = Path.Combine(StateDirectory, FILE_AGENT_HEALTH);
        BackupDirectory = Path.Combine(StateDirectory, DIRECTORY_BACKUPS);
        StateLockPath = Path.Combine(StateDirectory, FILE_STATE_LOCK);
        CoordinationLockPath = $"/run/lock/ec-deployment-attestation-{Sanitize(Service)}.agent.lock";
        AgentServiceUnit = $"ec-deployment-attestation@{Service}.service";
        AgentTimerUnit = $"ec-deployment-attestation@{Service}.timer";
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
    public TimeSpan AgentHealthMaxAge { get; }
    public bool CheckPublic { get; }
    public string StateDirectory { get; }
    public string CurrentStateFile { get; }
    public string TransactionFile { get; }
    public string AgentHealthFile { get; }
    public string BackupDirectory { get; }
    public string StateLockPath { get; }
    public string CoordinationLockPath { get; }
    public string AgentServiceUnit { get; }
    public string AgentTimerUnit { get; }
    public string InstallTransactionRoot { get; } = INSTALL_TRANSACTION_ROOT;

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

            var equals = line.IndexOf('=');
            if (equals <= 0)
                throw new AgentException($"Invalid configuration line in {path}: {raw}");

            var key = line[..equals].Trim();
            if (!IsName(key))
                throw new AgentException($"Invalid configuration key: {key}");
            values[key] = ParseValue(line[(equals + 1)..].Trim());
        }

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
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
            var builder = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] != '\\' || i + 1 >= inner.Length)
                {
                    builder.Append(inner[i]);
                    continue;
                }

                var next = inner[++i];
                builder.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '"' => '"',
                    '\\' => '\\',
                    _ => throw new AgentException("Unsupported escape in configuration value")
                });
            }

            var parsed = builder.ToString();
            RejectShellExpansion(parsed);
            return parsed;
        }

        RejectShellExpansion(value);
        return value;
    }

    private static void RejectShellExpansion(string value)
    {
        if (value.Contains('$') || value.Contains('`'))
            throw new AgentException("Shell expansion is not supported in agent configuration");
    }

    private static bool IsName(string key)
    {
        if (key.Length == 0 || !(char.IsAsciiLetter(key[0]) || key[0] == '_'))
            return false;
        return key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string Sanitize(string value)
        => new(value.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-' ? character : '-').ToArray());

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

internal static class HostIdentity
{
    internal static string SelectConfiguredOrDefault(string? configured, Func<string> resolveDefault)
        => !string.IsNullOrWhiteSpace(configured) ? configured : resolveDefault();

    internal static string ResolveDefault()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(COMMAND_HOSTNAME)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add(HOSTNAME_FLAG_FQDN);

            if (process.Start())
            {
                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                }
                else if (process.ExitCode == 0)
                {
                    return SelectDefault(process.StandardOutput.ReadToEnd(), Dns.GetHostName());
                }
            }
        }
        catch
        {
            // Preserve the Bash fallback: hostname -f || hostname.
        }

        return Dns.GetHostName();
    }

    internal static string SelectDefault(string? fqdn, string shortName)
        => string.IsNullOrWhiteSpace(fqdn) ? shortName : fqdn.Trim();
}

