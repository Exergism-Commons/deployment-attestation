#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

MODE="${1:-normal}"
[[ "$MODE" == "normal" || "$MODE" == "boot" ]] || {
  echo "Usage: $0 [normal|boot]" >&2
  exit 2
}

SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
AGENT_RUN_UNIT="ec-deployment-attestation@${SERVICE}.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"

AGENT="/usr/local/libexec/ec-deployment-agent"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
VALIDATOR="/usr/local/libexec/ec-id-generation-validator"
RECOVERY_FINALIZER="/usr/local/libexec/ec-deployment-install-recovery-finalize"
ARTIFACT_FENCE_AUDITOR="/usr/local/libexec/ec-id-production-artifact-fence"
RECOVERY_FINALIZE_UNIT="id-exergism-install-recovery-finalize.service"
AGENT_SERVICE_UNIT="/etc/systemd/system/ec-deployment-attestation@.service"
AGENT_TIMER_UNIT="/etc/systemd/system/ec-deployment-attestation@.timer"
ENV_FILE="/etc/ec-deployment-attestation/${SERVICE}.env"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"

INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
INSTALL_PENDING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
INSTALL_VALIDATED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.validated"
INSTALL_RECOVERING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovering"
INSTALL_RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
INSTALL_FINALIZED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.finalized"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"
AGENT_COORDINATION_LOCK="/run/lock/ec-deployment-attestation-${SERVICE}.agent.lock"
BOOT_ID_FILE="/proc/sys/kernel/random/boot_id"
TARGET_START_FENCE_ROOT="/run/ec-deployment-attestation"
TARGET_START_FENCE_MARKER="${TARGET_START_FENCE_ROOT}/${SERVICE}.target-start-fence"
TARGET_RUNTIME_MASK="/run/systemd/system/${TARGET_UNIT}"

path_exists_any() {
  [[ -e "$1" || -L "$1" ]]
}

require_real_phase_if_present() {
  local path="$1" owner mode
  if path_exists_any "$path"; then
    [[ -d "$path" && ! -L "$path" ]] || {
      echo "CRITICAL: installer transaction path is not a real directory: $path" >&2
      return 1
    }
    owner="$(stat -c '%u' -- "$path")" || return 1
    mode="$(stat -c '%a' -- "$path")" || return 1
    [[ "$owner" == 0 && "$mode" == 700 ]] || {
      echo "CRITICAL: installer transaction path is not root-owned mode 0700: $path" >&2
      return 1
    }
  fi
}

durable_sync_paths() {
  python3 - "$@" <<'PY'
import os
import pathlib
import stat
import sys

for raw in sys.argv[1:]:
    p = pathlib.Path(raw)
    try:
        st = os.lstat(p)
    except FileNotFoundError:
        continue
    if stat.S_ISREG(st.st_mode):
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    elif stat.S_ISDIR(st.st_mode):
        fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
PY
}

durable_sync_ancestor_chain() {
  python3 - "$@" <<'PY'
import os
import pathlib
import sys

seen = set()
for raw in sys.argv[1:]:
    p = pathlib.Path(raw).resolve(strict=False)
    if not p.exists():
        p = p.parent
    if p.exists() and p.is_file():
        p = p.parent
    while True:
        key = str(p)
        if key not in seen and p.exists() and p.is_dir():
            fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
            seen.add(key)
        if p == p.parent:
            break
        p = p.parent
PY
}

select_transaction() {
  local count=0 path phase
  TXN_PHASE=""
  TXN_DIR=""

  for phase in pending validated recovering recovered; do
    case "$phase" in
      pending) path="$INSTALL_PENDING_DIR" ;;
      validated) path="$INSTALL_VALIDATED_DIR" ;;
      recovering) path="$INSTALL_RECOVERING_DIR" ;;
      recovered) path="$INSTALL_RECOVERED_DIR" ;;
    esac
    require_real_phase_if_present "$path" || return 2
    if [[ -d "$path" ]]; then
      TXN_PHASE="$phase"
      TXN_DIR="$path"
      count=$((count + 1))
    fi
  done

  (( count <= 1 )) || {
    echo "CRITICAL: multiple installer transaction phases exist simultaneously." >&2
    return 2
  }
  (( count == 1 )) || return 1
}

if select_transaction; then
  :
else
  select_rc=$?
  (( select_rc == 1 )) && exit 0
  exit 1
