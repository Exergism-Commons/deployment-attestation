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
for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter; do
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
systemd_version="$(systemd-run --version | awk 'NR==1 {print $2}')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "systemd >= 250 is required for ExitType=cgroup smoke containment (found: ${systemd_version:-unknown})." >&2
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

fence_backup="$(mktemp)"
had_fence_dropin=0
if [[ -f "$FENCE_DROPIN" ]]; then
  install -o root -g root -m 0600 "$FENCE_DROPIN" "$fence_backup"
  had_fence_dropin=1
fi

restore_previous_fence() {
  local restore_rc=0
  if [[ "$had_fence_dropin" == 1 ]]; then
    install -o root -g root -m 0644 "$fence_backup" "$FENCE_DROPIN" || restore_rc=1
  else
    rm -f "$FENCE_DROPIN" || restore_rc=1
  fi
  systemctl daemon-reload || restore_rc=1
  systemctl restart "$TARGET_UNIT" || restore_rc=1
  systemctl is-active --quiet "$TARGET_UNIT" || restore_rc=1
  if (( restore_rc != 0 )); then
    echo "CRITICAL: failed to restore the previous target-service configuration after fence installation failure." >&2
    return 1
  fi
}

install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" \
    "/etc/ec-deployment-attestation/${SERVICE}.env"
fi

systemctl daemon-reload
# Apply the service-local read-only artifact namespace before the updater can
# accept any running deployment as a stable baseline. If the new fence is
# incompatible with this resolver, restore the exact previous drop-in state and
# bring the old configuration back before failing the installation.
if ! systemctl restart "$TARGET_UNIT" \
   || ! systemctl is-active --quiet "$TARGET_UNIT" \
   || ! curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null; then
  echo "Target service failed after artifact fence installation; restoring previous configuration." >&2
  restore_previous_fence || exit 1
  rm -f "$fence_backup"
  exit 1
fi
rm -f "$fence_backup"

systemctl enable --now "ec-deployment-attestation@${SERVICE}.timer"

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
