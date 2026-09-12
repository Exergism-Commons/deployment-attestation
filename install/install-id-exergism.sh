#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
AGENT_RUN_UNIT="ec-deployment-attestation@${SERVICE}.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"
RECOVERY_UNIT="id-exergism-install-recovery.service"

AGENT="/usr/local/libexec/ec-deployment-agent"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
VALIDATOR="/usr/local/libexec/ec-id-generation-validator"
AGENT_SERVICE_UNIT="/etc/systemd/system/ec-deployment-attestation@.service"
AGENT_TIMER_UNIT="/etc/systemd/system/ec-deployment-attestation@.timer"
ENV_FILE="/etc/ec-deployment-attestation/${SERVICE}.env"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"

RECOVERY_HELPER="/usr/local/libexec/ec-deployment-install-recovery"
RECOVERY_UNIT_PATH="/etc/systemd/system/${RECOVERY_UNIT}"
TARGET_RECOVERY_INTERLOCK="${FENCE_DROPIN_DIR}/80-ec-deployment-attestation-install-recovery.conf"
AGENT_RECOVERY_DROPIN_DIR="/etc/systemd/system/${AGENT_RUN_UNIT}.d"
AGENT_RECOVERY_INTERLOCK="${AGENT_RECOVERY_DROPIN_DIR}/80-ec-deployment-attestation-install-recovery.conf"
LEGACY_TIMER_RECOVERY_INTERLOCK="/etc/systemd/system/${TIMER_UNIT}.d/80-ec-deployment-attestation-install-recovery.conf"

INSTALL_STATE_PARENT="/var/lib/ec-deployment-attestation"
INSTALL_STATE_ROOT="${INSTALL_STATE_PARENT}/install"
INSTALL_PENDING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
INSTALL_VALIDATED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.validated"
INSTALL_RECOVERING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovering"
INSTALL_RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
INSTALL_COMMITTED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.committed"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"
BOOT_ID_FILE="/proc/sys/kernel/random/boot_id"

MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"

for command in curl git python3 sha256sum systemctl systemd-run systemd-analyze flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp cat sleep; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required dependency not found: $command" >&2
    exit 1
  }
done

