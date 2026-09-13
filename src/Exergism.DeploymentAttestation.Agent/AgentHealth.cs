using System.Globalization;
using System.Text.Json;
using static Exergism.DeploymentAttestation.Agent.AgentConstants;

namespace Exergism.DeploymentAttestation.Agent;

internal sealed record AgentHealthState(
    string AgentVersion,
    string Service,
    string State,
    string? Action,
    string? CycleStartedAt,
    string? LastCompletedAt,
    string? LastSuccessAt,
    string? LastAttestationAt,
    bool? LastAttestationDelivered,
    string? LastError);

internal sealed record AgentSelfHealthReport(
    string Status,
    IReadOnlyDictionary<string, bool> Checks,
    AgentHealthState? State);

internal static class HealthCheckRunner
{
    internal static async Task<bool> RunAsync(Func<Task<bool>> check)
    {
        try
        {
            return await check();
        }
        catch
        {
            return false;
        }
    }
}

internal sealed class AgentHealthStore(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    internal void BeginCycle(AgentAction action)
    {
        AgentHealthState? previous;
        try
        {
            previous = TryRead();
        }
        catch
        {
            previous = null;
        }

        var now = UtcNowText();
        Write(new AgentHealthState(
            AGENT_VERSION,
            _config.Service,
            HEALTH_STATE_RUNNING,
            AgentActionParser.ToWireValue(action),
            now,
            previous?.LastCompletedAt,
            previous?.LastSuccessAt,
            previous?.LastAttestationAt,
            previous?.LastAttestationDelivered,
            null));
    }

    internal void CompleteSuccess()
    {
        var current = RequireCurrent();
        var now = UtcNowText();
        Write(current with
        {
            AgentVersion = AGENT_VERSION,
            State = HEALTH_STATE_IDLE,
            LastCompletedAt = now,
            LastSuccessAt = now,
            LastError = null
        });
    }

    internal void CompleteFailure(string error)
    {
        AgentHealthState? current;
        try
        {
            current = TryRead();
        }
        catch
        {
            current = null;
        }

        current ??= new AgentHealthState(
            AGENT_VERSION,
            _config.Service,
            HEALTH_STATE_ERROR,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

        Write(current with
        {
            AgentVersion = AGENT_VERSION,
            State = HEALTH_STATE_ERROR,
            LastCompletedAt = UtcNowText(),
            LastError = error
        });
    }

    internal void RecordAttestation(bool delivered)
    {
        var current = RequireCurrent();
        Write(current with
        {
            LastAttestationAt = UtcNowText(),
            LastAttestationDelivered = delivered
        });
    }

    internal AgentHealthState? TryRead()
    {
        if (!File.Exists(_config.AgentHealthFile))
            return null;

        using var document = JsonDocument.Parse(File.ReadAllBytes(_config.AgentHealthFile));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new AgentException("Agent health state must be a JSON object");

        EnsureUniqueProperties(root);

        if (RequireString(root, JSON_SCHEMA_VERSION) != SCHEMA_VERSION)
            throw new AgentException("Unsupported agent health schema");
        if (RequireString(root, JSON_SERVICE) != _config.Service)
            throw new AgentException("Agent health service identity mismatch");

        var state = RequireString(root, JSON_HEALTH_STATE);
        if (state is not (HEALTH_STATE_IDLE or HEALTH_STATE_RUNNING or HEALTH_STATE_ERROR))
            throw new AgentException("Invalid agent health lifecycle state");

        return new AgentHealthState(
            RequireString(root, JSON_AGENT_VERSION),
            _config.Service,
            state,
            OptionalString(root, JSON_HEALTH_ACTION),
            OptionalString(root, JSON_HEALTH_CYCLE_STARTED_AT),
            OptionalString(root, JSON_HEALTH_LAST_COMPLETED_AT),
            OptionalString(root, JSON_HEALTH_LAST_SUCCESS_AT),
            OptionalString(root, JSON_HEALTH_LAST_ATTESTATION_AT),
            OptionalBool(root, JSON_HEALTH_LAST_ATTESTATION_DELIVERED),
            OptionalString(root, JSON_HEALTH_LAST_ERROR));
    }

    private AgentHealthState RequireCurrent()
        => TryRead() ?? throw new AgentException("Agent health state disappeared during a cycle");

    private void Write(AgentHealthState state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(JSON_SCHEMA_VERSION, SCHEMA_VERSION);
            writer.WriteString(JSON_AGENT_VERSION, state.AgentVersion);
            writer.WriteString(JSON_SERVICE, state.Service);
            writer.WriteString(JSON_HEALTH_STATE, state.State);
            WriteNullableString(writer, JSON_HEALTH_ACTION, state.Action);
            WriteNullableString(writer, JSON_HEALTH_CYCLE_STARTED_AT, state.CycleStartedAt);
            WriteNullableString(writer, JSON_HEALTH_LAST_COMPLETED_AT, state.LastCompletedAt);
            WriteNullableString(writer, JSON_HEALTH_LAST_SUCCESS_AT, state.LastSuccessAt);
            WriteNullableString(writer, JSON_HEALTH_LAST_ATTESTATION_AT, state.LastAttestationAt);
            if (state.LastAttestationDelivered is bool delivered)
                writer.WriteBoolean(JSON_HEALTH_LAST_ATTESTATION_DELIVERED, delivered);
            else
                writer.WriteNull(JSON_HEALTH_LAST_ATTESTATION_DELIVERED);
            WriteNullableString(writer, JSON_HEALTH_LAST_ERROR, state.LastError);
            writer.WriteEndObject();
        }

        Durability.AtomicWrite(
            _config.AgentHealthFile,
            buffer.ToArray(),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string UtcNowText()
        => DateTimeOffset.UtcNow.ToString(RFC3339_UTC_FORMAT, CultureInfo.InvariantCulture);

    private static void EnsureUniqueProperties(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new AgentException($"Duplicate agent health property: {property.Name}");
        }
    }

    private static string RequireString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            throw new AgentException($"Invalid agent health property: {property}");
        return value.GetString()!;
    }

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new AgentException($"Invalid agent health property: {property}");
        return value.GetString();
    }

    private static bool? OptionalBool(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new AgentException($"Invalid agent health property: {property}");
        return value.GetBoolean();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
            writer.WriteNull(property);
        else
            writer.WriteString(property, value);
    }
}

