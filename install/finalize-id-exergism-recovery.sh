#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
ARTIFACT_FENCE_AUDITOR="/usr/local/libexec/ec-id-production-artifact-fence"

INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
FINALIZED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.finalized"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"
AGENT_COORDINATION_LOCK="/run/lock/ec-deployment-attestation-${SERVICE}.agent.lock"
BOOT_ID_FILE="/proc/sys/kernel/random/boot_id"
TARGET_START_FENCE_ROOT="/run/ec-deployment-attestation"
TARGET_START_FENCE_MARKER="${TARGET_START_FENCE_ROOT}/${SERVICE}.target-start-fence"
TARGET_RUNTIME_MASK="/run/systemd/system/${TARGET_UNIT}"

path_exists_any() {
  [[ -e "$1" || -L "$1" ]]
}

require_real_dir_if_present() {
  local path="$1" owner mode
  if path_exists_any "$path"; then
    [[ -d "$path" && ! -L "$path" ]] || {
      echo "CRITICAL: recovery phase path is not a real directory: $path" >&2
      return 1
    }
    owner="$(stat -c '%u' -- "$path")" || return 1
    mode="$(stat -c '%a' -- "$path")" || return 1
    [[ "$owner" == 0 && "$mode" == 700 ]] || {
      echo "CRITICAL: recovery phase path is not root-owned mode 0700: $path" >&2
      return 1
    }
  fi
}

if [[ "${EC_INSTALL_LOCK_HELD:-0}" != 1 ]]; then
  exec 9>"$INSTALL_LOCK"
  flock -n 9 || {
    echo "Installer/recovery lock is busy; finalization will retry." >&2
    exit 1
  }
fi
if [[ "${EC_AGENT_COORDINATION_LOCK_HELD:-0}" != 1 ]]; then
  exec 8>"$AGENT_COORDINATION_LOCK"
  flock -n 8 || {
    echo "Agent coordination lock is busy; finalization will retry." >&2
    exit 1
  }
fi

require_real_dir_if_present "$RECOVERED_DIR"
require_real_dir_if_present "$FINALIZED_DIR"
[[ -d "$RECOVERED_DIR" ]] || exit 0

read_value() {
  local name="$1" path
  path="${RECOVERED_DIR}/${name}"
  [[ -f "$path" && ! -L "$path" ]] || {
    echo "Recovered journal scalar is missing or unsafe: ${name}" >&2
    return 1
  }
  cat "$path"
}

schema_version="$(read_value schema_version)"
[[ "$schema_version" == 2 ]] || {
  echo "Unsupported recovered journal schema: $schema_version" >&2
  exit 1
}

origin_boot_id="$(read_value origin_boot_id)"
current_boot_id="$(cat "$BOOT_ID_FILE")"
[[ "$origin_boot_id" =~ ^[0-9a-fA-F-]{36}$ && "$current_boot_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  echo "Recovered journal or kernel exposes an invalid boot ID." >&2
  exit 1
}
if [[ "$origin_boot_id" == "$current_boot_id" ]]; then
  RECOVERY_MODE="normal"
else
  RECOVERY_MODE="boot"
fi

timer_enablement_state="$(read_value timer_enablement_state)"
timer_was_active="$(read_value timer_was_active)"
target_was_active="$(read_value target_was_active)"
case "$timer_enablement_state" in
  enabled|enabled-runtime|disabled|not-found) ;;
  *) echo "Unsupported timer enablement state: $timer_enablement_state" >&2; exit 1 ;;
esac
[[ "$timer_was_active" == 0 || "$timer_was_active" == 1 ]] || exit 1
[[ "$target_was_active" == 0 || "$target_was_active" == 1 ]] || exit 1

enabled_state() {
  local load state rc
  if ! load="$(systemctl show "$TIMER_UNIT" --property=LoadState --value 2>/dev/null)"; then
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf 'not-found\n'
    return 0
  fi
  [[ -n "$load" ]] || return 1

  if state="$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null)"; then
    rc=0
  else
    rc=$?
  fi
  case "$state" in
    enabled|enabled-runtime) (( rc == 0 )) || return 1 ;;
    disabled) (( rc != 0 )) || return 1 ;;
    *) return 1 ;;
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

timer_active_state() {
  local load active
  if ! load="$(systemctl show "$TIMER_UNIT" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine timer LoadState while finalizing recovery." >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf 'not-found\n'
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Timer LoadState query returned no value while finalizing recovery." >&2
    return 1
  }

  if ! active="$(systemctl show "$TIMER_UNIT" --property=ActiveState --value 2>/dev/null)"; then
    echo "Could not determine timer ActiveState while finalizing recovery." >&2
    return 1
  fi
  [[ -n "$active" ]] || {
    echo "Timer ActiveState query returned no value while finalizing recovery." >&2
    return 1
  }
  printf '%s\n' "$active"
}

