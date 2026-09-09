#!/usr/bin/env bash
set -Eeuo pipefail

AGENT_VERSION="0.1.0-pre2"
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

for command in curl git python3 sha256sum systemctl flock; do
  command -v "$command" >/dev/null 2>&1 || die "Required command not found: $command"
done

EC_HOST_ID="${EC_HOST_ID:-$(hostname -f 2>/dev/null || hostname)}"
EC_GITHUB_DOWNLOAD_BASE="${EC_GITHUB_DOWNLOAD_BASE:-https://github.com/${EC_REPOSITORY}/releases/download/${EC_RELEASE_TAG}}"
EC_RELEASE_MANIFEST="${EC_RELEASE_MANIFEST:-DEPLOYMENT_MANIFEST.json}"
EC_ATTESTATION_ENDPOINT="${EC_ATTESTATION_ENDPOINT:-}"
EC_HMAC_SECRET_FILE="${EC_HMAC_SECRET_FILE:-}"
EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"
EC_STATE_DIR="${EC_STATE_DIR:-/var/lib/ec-deployment-attestation/${EC_SERVICE}}"
EC_CHECK_PUBLIC="${EC_CHECK_PUBLIC:-1}"

CURRENT_STATE_FILE="${EC_STATE_DIR}/current-state.json"
TRANSACTION_FILE="${EC_STATE_DIR}/transaction.json"
LOCK_FILE="${EC_STATE_DIR}/agent.lock"
BACKUP_DIR="${EC_STATE_DIR}/backups"

install -d -o root -g root -m 0700 "$EC_STATE_DIR" "$BACKUP_DIR"
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  log "Another deployment-attestation process holds the service lock; exiting"
  exit 0
fi

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

download() {
  local name="$1" target="$2"
  curl --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 10 -fsSL \
    "${EC_GITHUB_DOWNLOAD_BASE}/${name}" -o "$target"
}

atomic_write() {
  local target="$1" content="$2" mode="${3:-0600}"
  python3 - "$target" "$content" "$mode" <<'PY'
import os, pathlib, sys, tempfile
path = pathlib.Path(sys.argv[1])
data = sys.argv[2].encode()
mode = int(sys.argv[3], 8)
path.parent.mkdir(parents=True, exist_ok=True)
fd, tmp = tempfile.mkstemp(prefix=path.name + ".", dir=path.parent)
try:
    os.fchmod(fd, mode)
    with os.fdopen(fd, "wb", closefd=True) as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, path)
    dirfd = os.open(path.parent, os.O_DIRECTORY)
    try:
        os.fsync(dirfd)
    finally:
        os.close(dirfd)
finally:
    try:
        os.unlink(tmp)
    except FileNotFoundError:
        pass
PY
}

durable_remove() {
  local target="$1"
  python3 - "$target" <<'PY'
import os, pathlib, sys
path = pathlib.Path(sys.argv[1])
try:
    path.unlink()
except FileNotFoundError:
    raise SystemExit(0)
dirfd = os.open(path.parent, os.O_DIRECTORY)
try:
    os.fsync(dirfd)
finally:
    os.close(dirfd)
PY
}

file_sha256() {
  sha256sum "$1" | awk '{print $1}'
}

state_field() {
  local file="$1" field="$2"
  python3 - "$file" "$field" <<'PY'
import json, pathlib, sys
obj = json.loads(pathlib.Path(sys.argv[1]).read_text())
value = obj.get(sys.argv[2])
if value is None:
    print("")
elif isinstance(value, bool):
    print("true" if value else "false")
else:
    print(value)
PY
}

write_current_state() {
  local commit="$1" binary_sha="$2" manifest_sha="$3"
  local body
  body="$(python3 - "$commit" "$binary_sha" "$manifest_sha" "$EC_RELEASE_TAG" <<'PY'
import json, sys
commit, binary_sha, manifest_sha, tag = sys.argv[1:]
print(json.dumps({
    "source_commit": commit,
    "binary_sha256": binary_sha,
    "release_manifest_sha256": manifest_sha or None,
    "release_tag": tag,
}, sort_keys=True, separators=(",", ":")))
PY
)" || return 1
  atomic_write "$CURRENT_STATE_FILE" "$body" 0600 || return 1
  atomic_write "$EC_SOURCE_REVISION_FILE" "${commit}\n" 0644 || return 1
}

