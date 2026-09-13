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
        var previous = TryRead();
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
        var current = TryRead() ?? new AgentHealthState(
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
        return new AgentHealthState(
            RequireString(root, "agent_version"),
            RequireString(root, "service"),
            RequireString(root, "state"),
            OptionalString(root, "action"),
            OptionalString(root, "cycle_started_at"),
            OptionalString(root, "last_completed_at"),
            OptionalString(root, "last_success_at"),
            OptionalString(root, "last_attestation_at"),
            OptionalBool(root, "last_attestation_delivered"),
            OptionalString(root, "last_error"));
    }

    private AgentHealthState RequireCurrent()
        => TryRead() ?? throw new AgentException("Agent health state disappeared during a cycle");

    private void Write(AgentHealthState state)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SCHEMA_VERSION);
            writer.WriteString("agent_version", state.AgentVersion);
            writer.WriteString("service", state.Service);
            writer.WriteString("state", state.State);
            WriteNullableString(writer, "action", state.Action);
            WriteNullableString(writer, "cycle_started_at", state.CycleStartedAt);
            WriteNullableString(writer, "last_completed_at", state.LastCompletedAt);
            WriteNullableString(writer, "last_success_at", state.LastSuccessAt);
            WriteNullableString(writer, "last_attestation_at", state.LastAttestationAt);
            if (state.LastAttestationDelivered is bool delivered)
                writer.WriteBoolean("last_attestation_delivered", delivered);
            else
                writer.WriteNull("last_attestation_delivered");
            WriteNullableString(writer, "last_error", state.LastError);
            writer.WriteEndObject();
        }

        Durability.AtomicWrite(
            _config.AgentHealthFile,
            buffer.ToArray(),
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static string UtcNowText()
        => DateTimeOffset.UtcNow.ToString(RFC3339_UTC_FORMAT, CultureInfo.InvariantCulture);

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

        string status;
        if (mandatory.Any(key => !checks[key]))
            status = STATUS_UNHEALTHY;
        else if (!transactionClear && state?.State == HEALTH_STATE_RUNNING)
            status = STATUS_DEGRADED;
        else if (!transactionClear)
            status = STATUS_UNHEALTHY;
        else
            status = STATUS_HEALTHY;

        return new AgentSelfHealthReport(status, checks, state);
    }

    internal byte[] WriteJson(AgentSelfHealthReport report)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", SCHEMA_VERSION);
            writer.WriteString("agent_version", AGENT_VERSION);
            writer.WriteString("service", _config.Service);
            writer.WriteString("status", report.Status);
            writer.WriteString("observed_at", DateTimeOffset.UtcNow.ToString(RFC3339_UTC_FORMAT, CultureInfo.InvariantCulture));
            writer.WritePropertyName("checks");
            writer.WriteStartObject();
            foreach (var check in report.Checks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                writer.WriteBoolean(check.Key, check.Value);
            writer.WriteEndObject();
            writer.WritePropertyName("last_cycle");
            if (report.State is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("state", report.State.State);
                WriteNullableString(writer, "action", report.State.Action);
                WriteNullableString(writer, "cycle_started_at", report.State.CycleStartedAt);
                WriteNullableString(writer, "last_completed_at", report.State.LastCompletedAt);
                WriteNullableString(writer, "last_success_at", report.State.LastSuccessAt);
                WriteNullableString(writer, "last_attestation_at", report.State.LastAttestationAt);
                if (report.State.LastAttestationDelivered is bool delivered)
                    writer.WriteBoolean("last_attestation_delivered", delivered);
                else
                    writer.WriteNull("last_attestation_delivered");
                WriteNullableString(writer, "last_error", report.State.LastError);
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
