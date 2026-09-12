#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"
RECOVERY_UNIT="id-exergism-install-recovery.service"

AGENT="/usr/local/libexec/ec-deployment-agent"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
AGENT_SERVICE_UNIT="/etc/systemd/system/ec-deployment-attestation@.service"
AGENT_TIMER_UNIT="/etc/systemd/system/ec-deployment-attestation@.timer"
ENV_FILE="/etc/ec-deployment-attestation/${SERVICE}.env"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"

RECOVERY_HELPER="/usr/local/libexec/ec-deployment-install-recovery"
RECOVERY_UNIT_PATH="/etc/systemd/system/${RECOVERY_UNIT}"
INSTALL_STATE_PARENT="/var/lib/ec-deployment-attestation"
INSTALL_STATE_ROOT="${INSTALL_STATE_PARENT}/install"
INSTALL_TXN_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
INSTALL_COMMITTED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.committed"
INSTALL_RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"

MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"

for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp cat readlink; do
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
        continue
    if p.is_file():
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

# Create every parent used by the transaction and fsync the complete ancestor
# chain. This makes a first-install journal reachable after sudden power loss.
install -d -m 0755 /usr/local/libexec
install -d -m 0755 /etc/ec-deployment-attestation
install -d -m 0700 /etc/ec-deployment-attestation/secrets
install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"
install -d -o root -g root -m 0700 "$INSTALL_STATE_PARENT"
install -d -o root -g root -m 0700 "$INSTALL_STATE_ROOT"
durable_sync_ancestor_chain   /usr/local/libexec   /etc/ec-deployment-attestation/secrets   "$FENCE_DROPIN_DIR"   "$INSTALL_STATE_ROOT"

jq --version >/dev/null
git --version >/dev/null
sha256sum --version >/dev/null
flock --version >/dev/null
grep --version >/dev/null
systemd-run --version >/dev/null

manager_version="$(systemctl show --property=Version --value 2>/dev/null || true)"
systemd_version="$(printf '%s\n' "$manager_version" | sed -n 's/^\([0-9][0-9]*\).*/\1/p')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "Running systemd manager >= 250 is required for ExitType=cgroup smoke containment (found: ${manager_version:-unknown})." >&2
  exit 1
fi
findmnt --version >/dev/null
nsenter --version >/dev/null

# Recovery infrastructure is deliberately outside the application-generation
# transaction. It is inert without a .pending journal and must itself be durable
# before any transactional mutation is allowed.
install -o root -g root -m 0755 "$ROOT/install/recover-id-exergism-install.sh" "$RECOVERY_HELPER"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-install-recovery.service" "$RECOVERY_UNIT_PATH"
systemctl daemon-reload
systemctl enable "$RECOVERY_UNIT" >/dev/null
durable_sync_paths   "$RECOVERY_HELPER"   "$RECOVERY_UNIT_PATH"   /usr/local/libexec   /etc/systemd/system   /etc/systemd/system/multi-user.target.wants
durable_sync_ancestor_chain   /usr/local/libexec   /etc/systemd/system/multi-user.target.wants

if [[ -d "$INSTALL_TXN_DIR" ]]; then
  echo "Recovering interrupted Deployment Attestation installation before continuing." >&2
  EC_INSTALL_LOCK_HELD=1 "$RECOVERY_HELPER" normal
fi

# .committed/.recovered are cleanup remnants only: the atomic disappearance of
# .pending is already the durable decision. They are safe to remove here.
rm -rf "$INSTALL_COMMITTED_DIR" "$INSTALL_RECOVERED_DIR"
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

timer_enablement_state="$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null || true)"
[[ -n "$timer_enablement_state" ]] || timer_enablement_state="not-found"
case "$timer_enablement_state" in
  enabled|enabled-runtime|disabled|not-found) ;;
  *)
    echo "Unsupported pre-install timer enablement state '$timer_enablement_state'; refusing mutation so rollback semantics remain exact." >&2
    exit 1
    ;;
esac

timer_was_active=0
if systemctl is-active --quiet "$TIMER_UNIT" 2>/dev/null; then
  timer_was_active=1
fi

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
  stage="$(mktemp -d "${INSTALL_STATE_ROOT}/.${SERVICE}.pending.XXXXXX")"
  install -d -o root -g root -m 0700 "$stage/backups"

  printf '1\n' > "$stage/schema_version"
  printf '%s\n' "$timer_enablement_state" > "$stage/timer_enablement_state"
  printf '%s\n' "$timer_was_active" > "$stage/timer_was_active"

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

  mv "$stage" "$INSTALL_TXN_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

persist_installed_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"

  durable_sync_paths     "$AGENT"     "$SMOKE"     "$AGENT_SERVICE_UNIT"     "$AGENT_TIMER_UNIT"     "$ENV_FILE"     "$FENCE_DROPIN"     /usr/local/libexec     /etc/ec-deployment-attestation     "$FENCE_DROPIN_DIR"     /etc/systemd/system     "$timer_wants"

  # Directory-entry durability matters for files, symlinks and a newly created
  # wants/ drop-in directory. Sync every relevant ancestor before committing.
  durable_sync_ancestor_chain     /usr/local/libexec     /etc/ec-deployment-attestation     "$FENCE_DROPIN_DIR"     "$timer_wants"
}

commit_install_transaction() {
  rm -rf "$INSTALL_COMMITTED_DIR"
  mv "$INSTALL_TXN_DIR" "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  rm -rf "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

install_complete=0
rollback_install_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$install_complete" == 0 && -d "$INSTALL_TXN_DIR" ]]; then
    echo "Installation failed after transactional mutation; restoring durable previous generation." >&2
    EC_INSTALL_LOCK_HELD=1 "$RECOVERY_HELPER" normal || rc=1
  fi
  exit "$rc"
}

create_install_transaction
trap rollback_install_on_exit EXIT

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" "$AGENT"
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" "$SMOKE"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" "$AGENT_SERVICE_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" "$AGENT_TIMER_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"

if [[ ! -e "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" "$ENV_FILE"
fi

systemctl daemon-reload
systemctl restart "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null
EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"

systemctl enable --now "$TIMER_UNIT"
[[ "$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null || true)" == "enabled" ]] || {
  echo "Timer did not reach persistent enabled state." >&2
  exit 1
}
systemctl is-active --quiet "$TIMER_UNIT"

# The durability barrier precedes the journal commit. After this returns, every
# file and directory entry needed by the installed generation is on stable
# storage, including the persistent timer enablement symlink.
persist_installed_generation

commit_install_transaction
install_complete=1
trap - EXIT

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
