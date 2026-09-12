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
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
INSTALL_TXN_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
FENCE_BACKUP="${INSTALL_TXN_DIR}/fence.backup"

[[ -d "$INSTALL_TXN_DIR" ]] || exit 0

read_flag() {
  local name="$1" value
  [[ -f "${INSTALL_TXN_DIR}/${name}" ]] || {
    echo "Recovery journal is missing ${name}" >&2
    return 1
  }
  value="$(cat "${INSTALL_TXN_DIR}/${name}")"
  [[ "$value" == 0 || "$value" == 1 ]] || {
    echo "Recovery journal has invalid ${name}: $value" >&2
    return 1
  }
  printf '%s\n' "$value"
}

had_fence_dropin="$(read_flag had_fence_dropin)"
timer_was_enabled="$(read_flag timer_was_enabled)"
timer_was_active="$(read_flag timer_was_active)"

if [[ "$had_fence_dropin" == 1 && ! -e "$FENCE_BACKUP" && ! -L "$FENCE_BACKUP" ]]; then
  echo "Recovery journal says a prior fence existed but its backup is missing." >&2
  exit 1
fi

fsync_restored_fence() {
  python3 - "$FENCE_DROPIN" "$FENCE_DROPIN_DIR" <<'PY'
import os
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
parent = pathlib.Path(sys.argv[2])
if path.exists() and not path.is_symlink():
    fd = os.open(path, os.O_RDONLY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
}

durable_remove_journal() {
  rm -rf "$INSTALL_TXN_DIR"
  python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys

fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
}

restore_rc=0

# Prevent a partially enabled updater from firing while the target configuration
# is being restored. Continue all restoration steps even if one operation fails.
systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
systemctl disable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1

rm -f "$FENCE_DROPIN" || restore_rc=1
if [[ "$had_fence_dropin" == 1 ]]; then
  cp -a -- "$FENCE_BACKUP" "$FENCE_DROPIN" || restore_rc=1
fi
fsync_restored_fence || restore_rc=1
systemctl daemon-reload || restore_rc=1

if [[ "$timer_was_enabled" == 1 ]]; then
  systemctl enable "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
fi

if [[ "$MODE" == "boot" ]]; then
  # This unit runs before both the resolver and its attestation timer. Queue a
  # previously-active timer non-blockingly so it starts only after this recovery
  # unit exits; the resolver itself will start normally with the restored fence.
  if [[ "$timer_was_active" == 1 ]]; then
    systemctl --no-block start "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
  fi
else
  systemctl restart "$TARGET_UNIT" || restore_rc=1
  systemctl is-active --quiet "$TARGET_UNIT" || restore_rc=1
  curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || restore_rc=1
  if [[ -x "$SMOKE" ]]; then
    EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE" || restore_rc=1
  fi

  if [[ "$timer_was_active" == 1 ]]; then
    systemctl start "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
  else
    systemctl stop "$TIMER_UNIT" >/dev/null 2>&1 || restore_rc=1
  fi
fi

if (( restore_rc != 0 )); then
  echo "CRITICAL: interrupted Deployment Attestation installation could not be fully restored; recovery journal retained at $INSTALL_TXN_DIR." >&2
  exit 1
fi

durable_remove_journal