exec 9>"$INSTALL_LOCK"
flock -n 9 || {
  echo "Another Deployment Attestation installation/recovery is already running." >&2
  exit 1
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

install -d -m 0755 /usr/local/libexec
install -d -m 0755 /etc/ec-deployment-attestation
install -d -m 0700 /etc/ec-deployment-attestation/secrets
install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"
install -d -o root -g root -m 0755 "$AGENT_RECOVERY_DROPIN_DIR"
install -d -o root -g root -m 0700 "$INSTALL_STATE_PARENT"
install -d -o root -g root -m 0700 "$INSTALL_STATE_ROOT"
durable_sync_ancestor_chain   /usr/local/libexec   /etc/ec-deployment-attestation/secrets   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   "$INSTALL_STATE_ROOT"

manager_version="$(systemctl show --property=Version --value 2>/dev/null || true)"
systemd_version="$(printf '%s\n' "$manager_version" | sed -n 's/^\([0-9][0-9]*\).*/\1/p')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "Running systemd manager >= 250 is required (found: ${manager_version:-unknown})." >&2
  exit 1
fi

# Recovery and isolated validation infrastructure are outside the generation
# transaction. Publish the complete recovery core durably *before* exposing any
# interlock that can make production units depend on it.
install -o root -g root -m 0755 "$ROOT/install/recover-id-exergism-install.sh" "$RECOVERY_HELPER"
install -o root -g root -m 0755 "$ROOT/install/validate-id-exergism-generation.sh" "$VALIDATOR"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-install-recovery.service" "$RECOVERY_UNIT_PATH"

durable_sync_paths   "$RECOVERY_HELPER"   "$VALIDATOR"   "$RECOVERY_UNIT_PATH"   /usr/local/libexec   /etc/systemd/system
durable_sync_ancestor_chain   /usr/local/libexec   /etc/systemd/system

systemctl daemon-reload
systemctl enable "$RECOVERY_UNIT" >/dev/null

# The enablement link is part of the recovery core: make it durable before an
# interlock can require this unit on the next boot.
durable_sync_paths   /etc/systemd/system   /etc/systemd/system/multi-user.target.wants
durable_sync_ancestor_chain   /etc/systemd/system/multi-user.target.wants

# Only now expose resolver/updater dependencies on the already-durable recovery
# core. A power loss can no longer persist an interlock without its prerequisite.
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-install-recovery-interlock.conf" "$TARGET_RECOVERY_INTERLOCK"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-agent-recovery-interlock.conf" "$AGENT_RECOVERY_INTERLOCK"
rm -f "$LEGACY_TIMER_RECOVERY_INTERLOCK"

durable_sync_paths   "$TARGET_RECOVERY_INTERLOCK"   "$AGENT_RECOVERY_INTERLOCK"   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   /etc/systemd/system
durable_sync_ancestor_chain   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   /etc/systemd/system

systemctl daemon-reload

# Any old transaction from a failed invocation must be resolved before a new
# baseline can be captured. The helper recognizes pending/validated/recovering.
if [[ -d "$INSTALL_PENDING_DIR" || -d "$INSTALL_VALIDATED_DIR" || -d "$INSTALL_RECOVERING_DIR" ]]; then
  echo "Recovering interrupted Deployment Attestation installation before continuing." >&2
  EC_INSTALL_LOCK_HELD=1 "$RECOVERY_HELPER" normal
fi
rm -rf "$INSTALL_RECOVERED_DIR" "$INSTALL_COMMITTED_DIR"
durable_sync_paths "$INSTALL_STATE_ROOT"

tmp_manifest="$(mktemp)"
trap 'rm -f "$tmp_manifest"' EXIT
curl --retry 3 --retry-all-errors --connect-timeout 10 -fsSL "$MANIFEST_URL" -o "$tmp_manifest"   || { echo "runtime-main does not publish DEPLOYMENT_MANIFEST.json; refusing to enable updater." >&2; exit 1; }
python3 - "$tmp_manifest" <<'PY'
import json, pathlib, re, sys
m = json.loads(pathlib.Path(sys.argv[1]).read_text())
assert m.get("schema_version") == "0.1"
assert m.get("repository") == "Exergism-Commons/id"
assert m.get("release_tag") == "runtime-main"
assert re.fullmatch(r"[0-9a-f]{40}", m.get("source_commit", ""))
assets = m.get("assets")
assert isinstance(assets, dict)
for arch in ("amd64", "arm64"):
    a = assets.get(arch)
    assert isinstance(a, dict)
    assert re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", a.get("name", ""))
    assert re.fullmatch(r"[0-9a-f]{64}", a.get("sha256", ""))
PY
rm -f "$tmp_manifest"
trap - EXIT

current_boot_id="$(cat "$BOOT_ID_FILE")"
[[ "$current_boot_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  echo "Could not read a valid kernel boot ID." >&2
  exit 1
}

timer_enablement_state="$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null || true)"
[[ -n "$timer_enablement_state" ]] || timer_enablement_state="not-found"
case "$timer_enablement_state" in
  enabled|enabled-runtime|disabled|not-found) ;;
  *)
    echo "Unsupported pre-install timer enablement state '$timer_enablement_state'; refusing mutation." >&2
    exit 1
    ;;
esac

unit_has_processes() {
  local unit="$1" cgroup
  cgroup="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null || true)"
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
    except (FileNotFoundError, PermissionError):
        continue
raise SystemExit(1)
PY
}

unit_is_quiescent() {
  local unit="$1" load active main_pid
  load="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null || true)"
  [[ "$load" == "not-found" || -z "$load" ]] && return 0

  active="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true)"
  main_pid="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null || true)"
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ -z "$main_pid" || "$main_pid" == 0 ]] || return 1
  unit_has_processes "$unit" && return 1
  return 0
}

stop_and_wait_quiescent() {
  local unit="$1" i
  systemctl stop "$unit" >/dev/null 2>&1 || true
  for i in {1..30}; do
    unit_is_quiescent "$unit" && return 0
    sleep 1
  done
  echo "Unit did not become quiescent: $unit" >&2
  return 1
}

timer_was_active=0
systemctl is-active --quiet "$TIMER_UNIT" 2>/dev/null && timer_was_active=1
target_was_active=0
systemctl is-active --quiet "$TARGET_UNIT" 2>/dev/null && target_was_active=1

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

