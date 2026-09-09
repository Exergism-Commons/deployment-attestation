#!/usr/bin/env bash
set -Eeuo pipefail

AGENT_VERSION="0.1.0-pre1"
CONFIG_FILE="${EC_ATTESTATION_CONFIG:-/etc/ec-deployment-attestation/service.env}"

log()  { printf '\n==> %s\n' "$*"; }
warn() { printf 'WARN: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ -r "$CONFIG_FILE" ]] || die "Configuration not readable: $CONFIG_FILE"
set -a
# shellcheck disable=SC1090
source "$CONFIG_FILE"
set +a

required=(
  EC_SERVICE
  EC_REPOSITORY
  EC_ENVIRONMENT
  EC_RELEASE_TAG
  EC_RELEASE_ASSET_PATTERN
  EC_APP_DIR
  EC_APP_BIN
  EC_SERVICE_UNIT
  EC_SOURCE_REVISION_FILE
  EC_LOCAL_URL
  EC_PUBLIC_URL
)
for name in "${required[@]}"; do
  [[ -n "${!name:-}" ]] || die "Missing required configuration: $name"
done

EC_HOST_ID="${EC_HOST_ID:-$(hostname -f 2>/dev/null || hostname)}"
EC_GITHUB_DOWNLOAD_BASE="${EC_GITHUB_DOWNLOAD_BASE:-https://github.com/${EC_REPOSITORY}/releases/download/${EC_RELEASE_TAG}}"
EC_ATTESTATION_ENDPOINT="${EC_ATTESTATION_ENDPOINT:-}"
EC_HMAC_SECRET_FILE="${EC_HMAC_SECRET_FILE:-}"
EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"
EC_ROLLBACK_DIR="${EC_ROLLBACK_DIR:-/var/lib/ec-deployment-attestation/${EC_SERVICE}}"
EC_CHECK_PUBLIC="${EC_CHECK_PUBLIC:-1}"

mkdir -p "$EC_ROLLBACK_DIR"

arch() {
  local a
  if command -v dpkg >/dev/null 2>&1; then
    a="$(dpkg --print-architecture)"
  else
    case "$(uname -m)" in
      x86_64) a=amd64 ;;
      aarch64|arm64) a=arm64 ;;
      *) die "Unsupported architecture: $(uname -m)" ;;
    esac
  fi
  case "$a" in
    amd64|arm64) printf '%s\n' "$a" ;;
    *) die "Unsupported architecture: $a" ;;
  esac
}

release_asset() {
  local a
  a="$(arch)"
  printf '%s\n' "${EC_RELEASE_ASSET_PATTERN//\{arch\}/$a}"
}

download() {
  local name="$1" target="$2"
  curl --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 10 -fsSL \
    "${EC_GITHUB_DOWNLOAD_BASE}/${name}" -o "$target"
}

read_current_commit() {
  if [[ -r "$EC_SOURCE_REVISION_FILE" ]]; then
    tr -d '\r\n' < "$EC_SOURCE_REVISION_FILE"
  elif [[ -d "$EC_APP_DIR/.git" ]]; then
    git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true
  fi
}

resolve_expected_commit() {
  local tmp
  tmp="$(mktemp)"
  if ! download SOURCE_COMMIT "$tmp"; then
    rm -f "$tmp"
    return 1
  fi
  local sha
  sha="$(tr -d '\r\n' < "$tmp")"
  rm -f "$tmp"
  [[ "$sha" =~ ^[0-9a-f]{40}$ ]] || return 1
  printf '%s\n' "$sha"
}

run_smoke_script() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  EC_PUBLIC_URL="$EC_PUBLIC_URL" EC_LOCAL_URL="$EC_LOCAL_URL" "$EC_SMOKE_SCRIPT"
}

collect_checks() {
  local expected="$1"
  local deployed="$2"
  local systemd_ok=false local_ok=false public_ok=true revision_ok=false smoke_ok=true

  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd_ok=true
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_ok=true
  if [[ "$EC_CHECK_PUBLIC" == "1" ]]; then
    public_ok=false
    curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_ok=true
  fi
  [[ "$deployed" == "$expected" ]] && revision_ok=true
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    smoke_ok=false
    run_smoke_script >/dev/null 2>&1 && smoke_ok=true
  fi

  python3 - "$systemd_ok" "$local_ok" "$public_ok" "$revision_ok" "$smoke_ok" <<'PY'
import json, sys
names = ["systemd", "local_http", "public_https", "release_revision", "service_smoke"]
vals = [v.lower() == "true" for v in sys.argv[1:]]
print(json.dumps(dict(zip(names, vals)), sort_keys=True, separators=(",", ":")))
PY
}