bootstrap_current_state() {
  [[ -f "$CURRENT_STATE_FILE" ]] && return 0
  local commit head binary_sha
  if [[ -r "$EC_SOURCE_REVISION_FILE" ]]; then
    commit="$(tr -d '\r\n' < "$EC_SOURCE_REVISION_FILE")"
  else
    commit="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  fi
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || die "Cannot bootstrap current deployment revision"
  head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  [[ "$head" == "$commit" ]] || die "Refusing bootstrap: checkout HEAD does not match recorded deployment revision"
  [[ -x "$EC_APP_BIN" ]] || die "Refusing bootstrap: deployed runtime binary missing"
  binary_sha="$(file_sha256 "$EC_APP_BIN")"
  [[ "$binary_sha" =~ ^[0-9a-f]{64}$ ]] || die "Refusing bootstrap: invalid runtime digest"
  write_current_state "$commit" "$binary_sha" "" || die "Failed to bootstrap deployment state"
}

read_current_commit() {
  state_field "$CURRENT_STATE_FILE" source_commit
}

validate_release_manifest() {
  local file="$1" a="$2"
  python3 - "$file" "$EC_REPOSITORY" "$EC_RELEASE_TAG" "$a" <<'PY'
import json, pathlib, re, sys
path, expected_repo, expected_tag, arch = sys.argv[1:]
obj = json.loads(pathlib.Path(path).read_text())
if obj.get("schema_version") != "0.1":
    raise SystemExit("unsupported deployment manifest schema")
if obj.get("repository") != expected_repo:
    raise SystemExit("deployment manifest repository mismatch")
if obj.get("release_tag") != expected_tag:
    raise SystemExit("deployment manifest release tag mismatch")
commit = obj.get("source_commit", "")
if not re.fullmatch(r"[0-9a-f]{40}", commit):
    raise SystemExit("invalid source_commit")
assets = obj.get("assets")
if not isinstance(assets, dict) or arch not in assets or not isinstance(assets[arch], dict):
    raise SystemExit("missing architecture asset")
asset = assets[arch]
name = asset.get("name", "")
digest = asset.get("sha256", "")
if not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name):
    raise SystemExit("invalid asset name")
if not re.fullmatch(r"[0-9a-f]{64}", digest):
    raise SystemExit("invalid asset digest")
print(commit)
print(name)
print(digest)
PY
}

load_release_snapshot() {
  local directory="$1" manifest="$directory/$EC_RELEASE_MANIFEST" a values
  download "$EC_RELEASE_MANIFEST" "$manifest" || return 1
  a="$(arch)"
  values="$(validate_release_manifest "$manifest" "$a")" || return 1
  RELEASE_SOURCE_COMMIT="$(sed -n '1p' <<<"$values")"
  RELEASE_ASSET_NAME="$(sed -n '2p' <<<"$values")"
  RELEASE_ASSET_SHA256="$(sed -n '3p' <<<"$values")"
  RELEASE_MANIFEST_SHA256="$(file_sha256 "$manifest")"
  [[ "$RELEASE_MANIFEST_SHA256" =~ ^[0-9a-f]{64}$ ]] || return 1
}

run_smoke_script() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  EC_PUBLIC_URL="$EC_PUBLIC_URL" EC_LOCAL_URL="$EC_LOCAL_URL" "$EC_SMOKE_SCRIPT"
}

