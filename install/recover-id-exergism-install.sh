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
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"

AGENT="/usr/local/libexec/ec-deployment-agent"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
AGENT_SERVICE_UNIT="/etc/systemd/system/ec-deployment-attestation@.service"
AGENT_TIMER_UNIT="/etc/systemd/system/ec-deployment-attestation@.timer"
ENV_FILE="/etc/ec-deployment-attestation/${SERVICE}.env"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"

INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
INSTALL_TXN_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
INSTALL_RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"

if [[ "${EC_INSTALL_LOCK_HELD:-0}" != 1 ]]; then
  exec 9>"$INSTALL_LOCK"
  if ! flock -n 9; then
    if [[ "$MODE" == "boot" && -d "$INSTALL_TXN_DIR" ]]; then
      # A live installer owns the transaction lock and may intentionally start
      # the guarded resolver/timer while validating the pending generation.
      # Exiting successfully lets that owner proceed; if it dies, the lock is
      # released and the next guarded activation performs real recovery.
      exit 0
    fi
    echo "Another Deployment Attestation installation/recovery is already running." >&2
    exit 1
  fi
fi

[[ -d "$INSTALL_TXN_DIR" ]] || exit 0

read_value() {
  local name="$1"
  [[ -f "${INSTALL_TXN_DIR}/${name}" ]] || {
    echo "Recovery journal is missing ${name}" >&2
    return 1
  }
  cat "${INSTALL_TXN_DIR}/${name}"
}

schema_version="$(read_value schema_version)"
[[ "$schema_version" == 1 ]] || {
  echo "Unsupported installer recovery journal schema: $schema_version" >&2
  exit 1
}

timer_enablement_state="$(read_value timer_enablement_state)"
case "$timer_enablement_state" in
  enabled|enabled-runtime|disabled|not-found) ;;
  *)
    echo "Recovery journal has unsupported timer enablement state: $timer_enablement_state" >&2
    exit 1
    ;;
esac

timer_was_active="$(read_value timer_was_active)"
[[ "$timer_was_active" == 0 || "$timer_was_active" == 1 ]] || {
  echo "Recovery journal has invalid timer_was_active: $timer_was_active" >&2
  exit 1
}

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

restore_artifact() {
  local key="$1" path present backup
  path="$(artifact_path "$key")"
  present="$(read_value "${key}_present")"
  [[ "$present" == 0 || "$present" == 1 ]] || {
    echo "Recovery journal has invalid ${key}_present: $present" >&2
    return 1
  }

  backup="${INSTALL_TXN_DIR}/backups/${key}"
  if [[ "$present" == 1 && ! -e "$backup" && ! -L "$backup" ]]; then
    echo "Recovery backup for $key is missing." >&2
    return 1
  fi

  rm -f -- "$path"
  if [[ "$present" == 1 ]]; then
    cp -a -- "$backup" "$path"
  fi
}

persist_restored_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"

  durable_sync_paths     "$AGENT"     "$SMOKE"     "$AGENT_SERVICE_UNIT"     "$AGENT_TIMER_UNIT"     "$ENV_FILE"     "$FENCE_DROPIN"     /usr/local/libexec     /etc/ec-deployment-attestation     "$FENCE_DROPIN_DIR"     /etc/systemd/system     "$timer_wants"

  durable_sync_ancestor_chain     /usr/local/libexec     /etc/ec-deployment-attestation     "$FENCE_DROPIN_DIR"     "$timer_wants"
}

durable_remove_journal() {
  rm -rf "$INSTALL_RECOVERED_DIR"
  mv "$INSTALL_TXN_DIR" "$INSTALL_RECOVERED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  rm -rf "$INSTALL_RECOVERED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

restore_rc=0

# Normalize whatever the failed installation left behind before restoring the
# previous unit files and enablement semantics. Runtime and persistent
# enablement are separate namespaces and must both be cleared.
systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || true
systemctl disable "$TIMER_UNIT" >/dev/null 2>&1 || true
systemctl --runtime disable "$TIMER_UNIT" >/dev/null 2>&1 || true

for key in agent smoke service_unit timer_unit env fence; do
  restore_artifact "$key" || restore_rc=1
done

persist_restored_generation || restore_rc=1
systemctl daemon-reload || restore_rc=1

case "$MODE:$timer_enablement_state" in
  normal:enabled)
    systemctl enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
    ;;
  normal:enabled-runtime)
    systemctl --runtime enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
    ;;
  normal:disabled|normal:not-found)
    ;;
  boot:enabled)
    systemctl enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
    ;;
  boot:enabled-runtime|boot:disabled|boot:not-found)
    # Runtime-only enablement and manually active disabled units naturally do
    # not survive a reboot. Do not accidentally turn them persistent here.
    ;;
esac

if [[ "$MODE" == "normal" ]]; then
  systemctl restart "$TARGET_UNIT" || restore_rc=1
  systemctl is-active --quiet "$TARGET_UNIT" || restore_rc=1
  curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || restore_rc=1

  if [[ -x "$SMOKE" ]]; then
    EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE" || restore_rc=1
  fi

  if [[ "$timer_was_active" == 1 ]]; then
    systemctl start "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
  else
    systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || true
  fi
else
  # On boot only a persistently enabled timer should be brought back. A
  # runtime-enabled or manually active disabled timer would have disappeared
  # across this reboot even without the failed installation.
  if [[ "$timer_enablement_state" == "enabled" ]]; then
    systemctl --no-block start "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
  fi
fi

persist_restored_generation || restore_rc=1

if (( restore_rc != 0 )); then
  echo "CRITICAL: interrupted Deployment Attestation installation could not be fully restored; recovery journal retained at $INSTALL_TXN_DIR." >&2
  exit 1
fi

durable_remove_journal
