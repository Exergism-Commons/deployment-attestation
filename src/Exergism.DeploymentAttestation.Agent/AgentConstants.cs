namespace Exergism.DeploymentAttestation.Agent;

internal static class AgentConstants
{
    internal const string AGENT_VERSION = "0.2.0-aot-pre2";
    internal const string SCHEMA_VERSION = "0.1";

    internal const string ACTION_RUN = "run";
    internal const string ACTION_UPDATE = "update";
    internal const string ACTION_ATTEST = "attest";
    internal const string ACTION_HEALTH = "health";
    internal const string ACTION_STATUS = "status";
    internal const string ACTION_RECOVER = "recover";
    internal const string ACTION_SELF_TEST = "self-test";

    internal const string PHASE_ACTIVATING = "activating";
    internal const string PHASE_COMMITTED = "committed";
    internal const string PHASE_PENDING = "pending";
    internal const string PHASE_VALIDATED = "validated";
    internal const string PHASE_RECOVERING = "recovering";
    internal const string PHASE_RECOVERED = "recovered";

    internal const string HEALTH_STATE_IDLE = "idle";
    internal const string HEALTH_STATE_RUNNING = "running";
    internal const string HEALTH_STATE_ERROR = "error";

    internal const string STATUS_HEALTHY = "healthy";
    internal const string STATUS_DEGRADED = "degraded";
    internal const string STATUS_UNHEALTHY = "unhealthy";

    internal const string CHECK_SYSTEMD = "systemd";
    internal const string CHECK_LOCAL_HTTP = "local_http";
    internal const string CHECK_PUBLIC_HTTPS = "public_https";
    internal const string CHECK_RELEASE_REVISION = "release_revision";
    internal const string CHECK_SERVICE_SMOKE = "service_smoke";
    internal const string CHECK_ARTIFACT_FENCE = "artifact_fence";
    internal const string CHECK_RUNTIME_PROCESS = "runtime_process";
    internal const string CHECK_SOURCE_TREE = "source_tree";
    internal const string CHECK_STATE_INTEGRITY = "state_integrity";
    internal const string CHECK_RUNTIME_DIGEST = "runtime_digest";
    internal const string CHECK_RUNTIME_PRESENT = "runtime_present";

    internal const string SELF_CHECK_STATE_FILE = "state_file";
    internal const string SELF_CHECK_VERSION = "agent_version";
    internal const string SELF_CHECK_RECENT_SUCCESS = "recent_success";
    internal const string SELF_CHECK_CYCLE_NOT_STUCK = "cycle_not_stuck";
    internal const string SELF_CHECK_TRANSACTION_CLEAR = "transaction_clear";
    internal const string SELF_CHECK_TIMER_ACTIVE = "timer_active";
    internal const string SELF_CHECK_TIMER_ENABLED = "timer_enabled";
    internal const string SELF_CHECK_ATTESTATION_DELIVERY = "attestation_delivery";

    internal const string ENV_ATTESTATION_CONFIG = "EC_ATTESTATION_CONFIG";
    internal const string ENV_AGENT_COORDINATION_LOCK_HELD = "EC_AGENT_COORDINATION_LOCK_HELD";
    internal const string ENV_SERVICE = "EC_SERVICE";
    internal const string ENV_REPOSITORY = "EC_REPOSITORY";
    internal const string ENV_ENVIRONMENT = "EC_ENVIRONMENT";
    internal const string ENV_RELEASE_TAG = "EC_RELEASE_TAG";
    internal const string ENV_APP_DIR = "EC_APP_DIR";
    internal const string ENV_APP_BIN = "EC_APP_BIN";
    internal const string ENV_SERVICE_UNIT = "EC_SERVICE_UNIT";
    internal const string ENV_SOURCE_REVISION_FILE = "EC_SOURCE_REVISION_FILE";
    internal const string ENV_LOCAL_URL = "EC_LOCAL_URL";
    internal const string ENV_PUBLIC_URL = "EC_PUBLIC_URL";
    internal const string ENV_HOST_ID = "EC_HOST_ID";
    internal const string ENV_GITHUB_DOWNLOAD_BASE = "EC_GITHUB_DOWNLOAD_BASE";
    internal const string ENV_RELEASE_MANIFEST = "EC_RELEASE_MANIFEST";
    internal const string ENV_ATTESTATION_ENDPOINT = "EC_ATTESTATION_ENDPOINT";
    internal const string ENV_HMAC_SECRET_FILE = "EC_HMAC_SECRET_FILE";
    internal const string ENV_SMOKE_SCRIPT = "EC_SMOKE_SCRIPT";
    internal const string ENV_SMOKE_TIMEOUT = "EC_SMOKE_TIMEOUT";
    internal const string ENV_DOWNLOAD_TIMEOUT = "EC_DOWNLOAD_TIMEOUT";
    internal const string ENV_CHECK_PUBLIC = "EC_CHECK_PUBLIC";
    internal const string ENV_STATE_DIR = "EC_STATE_DIR";
    internal const string ENV_AGENT_HEALTH_MAX_AGE = "EC_AGENT_HEALTH_MAX_AGE";

