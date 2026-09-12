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
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"
MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
RECOVERY_HELPER="/usr/local/libexec/ec-deployment-install-recovery"
INSTALL_STATE_ROOT="/var/lib/ec-deployment-attestation/install"
INSTALL_TXN_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"

# Keep this in sync with the commands required by the installed agent, recovery
# helper and id-specific semantic smoke check. Fail before mutating the host.
for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp cat; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required dependency not found: $command" >&2
    exit 1
  }
done

jq --version >/dev/null
git --version >/dev/null
sha256sum --version >/dev/null
flock --version >/dev/null
grep --version >/dev/null
systemd-run --version >/dev/null

# Query the running manager through systemctl. The systemd-run client package
# can be newer than PID 1 after an upgrade, so its package version is not a safe
# feature gate for ExitType=cgroup.
manager_version="$(systemctl show --property=Version --value 2>/dev/null || true)"
systemd_version="$(printf '%s\n' "$manager_version" | sed -n 's/^\([0-9][0-9]*\).*/\1/p')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "Running systemd manager >= 250 is required for ExitType=cgroup smoke containment (found: ${manager_version:-unknown})." >&2
  exit 1
fi
findmnt --version >/dev/null
nsenter --version >/dev/null

install -d -m 0755 /usr/local/libexec
install -d -m 0755 /etc/ec-deployment-attestation
install -d -m 0700 /etc/ec-deployment-attestation/secrets
install -d -o root -g root -m 0700 /var/lib/ec-deployment-attestation
install -d -o root -g root -m 0700 "$INSTALL_STATE_ROOT"

# Install and enable crash recovery before any fence mutation can happen.
# A pending durable journal is recovered on the next installer invocation and,
# independently, at boot before the resolver or updater timer can start.
install -o root -g root -m 0755 "$ROOT/install/recover-id-exergism-install.sh" "$RECOVERY_HELPER"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-install-recovery.service" \
  "/etc/systemd/system/${RECOVERY_UNIT}"
systemctl daemon-reload
systemctl enable "$RECOVERY_UNIT" >/dev/null

# Make the boot recovery path itself durable before a transaction may be
# published. This covers the helper, unit file, enablement symlink and parents.
python3 - "$RECOVERY_HELPER" "/etc/systemd/system/${RECOVERY_UNIT}" \
  /usr/local/libexec /etc/systemd/system /etc/systemd/system/multi-user.target.wants <<'PY'
import os
import pathlib
import sys

for raw in sys.argv[1:]:
    p = pathlib.Path(raw)
    if p.exists() and p.is_file() and not p.is_symlink():
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    if p.exists() and p.is_dir():
        fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
PY

if [[ -d "$INSTALL_TXN_DIR" ]]; then
  echo "Recovering interrupted Deployment Attestation installation before continuing." >&2
  "$RECOVERY_HELPER" normal
fi

# The updater is unsafe to enable against the legacy rolling release contract.
# Refuse installation until id/runtime-main publishes the atomic manifest that
# binds source_commit and architecture-specific runtime digests.
tmp_manifest="$(mktemp)"
trap 'rm -f "$tmp_manifest"' EXIT
curl --retry 3 --retry-all-errors --connect-timeout 10 -fsSL "$MANIFEST_URL" -o "$tmp_manifest" \
  || { echo "runtime-main does not publish DEPLOYMENT_MANIFEST.json; refusing to enable updater." >&2; exit 1; }
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

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" \
  /usr/local/libexec/ec-deployment-agent
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" "$SMOKE"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" \
  /etc/systemd/system/ec-deployment-attestation@.service
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" \
  /etc/systemd/system/ec-deployment-attestation@.timer
install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"
systemctl daemon-reload

create_install_transaction() {
  local stage had_fence_dropin=0 timer_was_enabled=0 timer_was_active=0

  [[ ! -e "$INSTALL_TXN_DIR" ]] || {
    echo "Refusing to overwrite an existing installer recovery journal: $INSTALL_TXN_DIR" >&2
    return 1
  }

  if [[ -e "$FENCE_DROPIN" || -L "$FENCE_DROPIN" ]]; then
    had_fence_dropin=1
  fi
  if systemctl is-enabled --quiet "$TIMER_UNIT" 2>/dev/null; then
    timer_was_enabled=1
  fi
  if systemctl is-active --quiet "$TIMER_UNIT" 2>/dev/null; then
    timer_was_active=1
  fi

  stage="$(mktemp -d "${INSTALL_STATE_ROOT}/.${SERVICE}.pending.XXXXXX")"
  printf '%s\n' "$had_fence_dropin" > "${stage}/had_fence_dropin"
  printf '%s\n' "$timer_was_enabled" > "${stage}/timer_was_enabled"
  printf '%s\n' "$timer_was_active" > "${stage}/timer_was_active"
  if [[ "$had_fence_dropin" == 1 ]]; then
    cp -a -- "$FENCE_DROPIN" "${stage}/fence.backup"
  fi

  # Fsync every regular journal payload plus the staging directory before the
  # atomic rename publishes the rollback marker.
  python3 - "$stage" <<'PY'
import os
import pathlib
import sys

root = pathlib.Path(sys.argv[1])
for p in root.iterdir():
    if p.is_file() and not p.is_symlink():
        fd = os.open(p, os.O_RDONLY)
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

commit_install_transaction() {
  local committed="${INSTALL_TXN_DIR}.committed"
  rm -rf "$committed"
  # Atomic rename is the commit point. Once .pending disappears, boot recovery
  # must not roll back the already validated fence/timer generation.
  mv "$INSTALL_TXN_DIR" "$committed"
  python3 - "$INSTALL_STATE_ROOT" <<'PY'
import os
import sys

fd = os.open(sys.argv[1], os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY
  rm -rf "$committed"
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

install_complete=0
rollback_install_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$install_complete" == 0 && -d "$INSTALL_TXN_DIR" ]]; then
    echo "Installation failed after transactional mutation; restoring durable previous state." >&2
    "$RECOVERY_HELPER" normal || rc=1
  fi
  exit "$rc"
}

create_install_transaction
trap rollback_install_on_exit EXIT

# The durable journal is now the commit predecessor. From this point onward,
# SIGKILL/reboot is recovered by the boot unit; ordinary failures use the EXIT
# handler above.
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" \
    "/etc/ec-deployment-attestation/${SERVICE}.env"
fi

systemctl daemon-reload
systemctl restart "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null

# The agent treats this semantic smoke as mandatory, so the installer must not
# commit a fence that passes only the root endpoint.
EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"

# Preserve the pre-install timer state in the journal until *both* enablement and
# activation succeed. If --now partially succeeds, rollback disables/stops it
# and restores the exact prior enabled/active booleans.
systemctl enable --now "$TIMER_UNIT"
systemctl is-enabled --quiet "$TIMER_UNIT"
systemctl is-active --quiet "$TIMER_UNIT"

# Removing the durable journal is the installation commit point. A crash before
# this removal rolls back on boot; a crash after it leaves a fully validated
# fence and active updater.
commit_install_transaction
install_complete=1
trap - EXIT

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