internal sealed class AgentSelfHealthService(AgentConfig config)
{
    private readonly AgentConfig _config = config;

    internal async Task<AgentSelfHealthReport> EvaluateAsync()
    {
        AgentHealthState? state = null;
        var stateFile = true;
        try
        {
            state = new AgentHealthStore(_config).TryRead();
            stateFile = state is not null;
        }
        catch
        {
            stateFile = false;
        }

        var version = state is not null && state.AgentVersion == AGENT_VERSION;
        var recentSuccess = state is not null && IsRecent(state.LastSuccessAt, _config.AgentHealthMaxAge);
        var cycleNotStuck = state is not null && IsCycleNotStuck(state);
        var transactionClear = !File.Exists(_config.TransactionFile);
        var timerActive = await SystemctlSuccessAsync("is-active", "--quiet", _config.AgentTimerUnit);
        var timerEnabled = await SystemctlSuccessAsync("is-enabled", "--quiet", _config.AgentTimerUnit);
        var attestationDelivery = string.IsNullOrEmpty(_config.AttestationEndpoint) ||
            (state?.LastAttestationDelivered == true &&
             IsRecent(state.LastAttestationAt, _config.AgentHealthMaxAge));

        var checks = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [SELF_CHECK_STATE_FILE] = stateFile,
            [SELF_CHECK_VERSION] = version,
            [SELF_CHECK_RECENT_SUCCESS] = recentSuccess,
            [SELF_CHECK_CYCLE_NOT_STUCK] = cycleNotStuck,
            [SELF_CHECK_TRANSACTION_CLEAR] = transactionClear,
            [SELF_CHECK_TIMER_ACTIVE] = timerActive,
            [SELF_CHECK_TIMER_ENABLED] = timerEnabled,
            [SELF_CHECK_ATTESTATION_DELIVERY] = attestationDelivery
        };

