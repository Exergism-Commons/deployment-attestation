#!/usr/bin/env bash
set -Eeuo pipefail

SERVICE="id.exergism.org"
APP_USER="idexergism"
APP_DIR="/srv/id.exergism.org"
APP_BIN="/usr/local/bin/idresolver"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"

for command in systemd-run systemctl curl python3; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Validation dependency missing: $command" >&2
    exit 1
  }
done

[[ -x "$APP_BIN" ]] || { echo "Resolver binary is not executable: $APP_BIN" >&2; exit 1; }
[[ -d "$APP_DIR" ]] || { echo "Resolver source tree is missing: $APP_DIR" >&2; exit 1; }
[[ -x "$SMOKE" ]] || { echo "Semantic smoke is not executable: $SMOKE" >&2; exit 1; }

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
EC_LOCAL_URL="http://127.0.0.1:$port" "$SMOKE"