fi

if [[ "${EC_INSTALL_LOCK_HELD:-0}" != 1 ]]; then
  exec 9>"$INSTALL_LOCK"
  if ! flock -n 9; then
    # Only a fully validated+fsynced generation may be activated while the
    # installer owns the lock. pending/recovering always fail closed.
    if [[ "$MODE" == "boot" && ( "$TXN_PHASE" == "validated" || "$TXN_PHASE" == "recovered" ) ]]; then
      exit 0
    fi
    echo "Installation/recovery lock is busy while transaction phase is $TXN_PHASE." >&2
    exit 1
  fi
fi

if [[ "${EC_AGENT_COORDINATION_LOCK_HELD:-0}" != 1 ]]; then
  exec 8>"$AGENT_COORDINATION_LOCK"
  if ! flock -n 8; then
    echo "Agent coordination lock is busy; refusing installer recovery while an agent can mutate deployment artifacts." >&2
    exit 1
  fi
fi
# A .recovered journal means artifact restoration already succeeded and only
# runtime-state completion remains. Never replay byte restoration from this phase.
if [[ "$TXN_PHASE" == "recovered" ]]; then
  if [[ "$MODE" == "normal" ]]; then
    EC_INSTALL_LOCK_HELD=1 EC_AGENT_COORDINATION_LOCK_HELD=1 "$RECOVERY_FINALIZER"
  else
    systemctl --no-block start "$RECOVERY_FINALIZE_UNIT" || {
      echo "CRITICAL: failed to queue recovered-state finalizer." >&2
      exit 1
    }
  fi
  exit 0
fi

# Once recovery owns the transaction, make the rollback intent durable before
# touching any live artifact. This also closes the validated lock-contention
# admission window for concurrent unit starts.
if [[ "$TXN_PHASE" != "recovering" ]]; then
  mv -T -- "$TXN_DIR" "$INSTALL_RECOVERING_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  TXN_PHASE="recovering"
  TXN_DIR="$INSTALL_RECOVERING_DIR"
fi

read_value() {
  local name="$1" path
  path="${TXN_DIR}/${name}"
  [[ -f "$path" && ! -L "$path" ]] || {
    echo "Recovery journal scalar is missing or unsafe: ${name}" >&2
    return 1
  }
  cat "$path"
}

schema_version="$(read_value schema_version)"
[[ "$schema_version" == 2 ]] || {
  echo "Unsupported installer recovery journal schema: $schema_version" >&2
  exit 1
}

origin_boot_id="$(read_value origin_boot_id)"
current_boot_id="$(cat "$BOOT_ID_FILE")"
[[ "$origin_boot_id" =~ ^[0-9a-fA-F-]{36}$ && "$current_boot_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  echo "Recovery journal or kernel exposes an invalid boot ID." >&2
  exit 1
}
if [[ "$origin_boot_id" == "$current_boot_id" ]]; then
  RECOVERY_MODE="normal"
else
  RECOVERY_MODE="boot"
fi

timer_enablement_state="$(read_value timer_enablement_state)"
case "$timer_enablement_state" in
  enabled|enabled-runtime|disabled|not-found) ;;
  *) echo "Unsupported recorded timer enablement state: $timer_enablement_state" >&2; exit 1 ;;
esac

timer_was_active="$(read_value timer_was_active)"
target_was_active="$(read_value target_was_active)"
[[ "$timer_was_active" == 0 || "$timer_was_active" == 1 ]] || exit 1
[[ "$target_was_active" == 0 || "$target_was_active" == 1 ]] || exit 1

artifact_path() {
  case "$1" in
    agent) printf '%s\n' "$AGENT" ;;
    smoke) printf '%s\n' "$SMOKE" ;;
    service_unit) printf '%s\n' "$AGENT_SERVICE_UNIT" ;;
    timer_unit) printf '%s\n' "$AGENT_TIMER_UNIT" ;;
    env) printf '%s\n' "$ENV_FILE" ;;
    fence) printf '%s\n' "$FENCE_DROPIN" ;;
    *) return 1 ;;
  esac
}