wait_timer_quiescent() {
  local i state
  systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || true
  for i in {1..30}; do
    state="$(timer_active_state)" || {
      sleep 1
      continue
    }
    case "$state" in
      inactive|failed|not-found) return 0 ;;
      activating|deactivating|reloading|active) ;;
      *)
        echo "Unexpected timer ActiveState while finalizing recovery: $state" >&2
        return 1
        ;;
    esac
    sleep 1
  done
  echo "Timer did not reach a provably quiescent terminal state." >&2
  return 1
}

wait_timer_active() {
  local i state
  systemctl start "$TIMER_UNIT" || return 1
  for i in {1..30}; do
    state="$(timer_active_state)" || {
      sleep 1
      continue
    }
    [[ "$state" == "active" ]] && return 0
    case "$state" in
      activating|reloading) ;;
      inactive|failed|deactivating|not-found)
        echo "Timer failed to become active while finalizing recovery: $state" >&2
        return 1
        ;;
      *)
        echo "Unexpected timer ActiveState while finalizing recovery: $state" >&2
        return 1
        ;;
    esac
    sleep 1
  done
  echo "Timer did not become provably active." >&2
  return 1
}

expected_enabled="$(expected_enablement_state)"
actual_enabled="$(enabled_state)" || {
  echo "Could not determine timer enablement while finalizing recovery." >&2
  exit 1
}
[[ "$actual_enabled" == "$expected_enabled" ]] || {
  echo "Timer enablement mismatch during recovery finalization: expected=$expected_enabled actual=$actual_enabled" >&2
  exit 1
}

target_quiescent() {
  local load active main_pid cgroup scan_rc
  local load_after active_after main_pid_after cgroup_after

  if ! load="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)"; then
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    load_after="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)" || return 1
    [[ "$load_after" == "not-found" ]] || return 1
    return 0
  fi
  [[ -n "$load" ]] || return 1

  active="$(systemctl show "$TARGET_UNIT" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup="$(systemctl show "$TARGET_UNIT" --property=ControlGroup --value 2>/dev/null)" || return 1
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ "$main_pid" == 0 ]] || return 1

  if [[ -n "$cgroup" ]]; then
    if python3 - "$cgroup" <<'PY'
import pathlib, sys
root = pathlib.Path("/sys/fs/cgroup") / sys.argv[1].lstrip("/")
if not root.exists():
    raise SystemExit(0)
for procs in root.rglob("cgroup.procs"):
    try:
        if procs.read_text().strip():
            raise SystemExit(1)
    except FileNotFoundError:
        continue
    except PermissionError:
        raise SystemExit(2)
raise SystemExit(0)
PY
    then
      scan_rc=0
    else
      scan_rc=$?
    fi
    (( scan_rc == 0 )) || return 1
  fi

  # The process scan and systemd properties are sampled separately. Re-read
  # terminal state, PID and cgroup before accepting the target as quiescent.
  load_after="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)" || return 1
  active_after="$(systemctl show "$TARGET_UNIT" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid_after="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup_after="$(systemctl show "$TARGET_UNIT" --property=ControlGroup --value 2>/dev/null)" || return 1

  [[ "$load_after" == "$load" ]] || return 1
  [[ "$active_after" == "inactive" || "$active_after" == "failed" ]] || return 1
  [[ "$main_pid_after" == 0 ]] || return 1
  [[ "$cgroup_after" == "$cgroup" ]] || return 1
  return 0
}