verify_baseline() {
  local commit binary_sha head actual_binary
  commit="$(state_field "$CURRENT_STATE_FILE" source_commit)" || return 1
  binary_sha="$(state_field "$CURRENT_STATE_FILE" binary_sha256)" || return 1
  [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary_sha" =~ ^[0-9a-f]{64}$ ]] || return 1
  head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  actual_binary="$(file_sha256 "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$head" == "$commit" && "$actual_binary" == "$binary_sha" ]]
}

write_transaction() {
  local old_commit="$1" old_binary="$2" old_manifest="$3" backup="$4"
  local new_commit="$5" new_binary="$6" new_manifest="$7" body
  body="$(python3 - "$old_commit" "$old_binary" "$old_manifest" "$backup" \
    "$new_commit" "$new_binary" "$new_manifest" <<'PY'
import json, sys
old_commit, old_binary, old_manifest, backup, new_commit, new_binary, new_manifest = sys.argv[1:]
print(json.dumps({
    "schema_version": "0.1",
    "old_source_commit": old_commit,
    "old_binary_sha256": old_binary,
    "old_release_manifest_sha256": old_manifest or None,
    "backup_binary": backup,
    "new_source_commit": new_commit,
    "new_binary_sha256": new_binary,
    "new_release_manifest_sha256": new_manifest,
}, sort_keys=True, separators=(",", ":")))
PY
)" || return 1
  atomic_write "$TRANSACTION_FILE" "$body" 0600 || return 1
}

rollback_transaction() {
  [[ -f "$TRANSACTION_FILE" ]] || return 0
  local old_commit old_binary old_manifest backup actual_backup
  old_commit="$(state_field "$TRANSACTION_FILE" old_source_commit)" || return 1
  old_binary="$(state_field "$TRANSACTION_FILE" old_binary_sha256)" || return 1
  old_manifest="$(state_field "$TRANSACTION_FILE" old_release_manifest_sha256)" || return 1
  backup="$(state_field "$TRANSACTION_FILE" backup_binary)" || return 1
  [[ "$old_commit" =~ ^[0-9a-f]{40}$ && "$old_binary" =~ ^[0-9a-f]{64}$ ]] || return 1
  [[ "$backup" == "$BACKUP_DIR/"* && -f "$backup" ]] || return 1
  actual_backup="$(file_sha256 "$backup" 2>/dev/null || true)"
  [[ "$actual_backup" == "$old_binary" ]] || return 1

  warn "Recovering deployment transaction to $old_commit"
  if ! systemctl stop "$EC_SERVICE_UNIT"; then return 1; fi
  if systemctl is-active --quiet "$EC_SERVICE_UNIT"; then return 1; fi
  if ! git -C "$EC_APP_DIR" reset --hard "$old_commit"; then return 1; fi
  if ! git -C "$EC_APP_DIR" clean -fdx; then return 1; fi
  if [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" != "$old_commit" ]]; then return 1; fi
  if ! install -o root -g root -m 0755 "$backup" "${EC_APP_BIN}.rollback"; then return 1; fi
  if [[ "$(file_sha256 "${EC_APP_BIN}.rollback" 2>/dev/null || true)" != "$old_binary" ]]; then return 1; fi
  if ! mv -f "${EC_APP_BIN}.rollback" "$EC_APP_BIN"; then return 1; fi
  if ! write_current_state "$old_commit" "$old_binary" "$old_manifest"; then return 1; fi
  if ! systemctl start "$EC_SERVICE_UNIT"; then return 1; fi
  if ! systemctl is-active --quiet "$EC_SERVICE_UNIT"; then return 1; fi
  if ! curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null; then return 1; fi
  if ! run_smoke_script >/dev/null 2>&1; then return 1; fi
  if ! durable_remove "$TRANSACTION_FILE"; then return 1; fi
  return 0
}

recover_interrupted_transaction() {
  [[ -f "$TRANSACTION_FILE" ]] || return 0
  rollback_transaction || die "Interrupted deployment transaction could not be safely recovered; service was not advanced"
}

collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd_ok=false local_ok=false public_ok=true revision_ok=false smoke_ok=true
  local source_tree_ok=false state_integrity_ok=false runtime_digest_ok=false actual_head actual_binary

  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd_ok=true
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_ok=true
  if [[ "$EC_CHECK_PUBLIC" == "1" ]]; then
    public_ok=false
    curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_ok=true
  fi
  [[ "$deployed" == "$expected" ]] && revision_ok=true
  actual_head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  actual_binary="$(file_sha256 "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual_head" == "$deployed" ]] && source_tree_ok=true
  [[ "$actual_binary" == "$state_binary" ]] && state_integrity_ok=true
  [[ "$actual_binary" == "$expected_binary" ]] && runtime_digest_ok=true
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    smoke_ok=false
    run_smoke_script >/dev/null 2>&1 && smoke_ok=true
  fi

  python3 - "$systemd_ok" "$local_ok" "$public_ok" "$revision_ok" "$smoke_ok" \
    "$source_tree_ok" "$state_integrity_ok" "$runtime_digest_ok" <<'PY'
import json, sys
names = ["systemd", "local_http", "public_https", "release_revision", "service_smoke",
         "source_tree", "state_integrity", "runtime_digest"]
vals = [v.lower() == "true" for v in sys.argv[1:]]
print(json.dumps(dict(zip(names, vals)), sort_keys=True, separators=(",", ":")))
PY
}

