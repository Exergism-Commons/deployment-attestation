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

        Service = RequireSafeServiceIdentifier(ENV_SERVICE, Required(ENV_SERVICE));
        Repository = RequireRepositoryIdentifier(ENV_REPOSITORY, Required(ENV_REPOSITORY));
        EnvironmentName = Required(ENV_ENVIRONMENT);
        ReleaseTag = Required(ENV_RELEASE_TAG);
        AppDirectory = RequireAbsolutePath(ENV_APP_DIR, Required(ENV_APP_DIR));
        AppBinary = RequireAbsolutePath(ENV_APP_BIN, Required(ENV_APP_BIN));
        ServiceUnit = RequireSafeSystemdUnit(ENV_SERVICE_UNIT, Required(ENV_SERVICE_UNIT));
        SourceRevisionFile = RequireAbsolutePath(ENV_SOURCE_REVISION_FILE, Required(ENV_SOURCE_REVISION_FILE));
        LocalUrl = RequireLoopbackHttpUri(ENV_LOCAL_URL, Required(ENV_LOCAL_URL));
        PublicUrl = RequireHttpsUri(ENV_PUBLIC_URL, Required(ENV_PUBLIC_URL));

        values.TryGetValue(ENV_HOST_ID, out var configuredHostId);
        HostId = HostIdentity.SelectConfiguredOrDefault(configuredHostId, HostIdentity.ResolveDefault);
        GitHubDownloadBase = RequireHttpsUri(
            ENV_GITHUB_DOWNLOAD_BASE,
            Optional(
                ENV_GITHUB_DOWNLOAD_BASE,
                $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(ReleaseTag)}")
                .TrimEnd('/') + "/");
        ReleaseManifestName = RequireSafeAssetName(
            ENV_RELEASE_MANIFEST,
            Optional(ENV_RELEASE_MANIFEST, RELEASE_MANIFEST_DEFAULT));
        AttestationEndpoint = RequireOptionalAttestationUri(
            ENV_ATTESTATION_ENDPOINT,
            Optional(ENV_ATTESTATION_ENDPOINT));
        HmacSecretFile = RequireAttestationSecret(
            AttestationEndpoint,
            Optional(ENV_HMAC_SECRET_FILE));
        SmokeScript = RequireOptionalAbsolutePath(
            ENV_SMOKE_SCRIPT,
            Optional(ENV_SMOKE_SCRIPT));
        SmokeTimeout = PositiveSeconds(Optional(ENV_SMOKE_TIMEOUT, "60"), ENV_SMOKE_TIMEOUT);
        DownloadTimeout = PositiveSeconds(Optional(ENV_DOWNLOAD_TIMEOUT, "120"), ENV_DOWNLOAD_TIMEOUT);
        AgentHealthMaxAge = PositiveSeconds(Optional(ENV_AGENT_HEALTH_MAX_AGE, "1500"), ENV_AGENT_HEALTH_MAX_AGE);
        CheckPublic = RequireBoolean(
            ENV_CHECK_PUBLIC,
            Optional(ENV_CHECK_PUBLIC, CONFIG_BOOLEAN_TRUE));
        StateDirectory = RequireAbsolutePath(
            ENV_STATE_DIR,
            Optional(ENV_STATE_DIR, $"/var/lib/ec-deployment-attestation/{Service}"));

        ValidateDeploymentPathTopology(
            AppDirectory,
            AppBinary,
            SourceRevisionFile,
            StateDirectory,
            HmacSecretFile,
            SmokeScript);

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

    public Uri ReleaseUri(string name)
        => new(
            GitHubDownloadBase,
            Uri.EscapeDataString(RequireSafeAssetName("release asset", name)));

    public static AgentConfig Load(string path)
    {
        var text = Durability.ReadTrustedConfigText(path);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
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
        {
            var inner = value[1..^1];
            if (inner.Contains('\''))
                throw new AgentException("Concatenated single-quoted shell values are not supported in agent configuration");
            return inner;
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            var inner = value[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '"')
                    throw new AgentException("Embedded unescaped double quote is not supported in agent configuration");

                if (inner[i] != '\\')
                {
                    builder.Append(inner[i]);
                    continue;
                }

                if (++i >= inner.Length)
                    throw new AgentException("Trailing escape is not supported in agent configuration");

                var next = inner[i];
                if (next is '$' or '`' or '"' or '\\')
                {
                    builder.Append(next);
                    continue;
                }

                if (next == '\n')
                    continue;

                // Bash preserves the backslash before non-special characters
                // inside double quotes (for example, "\\n" remains backslash+n).
                builder.Append('\\');
                builder.Append(next);
            }

            var parsed = builder.ToString();
            RejectShellExpansion(parsed);
            return parsed;
        }

        RejectUnsupportedUnquotedShellSyntax(value);
        RejectShellExpansion(value);
        return value;
    }

    private static void RejectUnsupportedUnquotedShellSyntax(string value)
    {
        if (value.Any(character =>
                char.IsWhiteSpace(character) ||
                character is '\\' or '\'' or '"' or '#' or ';' or '&' or '|' or '<' or '>' or '(' or ')' or '{' or '}' or '~'))
            throw new AgentException("Unsupported shell syntax in unquoted agent configuration value");
    }

    internal static string RequireSafeAssetName(string key, string value)
    {
        if (value.Length == 0 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new AgentException($"{key} must be a single safe asset name");

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

    internal static string RequireRepositoryIdentifier(string key, string value)
    {
        if (!Protocol.IsValidRepositoryIdentifier(value))
            throw new AgentException($"{key} must match owner/repository using only letters, digits, '.', '_', and '-'");
        return value;
    }

    internal static string RequireOptionalHttpUri(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        _ = RequireHttpUri(key, value);
        return value;
    }

    internal static string RequireOptionalAttestationUri(string key, string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var uri = RequireHttpUri(key, value);
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new AgentException($"{key} must use HTTPS unless the receiver is loopback");
        return value;
    }

    internal static Uri RequireHttpUri(string key, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new AgentException($"{key} must be an absolute HTTP(S) URL");
        return uri;
    }

    internal static Uri RequireHttpsUri(string key, string value)
    {
        var uri = RequireHttpUri(key, value);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new AgentException($"{key} must use HTTPS");
        return uri;
    }

    internal static Uri RequireLoopbackHttpUri(string key, string value)
    {
        var uri = RequireHttpUri(key, value);
        if (!uri.IsLoopback)
            throw new AgentException($"{key} must target loopback");
        return uri;
    }

    internal static string RequireAttestationSecret(string endpoint, string secretPath)
    {
        if (string.IsNullOrEmpty(endpoint))
        {
            if (string.IsNullOrEmpty(secretPath))
                return string.Empty;
            return RequireAbsolutePath(ENV_HMAC_SECRET_FILE, secretPath);
        }

        if (string.IsNullOrWhiteSpace(secretPath))
            throw new AgentException($"{ENV_HMAC_SECRET_FILE} is required when {ENV_ATTESTATION_ENDPOINT} is configured");

        var fullPath = RequireAbsolutePath(ENV_HMAC_SECRET_FILE, secretPath);
        var bytes = Durability.ReadTrustedRegularFileBytes(
            fullPath,
            ENV_HMAC_SECRET_FILE);

        if (!bytes.Any(value => value is not (9 or 10 or 13 or 32)))
            throw new AgentException($"{ENV_HMAC_SECRET_FILE} must not be empty");

        return fullPath;
    }

    internal static bool RequireBoolean(string key, string value)
        => value switch
        {
            "0" => false,
            CONFIG_BOOLEAN_TRUE => true,
            _ => throw new AgentException($"{key} must be exactly 0 or 1")
        };

    internal static string RequireOptionalAbsolutePath(string key, string value)
        => string.IsNullOrEmpty(value)
            ? string.Empty
            : RequireAbsolutePath(key, value);

    internal static string RequireSafeServiceIdentifier(string key, string value)
    {
        if (value.Length == 0 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
            throw new AgentException($"{key} contains unsafe service identifier characters");
        return value;
    }

    internal static string RequireSafeSystemdUnit(string key, string value)
    {
        if (value.Length == 0 ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '.' or '_' or '-' or '@' or ':')))
            throw new AgentException($"{key} contains unsafe systemd unit characters");
        return value;
    }

    internal static void ValidateDeploymentPathTopology(
        string appDirectory,
        string appBinary,
        string sourceRevisionFile,
        string stateDirectory,
        string hmacSecretFile,
        string smokeScript)
    {
        appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appDirectory));
        appBinary = Path.GetFullPath(appBinary);
        sourceRevisionFile = Path.GetFullPath(sourceRevisionFile);
        stateDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateDirectory));

        if (appDirectory == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(appDirectory)!))
            throw new AgentException($"{ENV_APP_DIR} must not be a filesystem root");
        if (stateDirectory == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(stateDirectory)!))
            throw new AgentException($"{ENV_STATE_DIR} must not be a filesystem root");

        if (PathsIntersect(appDirectory, stateDirectory))
            throw new AgentException($"{ENV_STATE_DIR} must not overlap {ENV_APP_DIR}");
        RejectUnderCheckout(ENV_APP_BIN, appBinary, appDirectory);
        RejectUnderCheckout(ENV_SOURCE_REVISION_FILE, sourceRevisionFile, appDirectory);

        if (IsSameOrDescendant(appBinary, stateDirectory))
            throw new AgentException($"{ENV_APP_BIN} must not be inside {ENV_STATE_DIR}");

        if (!string.IsNullOrEmpty(hmacSecretFile))
            RejectUnderCheckout(ENV_HMAC_SECRET_FILE, hmacSecretFile, appDirectory);

        if (!string.IsNullOrEmpty(smokeScript))
        {
            RejectUnderCheckout(ENV_SMOKE_SCRIPT, smokeScript, appDirectory);
            if (IsSameOrDescendant(smokeScript, stateDirectory))
                throw new AgentException($"{ENV_SMOKE_SCRIPT} must not be inside {ENV_STATE_DIR}");
        }
    }

    private static void RejectUnderCheckout(string key, string path, string appDirectory)
    {
        if (IsSameOrDescendant(path, appDirectory))
            throw new AgentException($"{key} must not be inside {ENV_APP_DIR}");
    }

    private static bool PathsIntersect(string left, string right)
        => IsSameOrDescendant(left, right) || IsSameOrDescendant(right, left);

    private static bool IsSameOrDescendant(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return path == root ||
               path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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