status_from_checks() {
  python3 - "$1" <<'PY'
import json, sys
checks = json.loads(sys.argv[1])
mandatory = ("systemd", "local_http", "release_revision", "service_smoke")
if not all(checks.get(k, False) for k in mandatory):
    print("unhealthy")
elif not checks.get("public_https", False):
    print("degraded")
else:
    print("healthy")
PY
}

build_attestation() {
  local expected="$1" deployed="$2" checks="$3" status="$4"
  python3 - "$AGENT_VERSION" "$EC_SERVICE" "$EC_REPOSITORY" "$EC_ENVIRONMENT" \
    "$EC_RELEASE_TAG" "$deployed" "$expected" "$status" "$checks" "$EC_HOST_ID" <<'PY'
import datetime, json, sys
agent, service, repo, env, tag, deployed, expected, status, checks, host = sys.argv[1:]
payload = {
    "schema_version": "0.1",
    "service": service,
    "repository": repo,
    "environment": env,
    "release_tag": tag,
    "deployed_commit": deployed,
    "expected_commit": expected,
    "status": status,
    "checks": json.loads(checks),
    "observed_at": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
    "agent_version": agent,
    "host_id": host,
}
print(json.dumps(payload, sort_keys=True, separators=(",", ":")))
PY
}

send_attestation() {
  local body="$1"
  if [[ -z "$EC_ATTESTATION_ENDPOINT" ]]; then
    printf '%s\n' "$body"
    return 0
  fi
  [[ -r "$EC_HMAC_SECRET_FILE" ]] || die "HMAC secret not readable: $EC_HMAC_SECRET_FILE"
  local timestamp signature
  timestamp="$(date +%s)"
  signature="$(python3 - "$EC_HMAC_SECRET_FILE" "$timestamp" "$body" <<'PY'
import hashlib, hmac, pathlib, sys
secret_path, ts, body = sys.argv[1:]
secret = pathlib.Path(secret_path).read_bytes().strip()
message = (ts + "." + body).encode()
print(hmac.new(secret, message, hashlib.sha256).hexdigest())
PY
)"
  curl --retry 3 --retry-all-errors --connect-timeout 10 -fsS \
    -H 'Content-Type: application/json' \
    -H "X-EC-Timestamp: ${timestamp}" \
    -H "X-EC-Signature: sha256=${signature}" \
    --data-binary "$body" \
    "$EC_ATTESTATION_ENDPOINT" >/dev/null
}

attest() {
  local expected deployed checks status body
  if ! expected="$(resolve_expected_commit)"; then
    warn "Could not resolve expected release commit"
    return 2
  fi
  deployed="$(read_current_commit)"
  [[ "$deployed" =~ ^[0-9a-f]{40}$ ]] || die "Current deployed revision is invalid: ${deployed:-missing}"
  checks="$(collect_checks "$expected" "$deployed")"
  status="$(status_from_checks "$checks")"
  body="$(build_attestation "$expected" "$deployed" "$checks" "$status")"
  send_attestation "$body"
  [[ "$status" == "healthy" ]]
}

rollback() {
  local old_commit="$1" backup_bin="$2"
  warn "Rolling back to $old_commit"
  systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
  git -C "$EC_APP_DIR" reset --hard "$old_commit"
  git -C "$EC_APP_DIR" clean -fdx
  install -o root -g root -m 0755 "$backup_bin" "$EC_APP_BIN"
  printf '%s\n' "$old_commit" > "$EC_SOURCE_REVISION_FILE"
  chmod 0644 "$EC_SOURCE_REVISION_FILE"
  systemctl start "$EC_SERVICE_UNIT"
}