create_install_transaction() {
  local stage key path present
  [[ ! -e "$INSTALL_PENDING_DIR" && ! -e "$INSTALL_VALIDATED_DIR" && ! -e "$INSTALL_RECOVERING_DIR" ]] || return 1

  stage="$(mktemp -d "${INSTALL_STATE_ROOT}/.${SERVICE}.pending.XXXXXX")"
  install -d -o root -g root -m 0700 "$stage/backups"

  printf '2\n' > "$stage/schema_version"
  printf '%s\n' "$current_boot_id" > "$stage/origin_boot_id"
  printf '%s\n' "$timer_enablement_state" > "$stage/timer_enablement_state"
  printf '%s\n' "$timer_was_active" > "$stage/timer_was_active"
  printf '%s\n' "$target_was_active" > "$stage/target_was_active"

  for key in agent smoke service_unit timer_unit env fence; do
    path="$(artifact_path "$key")"
    present=0
    if [[ -e "$path" || -L "$path" ]]; then
      present=1
      cp -a -- "$path" "$stage/backups/$key"
    fi
    printf '%s\n' "$present" > "$stage/${key}_present"
  done

  python3 - "$stage" <<'PY'
import os
import pathlib
import stat
import sys

root = pathlib.Path(sys.argv[1])
for p in root.rglob("*"):
    st = os.lstat(p)
    if stat.S_ISREG(st.st_mode):
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
for p in sorted((q for q in root.rglob("*") if q.is_dir()), key=lambda q: len(q.parts), reverse=True):
    fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY

  mv "$stage" "$INSTALL_PENDING_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

persist_installed_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"
  durable_sync_paths     "$AGENT" "$SMOKE" "$AGENT_SERVICE_UNIT" "$AGENT_TIMER_UNIT"     "$ENV_FILE" "$FENCE_DROPIN"     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR"     /etc/systemd/system "$timer_wants"
  durable_sync_ancestor_chain     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR" "$timer_wants"
}

mark_generation_validated() {
  mv "$INSTALL_PENDING_DIR" "$INSTALL_VALIDATED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

finalize_install_transaction() {
  rm -rf "$INSTALL_COMMITTED_DIR"
  mv "$INSTALL_VALIDATED_DIR" "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  rm -rf "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

restore_prior_runtime_state() {
  local rc=0
  if [[ "$target_was_active" == 1 ]]; then
    systemctl start "$TARGET_UNIT" || rc=1
    systemctl is-active --quiet "$TARGET_UNIT" || rc=1
    curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || rc=1
    if [[ -x "$SMOKE" ]]; then
      EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE" || rc=1
    fi
  else
    systemctl stop "$TARGET_UNIT" >/dev/null 2>&1 || true
  fi
  return "$rc"
}

install_complete=0
rollback_install_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$install_complete" == 0 ]] &&      [[ -d "$INSTALL_PENDING_DIR" || -d "$INSTALL_VALIDATED_DIR" || -d "$INSTALL_RECOVERING_DIR" ]]; then
    echo "Installation failed; restoring the durable previous generation." >&2
    if EC_INSTALL_LOCK_HELD=1 "$RECOVERY_HELPER" normal; then
      restore_prior_runtime_state || rc=1
    else
      # Recovery retained a blocking journal and deliberately left services
      # quiesced. Never bypass that fail-closed state by starting the resolver.
      rc=1
    fi
  fi
  exit "$rc"
}

create_install_transaction
trap rollback_install_on_exit EXIT

# Quiesce every actor that could observe or mutate the generation while it is
# pending. Do not trust is-active alone: activating/deactivating units can still
# own live processes. Require a terminal state, MainPID=0 and an empty cgroup.
stop_and_wait_quiescent "$TIMER_UNIT"
stop_and_wait_quiescent "$AGENT_RUN_UNIT"
stop_and_wait_quiescent "$TARGET_UNIT"

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" "$AGENT"
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" "$SMOKE"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" "$AGENT_SERVICE_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" "$AGENT_TIMER_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"
if [[ ! -e "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" "$ENV_FILE"
fi

systemctl daemon-reload
systemd-analyze verify "$TARGET_UNIT" "$AGENT_RUN_UNIT" "$TIMER_UNIT" >/dev/null

# Validate the pending generation without starting any interlocked production
# unit. A transient resolver gets the same user, source, binary and read-only
# artifact boundaries on an isolated loopback port.
"$VALIDATOR"

# Successful installation intentionally makes the updater persistently enabled,
# but it remains stopped until the production resolver has activated safely.
systemctl enable "$TIMER_UNIT" >/dev/null
[[ "$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null || true)" == "enabled" ]]
! systemctl is-active --quiet "$TIMER_UNIT"

persist_installed_generation

# validated means the on-disk generation is complete, semantically healthy and
# durable. Recovery may allow reads/activation while the installer lock is held,
# because no mixed generation can exist beyond this atomic transition.
mark_generation_validated

systemctl start "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null
EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"

systemctl start "$TIMER_UNIT"
systemctl is-active --quiet "$TIMER_UNIT"

finalize_install_transaction
install_complete=1
trap - EXIT

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