restore_artifact() {
  local key="$1" path present backup
  path="$(artifact_path "$key")"
  present="$(read_value "${key}_present")"
  [[ "$present" == 0 || "$present" == 1 ]] || return 1
  backup="${TXN_DIR}/backups/${key}"

  if [[ "$present" == 1 ]]; then
    path_exists_any "$backup" || {
      echo "Recovery backup for $key is missing." >&2
      return 1
    }
    [[ -f "$backup" || -L "$backup" ]] || {
      echo "Recovery backup for $key is not a file/symlink." >&2
      return 1
    }
  fi

  if [[ -d "$path" && ! -L "$path" ]]; then
    echo "Refusing to replace unexpected directory at artifact path: $path" >&2
    return 1
  fi
  rm -f -- "$path" || return 1

  if [[ "$present" == 1 ]]; then
    cp -aT -- "$backup" "$path" || return 1
  fi
}

persist_restored_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"
  durable_sync_paths     "$AGENT" "$SMOKE" "$AGENT_SERVICE_UNIT" "$AGENT_TIMER_UNIT"     "$ENV_FILE" "$FENCE_DROPIN"     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR"     /etc/systemd/system "$timer_wants"
  durable_sync_ancestor_chain     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR" "$timer_wants"
}

enabled_state() {
  local load state rc
  if ! load="$(systemctl show "$TIMER_UNIT" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not query timer LoadState during recovery." >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf 'not-found\n'
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Timer LoadState query returned no value during recovery." >&2
    return 1
  }

  if state="$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null)"; then
    rc=0
  else
    rc=$?
  fi
  case "$state" in
    enabled|enabled-runtime)
      (( rc == 0 )) || return 1
      ;;
    disabled)
      (( rc != 0 )) || return 1
      ;;
    *)
      echo "Could not determine exact timer enablement during recovery (value=$state, rc=$rc)." >&2
      return 1
      ;;
  esac
  printf '%s\n' "$state"
}

expected_enablement_state() {
  if [[ "$RECOVERY_MODE" == "normal" ]]; then
    printf '%s\n' "$timer_enablement_state"
    return
  fi
  case "$timer_enablement_state" in
    enabled) printf 'enabled\n' ;;
    enabled-runtime|disabled) printf 'disabled\n' ;;
    not-found) printf 'not-found\n' ;;
  esac
}

expected_timer_active() {
  if [[ "$RECOVERY_MODE" == "normal" ]]; then
    printf '%s\n' "$timer_was_active"
  elif [[ "$timer_enablement_state" == "enabled" ]]; then
    printf '1\n'
  else
    printf '0\n'
  fi
}

restore_rc=0

ensure_target_start_fence_root() {
  local owner mode
  if path_exists_any "$TARGET_START_FENCE_ROOT"; then
    [[ -d "$TARGET_START_FENCE_ROOT" && ! -L "$TARGET_START_FENCE_ROOT" ]] || return 1
    owner="$(stat -c '%u' -- "$TARGET_START_FENCE_ROOT")" || return 1
    mode="$(stat -c '%a' -- "$TARGET_START_FENCE_ROOT")" || return 1
    [[ "$owner" == 0 && "$mode" == 700 ]]
    return
  fi
  install -d -o root -g root -m 0700 "$TARGET_START_FENCE_ROOT"
}

target_start_fence_active() {
  local load
  [[ -L "$TARGET_RUNTIME_MASK" ]] || return 1
  [[ "$(readlink -- "$TARGET_RUNTIME_MASK")" == "/dev/null" ]] || return 1
  load="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)" || return 1
  [[ "$load" == "masked" ]]
}

owned_target_start_fence_marker_valid() {
  ensure_target_start_fence_root || return 1
  [[ -f "$TARGET_START_FENCE_MARKER" && ! -L "$TARGET_START_FENCE_MARKER" ]] || return 1
  [[ "$(stat -c '%u:%a' -- "$TARGET_START_FENCE_MARKER")" == "0:600" ]] || return 1
  [[ "$(cat -- "$TARGET_START_FENCE_MARKER")" == "$TARGET_UNIT" ]]
}