activate_release() {
  local expected="$1" asset_path="$2"

  systemctl stop "$EC_SERVICE_UNIT" || return 1
  git -C "$EC_APP_DIR" checkout --detach "refs/tags/${EC_RELEASE_TAG}" || return 1
  git -C "$EC_APP_DIR" reset --hard "refs/tags/${EC_RELEASE_TAG}" || return 1
  git -C "$EC_APP_DIR" clean -fdx || return 1
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD)" == "$expected" ]] || return 1

  install -o root -g root -m 0755 "$asset_path" "${EC_APP_BIN}.new" || return 1
  mv -f "${EC_APP_BIN}.new" "$EC_APP_BIN" || return 1
  systemctl start "$EC_SERVICE_UNIT" || return 1

  local healthy=0
  for _ in {1..30}; do
    if systemctl is-active --quiet "$EC_SERVICE_UNIT" \
       && curl -fsS --max-time 5 "$EC_LOCAL_URL" >/dev/null 2>&1; then
      healthy=1
      break
    fi
    sleep 1
  done
  [[ "$healthy" == "1" ]] || return 1
  run_smoke_script >/dev/null 2>&1 || return 1
}

update_release() {
  local tmpdir expected current asset expected_checksum actual_checksum old_commit backup_bin tag_commit
  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' RETURN

  download SOURCE_COMMIT "$tmpdir/SOURCE_COMMIT"
  expected="$(tr -d '\r\n' < "$tmpdir/SOURCE_COMMIT")"
  [[ "$expected" =~ ^[0-9a-f]{40}$ ]] || die "Release SOURCE_COMMIT is invalid"

  current="$(read_current_commit)"
  if [[ "$current" == "$expected" ]]; then
    log "Already running release source $expected"
    attest || true
    return 0
  fi

  asset="$(release_asset)"
  download SHA256SUMS "$tmpdir/SHA256SUMS"
  download "$asset" "$tmpdir/$asset"
  expected_checksum="$(awk -v a="$asset" '$2 == a {print $1}' "$tmpdir/SHA256SUMS")"
  [[ "$expected_checksum" =~ ^[0-9a-f]{64}$ ]] || die "Missing valid checksum for $asset"
  actual_checksum="$(sha256sum "$tmpdir/$asset" | awk '{print $1}')"
  [[ "$actual_checksum" == "$expected_checksum" ]] || die "Checksum mismatch for $asset"

  [[ -d "$EC_APP_DIR/.git" ]] || die "Application directory is not a Git checkout: $EC_APP_DIR"

  # Fetch and prove the target revision before interrupting the running service.
  log "Fetching release tag $EC_RELEASE_TAG"
  git -C "$EC_APP_DIR" fetch --force --depth 1 origin \
    "+refs/tags/${EC_RELEASE_TAG}:refs/tags/${EC_RELEASE_TAG}"
  tag_commit="$(git -C "$EC_APP_DIR" rev-parse "refs/tags/${EC_RELEASE_TAG}^{commit}")"
  [[ "$tag_commit" == "$expected" ]] || die "Release tag does not equal SOURCE_COMMIT"

  old_commit="$(git -C "$EC_APP_DIR" rev-parse HEAD)"
  [[ "$old_commit" =~ ^[0-9a-f]{40}$ ]] || die "Cannot determine rollback commit"
  backup_bin="$EC_ROLLBACK_DIR/runtime-${old_commit}"
  install -o root -g root -m 0755 "$EC_APP_BIN" "$backup_bin"

  log "Activating release source $expected"
  if ! activate_release "$expected" "$tmpdir/$asset"; then
    rollback "$old_commit" "$backup_bin" || warn "Rollback itself failed; manual intervention required"
    attest || true
    return 1
  fi

  mkdir -p "$(dirname "$EC_SOURCE_REVISION_FILE")"
  printf '%s\n' "$expected" > "$EC_SOURCE_REVISION_FILE"
  chmod 0644 "$EC_SOURCE_REVISION_FILE"
  log "Activated release source $expected"

  # Public reachability is observed after activation. It does not trigger rollback,
  # because DNS/TLS/network failures may be external to an otherwise healthy host.
  attest || true
}

case "${1:-run}" in
  run|update) update_release ;;
  attest|health) attest ;;
  *) die "Usage: $0 [run|update|attest|health]" ;;
esac