status_from_checks() {
  python3 - "$1" <<'PY'
import json, sys
checks = json.loads(sys.argv[1])
mandatory = ("systemd", "local_http", "release_revision", "service_smoke",
             "source_tree", "state_integrity", "runtime_digest")
if not all(checks.get(k, False) for k in mandatory):
    print("unhealthy")
elif not checks.get("public_https", False):
    print("degraded")
else:
    print("healthy")
PY
}

build_attestation() {
  local expected="$1" deployed="$2" checks="$3" status="$4" manifest_sha="$5"
  local actual_runtime="$6" expected_runtime="$7"
  python3 - "$AGENT_VERSION" "$EC_SERVICE" "$EC_REPOSITORY" "$EC_ENVIRONMENT" \
    "$EC_RELEASE_TAG" "$deployed" "$expected" "$status" "$checks" "$EC_HOST_ID" \
    "$manifest_sha" "$actual_runtime" "$expected_runtime" <<'PY'
import datetime, hashlib, json, sys
(agent, service, repo, env, tag, deployed, expected, status, checks, host,
 manifest_sha, actual_runtime, expected_runtime) = sys.argv[1:]
payload = {
    "schema_version": "0.1",
    "service": service,
    "repository": repo,
    "environment": env,
    "release_tag": tag,
    "deployed_commit": deployed,
    "expected_commit": expected,
    "release_manifest_sha256": manifest_sha,
    "deployed_runtime_sha256": actual_runtime,
    "expected_runtime_sha256": expected_runtime,
    "status": status,
    "checks": json.loads(checks),
    "observed_at": datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00", "Z"),
    "agent_version": agent,
    "host_id": host,
}
canonical = json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()
payload["observation_id"] = hashlib.sha256(canonical).hexdigest()
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
  local timestamp signature observation_id
  timestamp="$(date +%s)"
  observation_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["observation_id"])' <<<"$body")"
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
    -H "Idempotency-Key: ${observation_id}" \
    --data-binary "$body" \
    "$EC_ATTESTATION_ENDPOINT" >/dev/null
}

attest() {
  local tmpdir deployed state_binary checks status body actual_runtime
  tmpdir="$(mktemp -d)"
  if ! load_release_snapshot "$tmpdir"; then
    rm -rf "$tmpdir"
    warn "Could not resolve a valid release manifest snapshot"
    return 2
  fi
  rm -rf "$tmpdir"
  deployed="$(read_current_commit)"
  state_binary="$(state_field "$CURRENT_STATE_FILE" binary_sha256)"
  [[ "$deployed" =~ ^[0-9a-f]{40}$ && "$state_binary" =~ ^[0-9a-f]{64}$ ]] \
    || die "Current deployment state is invalid"
  actual_runtime="$(file_sha256 "$EC_APP_BIN")"
  checks="$(collect_checks "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$deployed" "$state_binary")"
  status="$(status_from_checks "$checks")"
  body="$(build_attestation "$RELEASE_SOURCE_COMMIT" "$deployed" "$checks" "$status" \
    "$RELEASE_MANIFEST_SHA256" "$actual_runtime" "$RELEASE_ASSET_SHA256")"
  send_attestation "$body"
  [[ "$status" == "healthy" ]]
}

