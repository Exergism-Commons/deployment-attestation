#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"
MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"

# Keep this in sync with the commands required by the installed agent and the
# id-specific semantic smoke check. A successful installation must never leave
# a timer that can only fail at runtime because a dependency is absent.
for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required dependency not found: $command" >&2
    exit 1
  }
done

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

jq --version >/dev/null
git --version >/dev/null
sha256sum --version >/dev/null
flock --version >/dev/null
grep --version >/dev/null
systemd-run --version >/dev/null
# Query the running manager through systemctl. The systemd-run client package
# can be newer than PID 1 after an upgrade, so its package version is not a
# safe feature gate for ExitType=cgroup.
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

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" \
  /usr/local/libexec/ec-deployment-agent
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" \
  /usr/local/libexec/id.exergism.org-smoke.sh
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" \
  /etc/systemd/system/ec-deployment-attestation@.service
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" \
  /etc/systemd/system/ec-deployment-attestation@.timer

install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"

fence_backup_dir="$(mktemp -d)"
fence_backup="${fence_backup_dir}/fence"
had_fence_dropin=0
# -e is false for a dangling symlink, so test -L as well. cp -a preserves the
# prior object type, symlink target, ownership, mode and timestamps.
if [[ -e "$FENCE_DROPIN" || -L "$FENCE_DROPIN" ]]; then
  cp -a -- "$FENCE_DROPIN" "$fence_backup"
  had_fence_dropin=1
fi

restore_previous_fence() {
  local restore_rc=0
  rm -f "$FENCE_DROPIN" || restore_rc=1
  if [[ "$had_fence_dropin" == 1 ]]; then
    cp -a -- "$fence_backup" "$FENCE_DROPIN" || restore_rc=1
  fi
  systemctl daemon-reload || restore_rc=1
  systemctl restart "$TARGET_UNIT" || restore_rc=1
  systemctl is-active --quiet "$TARGET_UNIT" || restore_rc=1
  curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || restore_rc=1
  if (( restore_rc != 0 )); then
    echo "CRITICAL: failed to restore the previous target-service configuration after fence installation failure." >&2
    return 1
  fi
}

fence_mutated=0
install_complete=0
rollback_fence_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$fence_mutated" == 1 && "$install_complete" == 0 ]]; then
    echo "Installation failed after artifact fence mutation; restoring previous configuration." >&2
    restore_previous_fence || rc=1
  fi
  rm -rf "$fence_backup_dir"
  exit "$rc"
}
trap rollback_fence_on_exit EXIT

# Arm rollback before the first command that can alter the drop-in.
fence_mutated=1
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" "/etc/ec-deployment-attestation/${SERVICE}.env"
fi

systemctl daemon-reload
# Any failure from the first fence mutation through timer activation is covered
# by the EXIT rollback guard.
systemctl restart "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null

systemctl enable --now "ec-deployment-attestation@${SERVICE}.timer"

install_complete=1
trap - EXIT
rm -rf "$fence_backup_dir"

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