ensure_target_start_fence_root() {
  local owner mode
  if path_exists_any "$TARGET_START_FENCE_ROOT"; then
    [[ -d "$TARGET_START_FENCE_ROOT" && ! -L "$TARGET_START_FENCE_ROOT" ]] || {
      echo "CRITICAL: target start-fence root is not a real directory." >&2
      return 1
    }
    owner="$(stat -c '%u' -- "$TARGET_START_FENCE_ROOT")" || return 1
    mode="$(stat -c '%a' -- "$TARGET_START_FENCE_ROOT")" || return 1
    [[ "$owner" == 0 && "$mode" == 700 ]] || {
      echo "CRITICAL: target start-fence root is not root-owned mode 0700." >&2
      return 1
    }
    return 0
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

establish_target_start_fence() {
  local marker_tmp
  ensure_target_start_fence_root || return 1

  # Respect any already-active runtime/admin mask as an effective start fence.
  if target_start_fence_active; then
    return 0
  fi

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

target_active_state() {
  local load active
  if ! load="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine target LoadState while finalizing recovery." >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf 'not-found\n'
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Target LoadState query returned no value while finalizing recovery." >&2
    return 1
  }

  if ! active="$(systemctl show "$TARGET_UNIT" --property=ActiveState --value 2>/dev/null)"; then
    echo "Could not determine target ActiveState while finalizing recovery." >&2
    return 1
  fi
  [[ -n "$active" ]] || {
    echo "Target ActiveState query returned no value while finalizing recovery." >&2
    return 1
  }
  printf '%s\n' "$active"
}

quiesce_target_after_failed_validation() {
  local attempts=0
  while ! establish_target_start_fence; do
    # Never release finalizer/recovery locks without a manager-level fence that
    # prevents queued or future starts from racing the final quiescence sample.
    systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
    attempts=$((attempts + 1))
    if (( attempts % 30 == 0 )); then
      echo "CRITICAL: target start fence is not yet established; retrying while locks remain held." >&2
    fi
    sleep 1
  done

  while true; do
    systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
    if target_start_fence_active && target_quiescent && target_start_fence_active; then
      return 0
    fi

    attempts=$((attempts + 1))
    if (( attempts % 30 == 0 )); then
      echo "CRITICAL: target is still not provably fenced+quiescent after failed recovery validation; retrying while locks remain held." >&2
    fi
    sleep 1
  done
}

wait_target_healthy_active() {
  local i state
  for i in {1..30}; do
    state="$(target_active_state)" || return 1
    case "$state" in
      active) break ;;
      activating|reloading) sleep 1; continue ;;
      inactive|failed|deactivating|not-found)
        echo "Target failed to become active while finalizing recovery: $state" >&2
        return 1
        ;;
      *)
        echo "Unexpected target ActiveState while finalizing recovery: $state" >&2
        return 1
        ;;
    esac
  done
  [[ "$state" == "active" ]] || {
    echo "Target did not become provably active while finalizing recovery." >&2
    return 1
  }
  systemctl is-active --quiet "$TARGET_UNIT" || return 1
  curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || return 1
  if [[ -x "$SMOKE" ]]; then
    EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE" || return 1
  fi
  "$ARTIFACT_FENCE_AUDITOR" || {
    echo "Production resolver failed the live artifact-fence audit during recovery finalization." >&2
    return 1
  }
}

settle_target_after_inactive_baseline() {
  local i state
  # Recovery publishes .recovered only after it has proved the old target
  # quiescent. Any activity visible now is therefore a post-recovery start,
  # possibly the original explicit start job that pulled recovery in. Never
  # issue a stop here: doing so can cancel or reverse that legitimate start.
  for i in {1..30}; do
    target_quiescent && return 0
    state="$(target_active_state)" || return 1
    case "$state" in
      active|activating|reloading)
        wait_target_healthy_active
        return
        ;;
      inactive|failed|deactivating)
        sleep 1
        ;;
      not-found)
        return 0
        ;;
      *)
        echo "Unexpected target ActiveState while settling recovered state: $state" >&2
        return 1
        ;;
    esac
  done
  echo "Target neither became quiescent nor completed a post-recovery activation." >&2
  return 1
}

# Same-boot recovery restores a previously-active target. If the recorded
# baseline was inactive, recovery itself already established the stale-process
# boundary before publishing .recovered; preserve any later explicit start.
finalize_restored_active_target() {
  # Recovery owns this start because the durable baseline says the target was
  # active. Any failed/partial start or health validation must fail closed by
  # proving the target quiescent before finalization returns an error.
  if ! systemctl start "$TARGET_UNIT"; then
    quiesce_target_after_failed_validation
    echo "CRITICAL: restored active target failed to start during recovery finalization; recovered marker retained." >&2
    return 1
  fi

  if ! wait_target_healthy_active; then
    quiesce_target_after_failed_validation
    echo "CRITICAL: restored active target failed recovery finalization health validation; recovered marker retained." >&2
    return 1
  fi
}

if [[ "$RECOVERY_MODE" == "normal" ]]; then
  if [[ "$target_was_active" == 1 ]]; then
    finalize_restored_active_target || exit 1
  else
    # A later explicit start did not originate from recovery. Observe it but do
    # not cancel/reverse it merely because finalization cannot validate it.
    settle_target_after_inactive_baseline
  fi
fi

expected_active="$(expected_timer_active)"
if [[ "$expected_active" == 1 ]]; then
  wait_timer_active
else
  wait_timer_quiescent
fi

# Re-check enablement after runtime-state restoration. A concurrent manager or
# unit-file failure must not retire the only durable recovery marker.
actual_enabled="$(enabled_state)" || {
  echo "Could not re-check timer enablement after runtime restoration." >&2
  exit 1
}
[[ "$actual_enabled" == "$expected_enabled" ]] || {
  echo "Timer enablement changed during recovery finalization: expected=$expected_enabled actual=$actual_enabled" >&2
  exit 1
}

# Atomic disappearance of .recovered is the completion commit point.
rm -rf -- "$FINALIZED_DIR"
mv -T -- "$RECOVERED_DIR" "$FINALIZED_DIR"
python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys
fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
rm -rf -- "$FINALIZED_DIR"
python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys
fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
