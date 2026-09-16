#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE="id.exergism.org"
APP_USER="idexergism"
APP_DIR="/srv/id.exergism.org"
APP_BIN="/usr/local/bin/idresolver"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"

for command in systemd-run systemctl curl python3 grep jq mktemp; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Validation dependency missing: $command" >&2
    exit 1
  }
done

[[ -x "$APP_BIN" ]] || { echo "Resolver binary is not executable: $APP_BIN" >&2; exit 1; }
[[ -d "$APP_DIR" ]] || { echo "Resolver source tree is missing: $APP_DIR" >&2; exit 1; }
# The validator must also be able to prove a pre-attestation generation during
# first-install rollback, where the installed smoke helper did not yet exist.

port="$(python3 - <<'PY'
import os
print(18000 + (os.getpid() % 1000))
PY
)"
unit="ec-id-install-validation-$BASHPID.service"

cleanup() {
  systemctl stop "$unit" >/dev/null 2>&1 || true
  systemctl reset-failed "$unit" >/dev/null 2>&1 || true
}
trap cleanup EXIT

systemd-run   --quiet   --collect   --unit="$unit"   --property=Type=simple   --property=User="$APP_USER"   --property=Group="$APP_USER"   --property=WorkingDirectory="$APP_DIR"   --property=NoNewPrivileges=yes   --property=PrivateTmp=yes   --property=ProtectSystem=strict   --property=ProtectHome=yes   --property="ReadOnlyPaths=$APP_DIR"   --property="ReadOnlyPaths=$APP_BIN"   --property=KillMode=control-group   --property=RuntimeMaxSec=90s   "$APP_BIN"     -listen "127.0.0.1:$port"     -root "$APP_DIR"     -registry "$APP_DIR/resolver/registry.json"

for _ in {1..30}; do
  if curl -fsS --max-time 2 "http://127.0.0.1:$port/" >/dev/null; then
    break
  fi
  systemctl is-active --quiet "$unit" || {
    echo "Transient resolver validation unit exited before becoming healthy." >&2
    exit 1
  }
  sleep 1
done

curl -fsS --max-time 5 "http://127.0.0.1:$port/" >/dev/null

# Built-in id.exergism.org semantic probes keep rollback validation independent
# of whether the previous generation already had the attestation smoke helper.
tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"; cleanup' EXIT
curl -fsS --max-time 10 -H 'Accept: text/turtle'   "http://127.0.0.1:$port/ontology/commons/0.1-PRE2" -o "$tmpdir/commons.ttl"
grep -Fq 'owl:versionIRI <https://id.exergism.org/ontology/commons/0.1-PRE2>' "$tmpdir/commons.ttl"
curl -fsS --max-time 10 -H 'Accept: text/turtle'   "http://127.0.0.1:$port/ontology/governance/0.1-PRE2" -o "$tmpdir/governance.ttl"
grep -Fq 'owl:versionIRI <https://id.exergism.org/ontology/governance/0.1-PRE2>' "$tmpdir/governance.ttl"
curl -fsS --max-time 10 "http://127.0.0.1:$port/governance/profile/0.1-DRAFT" | jq -e '.operative == false' >/dev/null
curl -fsS --max-time 10 "http://127.0.0.1:$port/catalog/namespaces" | jq -e . >/dev/null
curl -fsS --max-time 10 "http://127.0.0.1:$port/catalog/terms" | jq -e . >/dev/null

if [[ -x "$SMOKE" ]]; then
  EC_LOCAL_URL="http://127.0.0.1:$port" "$SMOKE"
fi