release_owned_target_start_fence() {
  # Absence of our marker means any existing mask is administrative/external
  # and must never be removed by recovery.
  path_exists_any "$TARGET_START_FENCE_MARKER" || return 0

  owned_target_start_fence_marker_valid || {
    echo "CRITICAL: recovery start-fence marker is invalid; refusing to unmask target." >&2
    return 1
  }

  if path_exists_any "$TARGET_RUNTIME_MASK"; then
    [[ -L "$TARGET_RUNTIME_MASK" && "$(readlink -- "$TARGET_RUNTIME_MASK")" == "/dev/null" ]] || {
      echo "CRITICAL: recovery start-fence marker exists but runtime unit override is not our /dev/null mask." >&2
      return 1
    }
    systemctl unmask --runtime "$TARGET_UNIT" >/dev/null 2>&1 || return 1
  fi

  # Always reload while the trusted marker still exists. This makes a crash
  # after unmask but before daemon-reload/marker cleanup safely resumable.
  systemctl daemon-reload >/dev/null 2>&1 || return 1

  path_exists_any "$TARGET_RUNTIME_MASK" && {
    echo "CRITICAL: recovery-owned runtime start fence still exists after unmask." >&2
    return 1
  }

  rm -f -- "$TARGET_START_FENCE_MARKER" || return 1
  return 0
}

establish_target_start_fence() {
  local marker_tmp
  ensure_target_start_fence_root || return 1
  target_start_fence_active && return 0

  if path_exists_any "$TARGET_RUNTIME_MASK"; then
    echo "CRITICAL: unexpected runtime unit override prevents installing target start fence: $TARGET_RUNTIME_MASK" >&2
    return 1
  fi

  if path_exists_any "$TARGET_START_FENCE_MARKER"; then
    [[ -f "$TARGET_START_FENCE_MARKER" && ! -L "$TARGET_START_FENCE_MARKER" ]] || return 1
    [[ "$(stat -c '%u:%a' -- "$TARGET_START_FENCE_MARKER")" == "0:600" ]] || return 1
  else
    marker_tmp="$(mktemp "$TARGET_START_FENCE_ROOT/.target-start-fence.XXXXXX")" || return 1
    chown root:root "$marker_tmp" || { rm -f -- "$marker_tmp"; return 1; }
    chmod 0600 "$marker_tmp" || { rm -f -- "$marker_tmp"; return 1; }
    printf '%s\n' "$TARGET_UNIT" > "$marker_tmp" || { rm -f -- "$marker_tmp"; return 1; }
    mv -T -- "$marker_tmp" "$TARGET_START_FENCE_MARKER" || { rm -f -- "$marker_tmp"; return 1; }
  fi

  systemctl mask --runtime "$TARGET_UNIT" >/dev/null 2>&1 || return 1
  systemctl daemon-reload >/dev/null 2>&1 || return 1
  target_start_fence_active
}

unit_has_processes() {
  local cgroup="$1"
  [[ -n "$cgroup" ]] || return 1
  python3 - "$cgroup" <<'PY'
import pathlib
import sys

root = pathlib.Path("/sys/fs/cgroup") / sys.argv[1].lstrip("/")
if not root.exists():
    raise SystemExit(1)
for procs in root.rglob("cgroup.procs"):
    try:
        if procs.read_text().strip():
            raise SystemExit(0)
    except FileNotFoundError:
        continue
    except PermissionError:
        raise SystemExit(2)
raise SystemExit(1)
PY
}

unit_is_quiescent() {
  local unit="$1"
  local load active main_pid cgroup cgroup_rc
  local load_after active_after main_pid_after cgroup_after

  if ! load="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine LoadState for $unit" >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    load_after="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)" || return 1
    [[ "$load_after" == "not-found" ]] || return 1
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Could not determine LoadState for $unit" >&2
    return 1
  }

  active="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null)" || return 1
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ "$main_pid" == 0 ]] || return 1

  if unit_has_processes "$cgroup"; then
    return 1
  else
    cgroup_rc=$?
    (( cgroup_rc == 1 )) || return 1
  fi

  load_after="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)" || return 1
  active_after="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid_after="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup_after="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null)" || return 1

  [[ "$load_after" == "$load" ]] || return 1
  [[ "$active_after" == "inactive" || "$active_after" == "failed" ]] || return 1
  [[ "$main_pid_after" == 0 ]] || return 1
  [[ "$cgroup_after" == "$cgroup" ]] || return 1
  return 0
}

fence_and_quiesce_target_after_failure() {
  local attempts=0
  while ! establish_target_start_fence; do
    systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
    attempts=$((attempts + 1))
    if (( attempts % 30 == 0 )); then
      echo "CRITICAL: target start fence is not yet established during recovery failure; retrying while locks remain held." >&2
    fi
    sleep 1
  done

  while true; do
    systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
    if target_start_fence_active && unit_is_quiescent "$TARGET_UNIT" && target_start_fence_active; then
      return 0
    fi
    attempts=$((attempts + 1))
    if (( attempts % 30 == 0 )); then
      echo "CRITICAL: target is still not provably fenced+quiescent after recovery failure; retrying while locks remain held." >&2
    fi
    sleep 1
  done
}