        var status = StatusFromChecks(checks, state);
        return new AgentSelfHealthReport(status, checks, state);
    }

    internal static string StatusFromChecks(
        IReadOnlyDictionary<string, bool> checks,
        AgentHealthState? state)
    {
        var mandatory = new[]
        {
            SELF_CHECK_STATE_FILE,
            SELF_CHECK_VERSION,
            SELF_CHECK_RECENT_SUCCESS,
            SELF_CHECK_CYCLE_NOT_STUCK,
            SELF_CHECK_TIMER_ACTIVE,
            SELF_CHECK_TIMER_ENABLED,
            SELF_CHECK_ATTESTATION_DELIVERY
        };

        if (mandatory.Any(key => !checks.TryGetValue(key, out var value) || !value))
            return STATUS_UNHEALTHY;

        var transactionClear =
            checks.TryGetValue(SELF_CHECK_TRANSACTION_CLEAR, out var clear) && clear;
        if (!transactionClear && state?.State == HEALTH_STATE_RUNNING)
            return STATUS_DEGRADED;
        if (!transactionClear)
            return STATUS_UNHEALTHY;
        return STATUS_HEALTHY;
    }

    internal byte[] WriteJson(AgentSelfHealthReport report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(JSON_SCHEMA_VERSION, SCHEMA_VERSION);
            writer.WriteString(JSON_AGENT_VERSION, AGENT_VERSION);
            writer.WriteString(JSON_SERVICE, _config.Service);
            writer.WriteString(JSON_STATUS, report.Status);
            writer.WriteString(JSON_OBSERVED_AT, DateTimeOffset.UtcNow.ToString(RFC3339_UTC_FORMAT, CultureInfo.InvariantCulture));
            writer.WritePropertyName(JSON_CHECKS);
            writer.WriteStartObject();
            foreach (var check in report.Checks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteBoolean(check.Key, check.Value);
            writer.WriteEndObject();
            writer.WritePropertyName(JSON_HEALTH_LAST_CYCLE);
            if (report.State is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString(JSON_HEALTH_STATE, report.State.State);
                WriteNullableString(writer, JSON_HEALTH_ACTION, report.State.Action);
                WriteNullableString(writer, JSON_HEALTH_CYCLE_STARTED_AT, report.State.CycleStartedAt);
                WriteNullableString(writer, JSON_HEALTH_LAST_COMPLETED_AT, report.State.LastCompletedAt);
                WriteNullableString(writer, JSON_HEALTH_LAST_SUCCESS_AT, report.State.LastSuccessAt);
                WriteNullableString(writer, JSON_HEALTH_LAST_ATTESTATION_AT, report.State.LastAttestationAt);
                if (report.State.LastAttestationDelivered is bool delivered)
                    writer.WriteBoolean(JSON_HEALTH_LAST_ATTESTATION_DELIVERED, delivered);
                else
                    writer.WriteNull(JSON_HEALTH_LAST_ATTESTATION_DELIVERED);
                WriteNullableString(writer, JSON_HEALTH_LAST_ERROR, report.State.LastError);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private bool IsCycleNotStuck(AgentHealthState state)
    {
        if (state.State == HEALTH_STATE_IDLE)
            return true;
        if (state.State != HEALTH_STATE_RUNNING)
            return false;
        return IsRecent(state.CycleStartedAt, _config.AgentHealthMaxAge);
    }

    internal static bool IsRecent(string? value, TimeSpan maxAge, DateTimeOffset? now = null)
    {
        if (value is null ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var timestamp))
            return false;

        var current = now ?? DateTimeOffset.UtcNow;
        var age = current - timestamp;
        return age >= TimeSpan.Zero && age <= maxAge;
    }

    private static Task<bool> SystemctlSuccessAsync(params string[] args)
        => HealthCheckRunner.RunAsync(async () =>
        {
            var result = await ProcessRunner.RunAsync(COMMAND_SYSTEMCTL, args, TimeSpan.FromSeconds(10));
            return result.Success;
        });

    private static void WriteNullableString(Utf8JsonWriter writer, string property, string? value)
    {
        if (value is null)
            writer.WriteNull(property);
        else
            writer.WriteString(property, value);
    }
}