    internal const string FILE_CURRENT_STATE = "current-state.json";
    internal const string FILE_TRANSACTION = "transaction.json";
    internal const string FILE_AGENT_HEALTH = "agent-health.json";
    internal const string DIRECTORY_BACKUPS = "backups";
    internal const string FILE_STATE_LOCK = "agent.lock";

    internal const string SYSTEMD_LOAD_STATE = "LoadState";
    internal const string SYSTEMD_ACTIVE_STATE = "ActiveState";
    internal const string SYSTEMD_MAIN_PID = "MainPID";
    internal const string SYSTEMD_CONTROL_GROUP = "ControlGroup";
    internal const string SYSTEMD_STATE_LOADED = "loaded";
    internal const string SYSTEMD_STATE_ACTIVE = "active";
    internal const string SYSTEMD_STATE_INACTIVE = "inactive";
    internal const string SYSTEMD_STATE_FAILED = "failed";

    internal const string COMMAND_SYSTEMCTL = "systemctl";
    internal const string COMMAND_SYSTEMD_RUN = "systemd-run";
    internal const string COMMAND_GIT = "git";
    internal const string COMMAND_STAT = "stat";
    internal const string COMMAND_NSENTER = "nsenter";

    internal const string HEADER_TIMESTAMP = "X-EC-Timestamp";
    internal const string HEADER_SIGNATURE = "X-EC-Signature";
    internal const string HEADER_IDEMPOTENCY_KEY = "Idempotency-Key";

    internal const string JSON_SCHEMA_VERSION = "schema_version";
    internal const string JSON_REPOSITORY = "repository";
    internal const string JSON_RELEASE_TAG = "release_tag";
    internal const string JSON_SOURCE_COMMIT = "source_commit";
    internal const string JSON_GENERATED_AT = "generated_at";
    internal const string JSON_ASSETS = "assets";
    internal const string JSON_NAME = "name";
    internal const string JSON_SHA256 = "sha256";
    internal const string JSON_PHASE = "phase";
    internal const string JSON_BINARY_SHA256 = "binary_sha256";
    internal const string JSON_RELEASE_MANIFEST_SHA256 = "release_manifest_sha256";
    internal const string JSON_OLD_SOURCE_COMMIT = "old_source_commit";
    internal const string JSON_OLD_BINARY_SHA256 = "old_binary_sha256";
    internal const string JSON_OLD_RELEASE_MANIFEST_SHA256 = "old_release_manifest_sha256";
    internal const string JSON_BACKUP_BINARY = "backup_binary";
    internal const string JSON_NEW_SOURCE_COMMIT = "new_source_commit";
    internal const string JSON_NEW_BINARY_SHA256 = "new_binary_sha256";
    internal const string JSON_NEW_RELEASE_MANIFEST_SHA256 = "new_release_manifest_sha256";
    internal const string JSON_OBSERVATION_ID = "observation_id";

    internal const string DEFAULT_CONFIG_PATH = "/etc/ec-deployment-attestation/service.env";
    internal const string INSTALL_TRANSACTION_ROOT = "/var/lib/ec-deployment-attestation/install";
    internal const string RELEASE_MANIFEST_DEFAULT = "DEPLOYMENT_MANIFEST.json";
    internal const string RFC3339_UTC_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
}