quiesce_unit() {
  local unit="$1" i
  systemctl stop "$unit" >/dev/null 2>&1 || true
  for i in {1..30}; do
    unit_is_quiescent "$unit" && return 0
    sleep 1
  done
  echo "Unit did not become quiescent: $unit" >&2
  return 1
}

quiesce_dependent_unit_for_recovery() {
  local unit="$1"
  # In dependency-triggered boot recovery a dependent start job may already be
  # queued behind this recovery unit. Stopping an already-quiescent unit would
  # replace/cancel that safely waiting job. Preserve it while dependency
  # ordering keeps it unable to start until recovery exits.
  if [[ "$MODE" == "boot" ]] && unit_is_quiescent "$unit"; then
    return 0
  fi
  quiesce_unit "$unit"
}

# Nothing that can execute or mutate the generation may remain live while old
# bytes and systemd policy are being restored. Quiescence is a hard gate:
# never touch an artifact if even one unit can still execute or trigger work.
quiesce_failed=0
quiesce_unit "$TIMER_UNIT" || quiesce_failed=1
quiesce_dependent_unit_for_recovery "$AGENT_RUN_UNIT" || quiesce_failed=1
quiesce_dependent_unit_for_recovery "$TARGET_UNIT" || quiesce_failed=1
if (( quiesce_failed != 0 )); then
  echo "CRITICAL: recovery could not prove all units quiescent; no deployment artifact was modified and journal is retained." >&2
  exit 1
fi

systemctl disable "$TIMER_UNIT" >/dev/null 2>&1 || {
  [[ "$(enabled_state)" == "disabled" || "$(enabled_state)" == "not-found" ]] || restore_rc=1
}
systemctl --runtime disable "$TIMER_UNIT" >/dev/null 2>&1 || {
  [[ "$(enabled_state)" != "enabled-runtime" ]] || restore_rc=1
}

for key in agent smoke service_unit timer_unit env fence; do
  restore_artifact "$key" || restore_rc=1
done

persist_restored_generation || restore_rc=1
systemctl daemon-reload || restore_rc=1

case "$RECOVERY_MODE:$timer_enablement_state" in
  normal:enabled|boot:enabled)
    systemctl enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
    ;;
  normal:enabled-runtime)
    systemctl --runtime enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
    ;;
  normal:disabled|normal:not-found|boot:enabled-runtime|boot:disabled|boot:not-found)
    ;;
esac

expected_enabled="$(expected_enablement_state)"
actual_enabled="$(enabled_state)"
[[ "$actual_enabled" == "$expected_enabled" ]] || {
  echo "Timer enablement restore mismatch: expected=$expected_enabled actual=$actual_enabled" >&2
  restore_rc=1
}

# Keep the timer quiesced until the restored generation has passed transient
# validation and the blocking recovering phase has been retired.
expected_active="$(expected_timer_active)"
quiesce_unit "$TIMER_UNIT" || restore_rc=1

persist_restored_generation || restore_rc=1

# Validate restored bytes without starting the interlocked production resolver.
# This avoids a dependency cycle when recovery itself was pulled in by target.
if (( restore_rc == 0 )); then
  "$VALIDATOR" || restore_rc=1
fi

if (( restore_rc != 0 )); then
  systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || true
  systemctl stop "$AGENT_RUN_UNIT" >/dev/null 2>&1 || true
  fence_and_quiesce_target_after_failure
  echo "CRITICAL: previous generation could not be restored exactly; target is fenced+quiescent and recovery journal is retained." >&2
  exit 1
fi

# A prior failed recovery/finalizer may have left a recovery-owned runtime mask.
# Release only that owned fence after the restored generation is exact and has
# passed transient validation. Administrative masks without our marker remain.
release_owned_target_start_fence || {
  fence_and_quiesce_target_after_failure
  echo "CRITICAL: restored generation is valid but recovery-owned target start fence could not be released; target remains fenced+quiescent and journal is retained." >&2
  exit 1
}

