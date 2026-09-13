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
    internal const string SYSTEMD_COMMAND_IS_ACTIVE = "is-active";
    internal const string SYSTEMD_COMMAND_IS_ENABLED = "is-enabled";
    internal const string SYSTEMD_FLAG_QUIET = "--quiet";

    internal const string COMMAND_SYSTEMCTL = "systemctl";
    internal const string COMMAND_SYSTEMD_RUN = "systemd-run";
    internal const string COMMAND_GIT = "git";
    internal const string COMMAND_STAT = "stat";
    internal const string COMMAND_NSENTER = "nsenter";
    internal const string COMMAND_HOSTNAME = "hostname";
    internal const string HOSTNAME_FLAG_FQDN = "-f";
    internal const string CONFIG_BOOLEAN_TRUE = "1";
    internal const string STAT_FLAG_DEREFERENCE_FORMAT = "-Lc";
    internal const string STAT_FORMAT_DEVICE_INODE = "%d:%i";
    internal const string MOUNT_OPTION_READ_ONLY = "ro";
    internal const string MOUNT_OPTION_READ_WRITE = "rw";

    internal const string GIT_ENV_DIR = "GIT_DIR";
    internal const string GIT_ENV_WORK_TREE = "GIT_WORK_TREE";
    internal const string GIT_ENV_COMMON_DIR = "GIT_COMMON_DIR";
    internal const string GIT_ENV_INDEX_FILE = "GIT_INDEX_FILE";
    internal const string GIT_ENV_OBJECT_DIRECTORY = "GIT_OBJECT_DIRECTORY";
    internal const string GIT_ENV_ALTERNATE_OBJECT_DIRECTORIES = "GIT_ALTERNATE_OBJECT_DIRECTORIES";
    internal const string GIT_ENV_CONFIG_COUNT = "GIT_CONFIG_COUNT";
    internal const string GIT_ENV_CONFIG_PARAMETERS = "GIT_CONFIG_PARAMETERS";
    internal const string GIT_ENV_CONFIG_SYSTEM = "GIT_CONFIG_SYSTEM";
    internal const string GIT_ENV_CONFIG_GLOBAL = "GIT_CONFIG_GLOBAL";
    internal const string GIT_ENV_CONFIG_NOSYSTEM = "GIT_CONFIG_NOSYSTEM";
    internal const string GIT_ENV_CEILING_DIRECTORIES = "GIT_CEILING_DIRECTORIES";
    internal const string GIT_ENV_DISCOVERY_ACROSS_FILESYSTEM = "GIT_DISCOVERY_ACROSS_FILESYSTEM";
    internal const string GIT_ENV_CONFIG_KEY_PREFIX = "GIT_CONFIG_KEY_";
    internal const string GIT_ENV_CONFIG_VALUE_PREFIX = "GIT_CONFIG_VALUE_";
    internal const string GIT_CONFIG_CORE_WORKTREE = "core.worktree";

    internal const string GIT_FLAG_DIR = "--git-dir";
    internal const string GIT_FLAG_WORK_TREE = "--work-tree";
    internal const string GIT_FLAG_CHDIR = "-C";
    internal const string GIT_FLAG_CONFIG = "-c";
    internal const string GIT_FLAG_CONFIG_ENV_PREFIX = "--config-env=";
    internal const string GIT_SUBCOMMAND_REV_PARSE = "rev-parse";
    internal const string GIT_SUBCOMMAND_FETCH = "fetch";
    internal const string GIT_SUBCOMMAND_CHECKOUT = "checkout";
    internal const string GIT_SUBCOMMAND_RESET = "reset";
    internal const string GIT_SUBCOMMAND_CLEAN = "clean";
    internal const string GIT_SUBCOMMAND_SUBMODULE = "submodule";
    internal const string GIT_SUBCOMMAND_LS_TREE = "ls-tree";
    internal const string GIT_OBJECT_BLOB = "blob";
    internal const string GIT_OBJECT_COMMIT = "commit";
    internal const string GIT_MODE_GITLINK = "160000";
    internal const string GIT_MODE_SYMLINK = "120000";
    internal const string GIT_MODE_FILE = "100644";
    internal const string GIT_MODE_EXECUTABLE = "100755";
    internal const string GIT_METADATA_NAME = ".git";
    internal const string GIT_OBJECT_FORMAT_SHA1 = "sha1";
    internal const string GIT_OBJECT_FORMAT_SHA256 = "sha256";

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
    internal const string JSON_AGENT_VERSION = "agent_version";
    internal const string JSON_CHECKS = "checks";
    internal const string JSON_DEPLOYED_COMMIT = "deployed_commit";
    internal const string JSON_DEPLOYED_RUNTIME_SHA256 = "deployed_runtime_sha256";
    internal const string JSON_ENVIRONMENT = "environment";
    internal const string JSON_EXPECTED_COMMIT = "expected_commit";
    internal const string JSON_EXPECTED_RUNTIME_SHA256 = "expected_runtime_sha256";
    internal const string JSON_HOST_ID = "host_id";
    internal const string JSON_OBSERVED_AT = "observed_at";
    internal const string JSON_SERVICE = "service";
    internal const string JSON_STATUS = "status";

    internal const string JSON_HEALTH_STATE = "state";
    internal const string JSON_HEALTH_ACTION = "action";
    internal const string JSON_HEALTH_CYCLE_STARTED_AT = "cycle_started_at";
    internal const string JSON_HEALTH_LAST_COMPLETED_AT = "last_completed_at";
    internal const string JSON_HEALTH_LAST_SUCCESS_AT = "last_success_at";
    internal const string JSON_HEALTH_LAST_ATTESTATION_AT = "last_attestation_at";
    internal const string JSON_HEALTH_LAST_ATTESTATION_DELIVERED = "last_attestation_delivered";
    internal const string JSON_HEALTH_LAST_ERROR = "last_error";
    internal const string JSON_HEALTH_LAST_CYCLE = "last_cycle";

    internal const string DEFAULT_CONFIG_PATH = "/etc/ec-deployment-attestation/service.env";
    internal const string INSTALL_TRANSACTION_ROOT = "/var/lib/ec-deployment-attestation/install";
    internal const string RELEASE_MANIFEST_DEFAULT = "DEPLOYMENT_MANIFEST.json";
    internal const string RFC3339_UTC_FORMAT = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
}