update_release() {
  local tmpdir current old_commit old_binary old_manifest backup actual_downloaded
  recover_interrupted_transaction
  verify_baseline || die "Current source/runtime pair does not match durable deployment state"

  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' RETURN
  load_release_snapshot "$tmpdir" || die "Could not load a valid atomic deployment manifest"

  current="$(read_current_commit)"
  if [[ "$current" == "$RELEASE_SOURCE_COMMIT" ]]; then
    log "Already running release source $RELEASE_SOURCE_COMMIT"
    attest || true
    return 0
  fi

  download "$RELEASE_ASSET_NAME" "$tmpdir/$RELEASE_ASSET_NAME"
  actual_downloaded="$(file_sha256 "$tmpdir/$RELEASE_ASSET_NAME")"
  [[ "$actual_downloaded" == "$RELEASE_ASSET_SHA256" ]] \
    || die "Runtime asset does not match the digest bound by DEPLOYMENT_MANIFEST.json"

  old_commit="$(state_field "$CURRENT_STATE_FILE" source_commit)"
  old_binary="$(state_field "$CURRENT_STATE_FILE" binary_sha256)"
  old_manifest="$(state_field "$CURRENT_STATE_FILE" release_manifest_sha256)"
  [[ "$old_commit" =~ ^[0-9a-f]{40}$ && "$old_binary" =~ ^[0-9a-f]{64}$ ]] \
    || die "Cannot determine a valid rollback baseline"

  backup="$BACKUP_DIR/runtime-${old_commit}-${old_binary:0:16}"
  install -o root -g root -m 0755 "$EC_APP_BIN" "$backup"
  [[ "$(file_sha256 "$backup")" == "$old_binary" ]] || die "Rollback binary backup digest mismatch"

  write_transaction "$old_commit" "$old_binary" "$old_manifest" "$backup" \
    "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256" \
    || die "Could not durably record deployment transaction"

  if ! systemctl stop "$EC_SERVICE_UNIT"; then
    rollback_transaction || die "Activation stop failed and rollback could not be completed"
    return 1
  fi
  if systemctl is-active --quiet "$EC_SERVICE_UNIT"; then
    rollback_transaction || die "Service remained active and rollback could not be completed"
    return 1
  fi

  if ! git -C "$EC_APP_DIR" fetch --force --depth 1 origin "$RELEASE_SOURCE_COMMIT" \
     || ! git -C "$EC_APP_DIR" checkout --detach FETCH_HEAD \
     || ! git -C "$EC_APP_DIR" reset --hard "$RELEASE_SOURCE_COMMIT" \
     || ! git -C "$EC_APP_DIR" clean -fdx \
     || [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" != "$RELEASE_SOURCE_COMMIT" ]]; then
    rollback_transaction || die "Source activation failed and rollback could not be completed"
    attest || true
    return 1
  fi

  if ! install -o root -g root -m 0755 "$tmpdir/$RELEASE_ASSET_NAME" "${EC_APP_BIN}.new" \
     || [[ "$(file_sha256 "${EC_APP_BIN}.new" 2>/dev/null || true)" != "$RELEASE_ASSET_SHA256" ]] \
     || ! mv -f "${EC_APP_BIN}.new" "$EC_APP_BIN"; then
    rollback_transaction || die "Runtime activation failed and rollback could not be completed"
    attest || true
    return 1
  fi

  if ! systemctl start "$EC_SERVICE_UNIT" \
     || ! systemctl is-active --quiet "$EC_SERVICE_UNIT" \
     || ! curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null \
     || ! run_smoke_script >/dev/null 2>&1; then
    rollback_transaction || die "Health validation failed and rollback could not be completed"
    attest || true
    return 1
  fi

  if ! write_current_state "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "State commit failed and rollback could not be completed"
    attest || true
    return 1
  fi

  if ! durable_remove "$TRANSACTION_FILE"; then
    rollback_transaction || die "Transaction commit cleanup failed and rollback could not be completed"
    attest || true
    return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"
  attest || true
}

bootstrap_current_state
recover_interrupted_transaction

case "${1:-run}" in
  run|update) update_release ;;
  attest|health) attest ;;
  *) die "Usage: $0 [run|update|attest|health]" ;;
esac