# The restored generation is now exact, durable and transiently healthy.
# Retire the blocking phase atomically, but keep a recovered marker until the
# recorded runtime state has also been restored successfully.
path_exists_any "$INSTALL_RECOVERED_DIR" && {
  echo "CRITICAL: recovered phase appeared while recovery lock is held." >&2
  exit 1
}
mv -T -- "$INSTALL_RECOVERING_DIR" "$INSTALL_RECOVERED_DIR"
durable_sync_paths "$INSTALL_STATE_ROOT"

restored_smoke_healthy() {
  if [[ ! -x "$SMOKE" ]]; then
    return 0
  fi
  EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"
}

if [[ "$RECOVERY_MODE" == "normal" && "$target_was_active" == 1 ]]; then
  if [[ "$MODE" == "normal" ]]; then
    # Direct installer recovery is not executing as the target's prerequisite,
    # so restore the previously-active production resolver synchronously before
    # returning to preflight/network work.
    if ! systemctl start "$TARGET_UNIT" \
       || ! systemctl is-active --quiet "$TARGET_UNIT" \
       || ! curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null \
       || ! restored_smoke_healthy \
       || ! "$ARTIFACT_FENCE_AUDITOR"; then
      fence_and_quiesce_target_after_failure
      echo "CRITICAL: previously active resolver could not be restored healthy after direct recovery; target is fenced+quiescent and recovered marker retained." >&2
      exit 1
    fi
  else
    # Dependency-triggered recovery cannot synchronously start a unit ordered
    # after itself. Queue it and let systemd satisfy the dependency graph.
    systemctl --no-block start "$TARGET_UNIT" >/dev/null 2>&1 || {
      echo "CRITICAL: failed to queue previously active resolver after recovery." >&2
      exit 1
    }
  fi
fi

# Restore timer activation only after the old generation is fully restored and
# validated. This prevents a timer expiry from launching the updater mid-rollback.
if [[ "$expected_active" == 1 ]]; then
  if [[ "$MODE" == "boot" ]]; then
    systemctl --no-block start "$TIMER_UNIT" >/dev/null 2>&1 || {
      echo "CRITICAL: failed to queue restored active timer." >&2
      exit 1
    }
  else
    systemctl start "$TIMER_UNIT" >/dev/null 2>&1 || {
      echo "CRITICAL: failed to restore active timer." >&2
      exit 1
    }
    systemctl is-active --quiet "$TIMER_UNIT" || exit 1
  fi
else
  quiesce_unit "$TIMER_UNIT" || exit 1
fi

# For synchronous/direct recovery prove the exact terminal state with
# systemctl show. Never interpret a DBus/query failure as "inactive".
if [[ "$MODE" == "normal" ]]; then
  if [[ "$expected_active" == 1 ]]; then
    if ! actual_state="$(systemctl show "$TIMER_UNIT" --property=ActiveState --value 2>/dev/null)"; then
      echo "Could not determine timer ActiveState after synchronous recovery." >&2
      exit 1
    fi
    [[ "$actual_state" == "active" ]] || {
      echo "Timer active-state restore mismatch: expected=active actual=${actual_state:-unknown}" >&2
      exit 1
    }
  else
    unit_is_quiescent "$TIMER_UNIT" || {
      echo "Timer did not remain provably quiescent after synchronous recovery." >&2
      exit 1
    }
  fi
fi

if [[ "$MODE" == "normal" ]]; then
  # Synchronous/direct recovery has verified final target/timer states.
  # Retire the actionable .recovered journal with an atomic rename first.
  # A crash before the rename leaves a complete actionable journal; a crash
  # after the durable rename leaves only a non-actionable .finalized tree.
  require_real_phase_if_present "$INSTALL_FINALIZED_DIR" || exit 1
  rm -rf -- "$INSTALL_FINALIZED_DIR"
  mv -T -- "$INSTALL_RECOVERED_DIR" "$INSTALL_FINALIZED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  rm -rf -- "$INSTALL_FINALIZED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
else
  # Dependency/boot recovery used --no-block for at least one possible start.
  # Keep the actionable marker, then queue an independent finalizer that runs
  # after this recovery unit exits and can synchronously verify final states.
  durable_sync_paths "$INSTALL_RECOVERED_DIR" "$INSTALL_STATE_ROOT"
  systemctl --no-block start "$RECOVERY_FINALIZE_UNIT" || {
    echo "CRITICAL: failed to queue recovered-state finalizer." >&2
    exit 1
  }
fi
