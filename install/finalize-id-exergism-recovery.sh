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

INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
FINALIZED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.finalized"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"
AGENT_COORDINATION_LOCK="/run/lock/ec-deployment-attestation-${SERVICE}.agent.lock"
BOOT_ID_FILE="/proc/sys/kernel/random/boot_id"

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

[[ -d "$RECOVERED_DIR" ]] || exit 0

read_value() {
  local name="$1"
  [[ -f "${RECOVERED_DIR}/${name}" ]] || {
    echo "Recovered journal is missing ${name}" >&2
    return 1
  }
  cat "${RECOVERED_DIR}/${name}"
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
  local load active main_pid cgroup
  if ! load="$(systemctl show "$TARGET_UNIT" --property=LoadState --value 2>/dev/null)"; then
    return 1
  fi
  [[ "$load" == "not-found" ]] && return 0
  [[ -n "$load" ]] || return 1
  if ! active="$(systemctl show "$TARGET_UNIT" --property=ActiveState --value 2>/dev/null)"      || ! main_pid="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)"      || ! cgroup="$(systemctl show "$TARGET_UNIT" --property=ControlGroup --value 2>/dev/null)"; then
    return 1
  fi
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ "$main_pid" == 0 ]] || return 1
  [[ -z "$cgroup" ]] && return 0
  python3 - "$cgroup" <<'PY'
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
}

wait_target_quiescent() {
  local i
  systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
  for i in {1..30}; do
    target_quiescent && return 0
    sleep 1
  done
  echo "Target did not become provably quiescent while finalizing recovery." >&2
  return 1
}

# Same-boot recovery preserves the exact pre-install target active state. A
# reboot intentionally does not preserve volatile activity across boots.
if [[ "$RECOVERY_MODE" == "normal" ]]; then
  if [[ "$target_was_active" == 1 ]]; then
    systemctl start "$TARGET_UNIT"
    systemctl is-active --quiet "$TARGET_UNIT"
    curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null
    if [[ -x "$SMOKE" ]]; then
      EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"
    fi
  else
    wait_target_quiescent
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
rm -rf "$FINALIZED_DIR"
mv "$RECOVERED_DIR" "$FINALIZED_DIR"
python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys
fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
rm -rf "$FINALIZED_DIR"
python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys
fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
