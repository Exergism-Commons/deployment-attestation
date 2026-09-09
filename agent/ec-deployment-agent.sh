#!/usr/bin/env bash
set -Eeuo pipefail

AGENT_VERSION="0.1.0-pre4"
CONFIG_FILE="${EC_ATTESTATION_CONFIG:-/etc/ec-deployment-attestation/service.env}"

log()  { printf '\n==> %s\n' "$*"; }
warn() { printf 'WARN: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ -r "$CONFIG_FILE" ]] || die "Configuration not readable: $CONFIG_FILE"
set -a
# shellcheck disable=SC1090
source "$CONFIG_FILE"
set +a

required=(EC_SERVICE EC_REPOSITORY EC_ENVIRONMENT EC_RELEASE_TAG EC_APP_DIR EC_APP_BIN EC_SERVICE_UNIT EC_SOURCE_REVISION_FILE EC_LOCAL_URL EC_PUBLIC_URL)
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

CURRENT_STATE_FILE="$EC_STATE_DIR/current-state.json"
TRANSACTION_FILE="$EC_STATE_DIR/transaction.json"
BACKUP_DIR="$EC_STATE_DIR/backups"
LOCK_FILE="$EC_STATE_DIR/agent.lock"

install -d -o root -g root -m 0700 "$EC_STATE_DIR" "$BACKUP_DIR"
exec 9>"$LOCK_FILE"
flock -n 9 || { log "Another agent invocation holds the lock"; exit 0; }

arch() {
  local value
  if command -v dpkg >/dev/null 2>&1; then
    value="$(dpkg --print-architecture)"
  else
    case "$(uname -m)" in
      x86_64) value=amd64 ;;
      aarch64|arm64) value=arm64 ;;
      *) return 1 ;;
    esac
  fi
  case "$value" in
    amd64|arm64) printf '%s\n' "$value" ;;
    *) return 1 ;;
  esac
}

download() {
  curl --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 10 -fsSL \
    "$EC_GITHUB_DOWNLOAD_BASE/$1" -o "$2"
}

sha256_file() { sha256sum "$1" | awk '{print $1}'; }

fsync_file_and_dir() {
  python3 - "$1" <<'PY'
import os, pathlib, sys
p = pathlib.Path(sys.argv[1])
fd = os.open(p, os.O_RDONLY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
dfd = os.open(p.parent, os.O_DIRECTORY)
try:
    os.fsync(dfd)
finally:
    os.close(dfd)
PY
}

atomic_write() {
  local target="$1" content="$2" mode="${3:-0600}"
  python3 - "$target" "$content" "$mode" <<'PY'
import os, pathlib, sys, tempfile
p = pathlib.Path(sys.argv[1]); data = sys.argv[2].encode(); mode = int(sys.argv[3], 8)
p.parent.mkdir(parents=True, exist_ok=True)
fd, tmp = tempfile.mkstemp(prefix=p.name + '.', dir=p.parent)
try:
    os.fchmod(fd, mode)
    with os.fdopen(fd, 'wb') as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, p)
    dfd = os.open(p.parent, os.O_DIRECTORY)
    try:
        os.fsync(dfd)
    finally:
        os.close(dfd)
finally:
    try:
        os.unlink(tmp)
    except FileNotFoundError:
        pass
PY
}

durable_remove() {
  python3 - "$1" <<'PY'
import os, pathlib, sys
p = pathlib.Path(sys.argv[1])
try:
    p.unlink()
except FileNotFoundError:
    raise SystemExit(0)
dfd = os.open(p.parent, os.O_DIRECTORY)
try:
    os.fsync(dfd)
finally:
    os.close(dfd)
PY
}

json_field() {
  python3 - "$1" "$2" <<'PY'
import json, pathlib, sys
v = json.loads(pathlib.Path(sys.argv[1]).read_text()).get(sys.argv[2])
print('' if v is None else ('true' if v is True else 'false' if v is False else v))
PY
}

write_current_state() {
  local commit="$1" binary="$2" manifest="$3" body
  body="$(python3 - "$commit" "$binary" "$manifest" "$EC_RELEASE_TAG" <<'PY'
import json, sys
c,b,m,t=sys.argv[1:]
print(json.dumps({'source_commit':c,'binary_sha256':b,'release_manifest_sha256':m or None,'release_tag':t},sort_keys=True,separators=(',',':')))
PY
)" || return 1
  atomic_write "$CURRENT_STATE_FILE" "$body" 0600 || return 1
  atomic_write "$EC_SOURCE_REVISION_FILE" "$commit"$'\n' 0644 || return 1
}

bootstrap_state() {
  [[ -f "$CURRENT_STATE_FILE" ]] && return 0
  local commit head binary
  if [[ -r "$EC_SOURCE_REVISION_FILE" ]]; then
    commit="$(tr -d '\r\n' < "$EC_SOURCE_REVISION_FILE")"
  else
    commit="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  fi
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || die "Cannot bootstrap deployment revision"
  head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  [[ "$head" == "$commit" ]] || die "Bootstrap refused: checkout differs from recorded revision"
  [[ -x "$EC_APP_BIN" ]] || die "Bootstrap refused: runtime missing"
  binary="$(sha256_file "$EC_APP_BIN")"
  [[ "$binary" =~ ^[0-9a-f]{64}$ ]] || die "Bootstrap refused: runtime digest invalid"
  write_current_state "$commit" "$binary" "" || die "Could not bootstrap durable state"
}

validate_manifest() {
  python3 - "$1" "$EC_REPOSITORY" "$EC_RELEASE_TAG" "$2" <<'PY'
import json, pathlib, re, sys
p,repo,tag,arch=sys.argv[1:]
o=json.loads(pathlib.Path(p).read_text())
assert o.get('schema_version')=='0.1', 'schema'
assert o.get('repository')==repo, 'repository'
assert o.get('release_tag')==tag, 'release_tag'
c=o.get('source_commit','')
assert re.fullmatch(r'[0-9a-f]{40}',c), 'source_commit'
a=o.get('assets',{}).get(arch)
assert isinstance(a,dict), 'asset'
n=a.get('name',''); d=a.get('sha256','')
assert re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]*',n), 'asset name'
assert re.fullmatch(r'[0-9a-f]{64}',d), 'asset digest'
print(c); print(n); print(d)
PY
}

load_release_snapshot() {
  local dir="$1" manifest="$dir/$EC_RELEASE_MANIFEST" values a
  download "$EC_RELEASE_MANIFEST" "$manifest" || return 1
  a="$(arch)" || return 1
  values="$(validate_manifest "$manifest" "$a")" || return 1
  RELEASE_SOURCE_COMMIT="$(sed -n '1p' <<<"$values")"
  RELEASE_ASSET_NAME="$(sed -n '2p' <<<"$values")"
  RELEASE_ASSET_SHA256="$(sed -n '3p' <<<"$values")"
  RELEASE_MANIFEST_SHA256="$(sha256_file "$manifest")"
  [[ "$RELEASE_MANIFEST_SHA256" =~ ^[0-9a-f]{64}$ ]]
}

run_smoke() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  EC_PUBLIC_URL="$EC_PUBLIC_URL" EC_LOCAL_URL="$EC_LOCAL_URL" "$EC_SMOKE_SCRIPT"
}

verify_baseline() {
  local commit binary head actual
  commit="$(json_field "$CURRENT_STATE_FILE" source_commit)" || return 1
  binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)" || return 1
  [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary" =~ ^[0-9a-f]{64}$ ]] || return 1
  head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$head" == "$commit" && "$actual" == "$binary" ]]
}

write_transaction() {
  local oldc="$1" oldb="$2" oldm="$3" backup="$4" newc="$5" newb="$6" newm="$7" body
  body="$(python3 - "$oldc" "$oldb" "$oldm" "$backup" "$newc" "$newb" "$newm" <<'PY'
import json,sys
a,b,c,d,e,f,g=sys.argv[1:]
print(json.dumps({'schema_version':'0.1','old_source_commit':a,'old_binary_sha256':b,'old_release_manifest_sha256':c or None,'backup_binary':d,'new_source_commit':e,'new_binary_sha256':f,'new_release_manifest_sha256':g},sort_keys=True,separators=(',',':')))
PY
)" || return 1
  atomic_write "$TRANSACTION_FILE" "$body" 0600
}

rollback_transaction() {
  [[ -f "$TRANSACTION_FILE" ]] || return 0
  local commit binary manifest backup digest
  commit="$(json_field "$TRANSACTION_FILE" old_source_commit)" || return 1
  binary="$(json_field "$TRANSACTION_FILE" old_binary_sha256)" || return 1
  manifest="$(json_field "$TRANSACTION_FILE" old_release_manifest_sha256)" || return 1
  backup="$(json_field "$TRANSACTION_FILE" backup_binary)" || return 1
  [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary" =~ ^[0-9a-f]{64}$ ]] || return 1
  [[ "$backup" == "$BACKUP_DIR/"* && -f "$backup" ]] || return 1
  digest="$(sha256_file "$backup" 2>/dev/null || true)"
  [[ "$digest" == "$binary" ]] || return 1

  warn "Recovering transaction to $commit"
  systemctl stop "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && return 1
  git -C "$EC_APP_DIR" reset --hard "$commit" || return 1
  git -C "$EC_APP_DIR" clean -fdx || return 1
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || return 1
  install -o root -g root -m 0755 "$backup" "${EC_APP_BIN}.rollback" || return 1
  [[ "$(sha256_file "${EC_APP_BIN}.rollback" 2>/dev/null || true)" == "$binary" ]] || return 1
  mv -f "${EC_APP_BIN}.rollback" "$EC_APP_BIN" || return 1
  fsync_file_and_dir "$EC_APP_BIN" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  systemctl start "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null || return 1
  run_smoke >/dev/null 2>&1 || return 1
  durable_remove "$TRANSACTION_FILE" || return 1
}

recover_transaction() {
  [[ -f "$TRANSACTION_FILE" ]] || return 0
  rollback_transaction || die "Interrupted transaction could not be safely recovered; refusing a new baseline"
}

collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true source_tree=false state_integrity=false runtime_digest=false
  local head actual
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd=true
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_http=true
  if [[ "$EC_CHECK_PUBLIC" == 1 ]]; then
    public_https=false
    curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_https=true
  fi
  [[ "$deployed" == "$expected" ]] && release_revision=true
  head="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"
  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$head" == "$deployed" ]] && source_tree=true
  [[ "$actual" == "$state_binary" ]] && state_integrity=true
  [[ "$actual" == "$expected_binary" ]] && runtime_digest=true
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    service_smoke=false
    run_smoke >/dev/null 2>&1 && service_smoke=true
  fi
  python3 - "$systemd" "$local_http" "$public_https" "$release_revision" "$service_smoke" "$source_tree" "$state_integrity" "$runtime_digest" <<'PY'
import json,sys
n=['systemd','local_http','public_https','release_revision','service_smoke','source_tree','state_integrity','runtime_digest']
print(json.dumps(dict(zip(n,[x=='true' for x in sys.argv[1:]])),sort_keys=True,separators=(',',':')))
PY
}

status_from_checks() {
  python3 - "$1" <<'PY'
import json,sys
c=json.loads(sys.argv[1])
mandatory=('systemd','local_http','release_revision','service_smoke','source_tree','state_integrity','runtime_digest')
print('unhealthy' if not all(c.get(k,False) for k in mandatory) else 'degraded' if not c.get('public_https',False) else 'healthy')
PY
}

build_attestation() {
  python3 - "$AGENT_VERSION" "$EC_SERVICE" "$EC_REPOSITORY" "$EC_ENVIRONMENT" "$EC_RELEASE_TAG" "$1" "$2" "$3" "$4" "$EC_HOST_ID" "$5" "$6" "$7" <<'PY'
import datetime,hashlib,json,sys
agent,service,repo,env,tag,deployed,expected,status,checks,host,manifest,actual,expected_runtime=sys.argv[1:]
p={'schema_version':'0.1','service':service,'repository':repo,'environment':env,'release_tag':tag,'deployed_commit':deployed,'expected_commit':expected,'release_manifest_sha256':manifest,'deployed_runtime_sha256':actual,'expected_runtime_sha256':expected_runtime,'status':status,'checks':json.loads(checks),'observed_at':datetime.datetime.now(datetime.timezone.utc).isoformat().replace('+00:00','Z'),'agent_version':agent,'host_id':host}
p['observation_id']=hashlib.sha256(json.dumps(p,sort_keys=True,separators=(',',':')).encode()).hexdigest()
print(json.dumps(p,sort_keys=True,separators=(',',':')))
PY
}

send_attestation() {
  local body="$1"
  [[ -z "$EC_ATTESTATION_ENDPOINT" ]] && { printf '%s\n' "$body"; return 0; }
  [[ -r "$EC_HMAC_SECRET_FILE" ]] || die "HMAC secret not readable"
  local ts sig oid
  ts="$(date +%s)"
  oid="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["observation_id"])' <<<"$body")"
  sig="$(python3 - "$EC_HMAC_SECRET_FILE" "$ts" "$body" <<'PY'
import hashlib,hmac,pathlib,sys
p,t,b=sys.argv[1:]
key=pathlib.Path(p).read_bytes().strip()
print(hmac.new(key,(t+'.'+b).encode(),hashlib.sha256).hexdigest())
PY
)"
  curl --retry 3 --retry-all-errors --connect-timeout 10 -fsS \
    -H 'Content-Type: application/json' \
    -H "X-EC-Timestamp: $ts" \
    -H "X-EC-Signature: sha256=$sig" \
    -H "Idempotency-Key: $oid" \
    --data-binary "$body" "$EC_ATTESTATION_ENDPOINT" >/dev/null
}

attest() {
  local dir deployed state_binary actual checks status body
  dir="$(mktemp -d)"
  if ! load_release_snapshot "$dir"; then
    rm -rf "$dir"
    warn "Could not load release manifest"
    return 2
  fi
  rm -rf "$dir"
  deployed="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  state_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  [[ "$deployed" =~ ^[0-9a-f]{40}$ && "$state_binary" =~ ^[0-9a-f]{64}$ ]] || return 2
  actual="$(sha256_file "$EC_APP_BIN")"
  checks="$(collect_checks "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$deployed" "$state_binary")"
  status="$(status_from_checks "$checks")"
  body="$(build_attestation "$deployed" "$RELEASE_SOURCE_COMMIT" "$status" "$checks" "$RELEASE_MANIFEST_SHA256" "$actual" "$RELEASE_ASSET_SHA256")"
  send_attestation "$body"
  [[ "$status" == healthy ]]
}

update_release() {
  recover_transaction
  verify_baseline || die "Current source/runtime pair differs from durable state"

  local dir current current_binary current_manifest downloaded oldc oldb oldm backup
  dir="$(mktemp -d)"
  if ! load_release_snapshot "$dir"; then
    rm -rf "$dir"
    die "Could not load valid deployment manifest"
  fi
  current="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  current_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  current_manifest="$(json_field "$CURRENT_STATE_FILE" release_manifest_sha256)"

  # A rolling release may legitimately rebuild the runtime for the same source
  # commit. Skip activation only when BOTH source and architecture-specific
  # runtime digest match the captured manifest snapshot.
  if [[ "$current" == "$RELEASE_SOURCE_COMMIT" && "$current_binary" == "$RELEASE_ASSET_SHA256" ]]; then
    rm -rf "$dir"
    if [[ "$current_manifest" != "$RELEASE_MANIFEST_SHA256" ]]; then
      write_current_state "$current" "$current_binary" "$RELEASE_MANIFEST_SHA256" \
        || die "Could not refresh manifest identity for matching source/runtime"
    fi
    log "Already running exact source/runtime pair $current"
    attest || true
    return 0
  fi

  if ! download "$RELEASE_ASSET_NAME" "$dir/$RELEASE_ASSET_NAME"; then
    rm -rf "$dir"
    die "Runtime download failed"
  fi
  downloaded="$(sha256_file "$dir/$RELEASE_ASSET_NAME")"
  [[ "$downloaded" == "$RELEASE_ASSET_SHA256" ]] || {
    rm -rf "$dir"
    die "Runtime does not match manifest digest"
  }

  oldc="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  oldb="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  oldm="$(json_field "$CURRENT_STATE_FILE" release_manifest_sha256)"
  [[ "$oldc" =~ ^[0-9a-f]{40}$ && "$oldb" =~ ^[0-9a-f]{64}$ ]] || {
    rm -rf "$dir"
    die "Invalid rollback baseline"
  }
  backup="$BACKUP_DIR/runtime-${oldc}-${oldb:0:16}"
  install -o root -g root -m 0755 "$EC_APP_BIN" "$backup" || {
    rm -rf "$dir"
    die "Backup failed"
  }
  [[ "$(sha256_file "$backup")" == "$oldb" ]] || {
    rm -rf "$dir"
    die "Backup digest mismatch"
  }
  fsync_file_and_dir "$backup" || {
    rm -rf "$dir"
    die "Could not durably persist rollback binary"
  }
  write_transaction "$oldc" "$oldb" "$oldm" "$backup" "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256" || {
    rm -rf "$dir"
    die "Could not durably persist transaction"
  }

  if ! systemctl stop "$EC_SERVICE_UNIT" || systemctl is-active --quiet "$EC_SERVICE_UNIT"; then
    rm -rf "$dir"
    rollback_transaction || die "Stop failed and rollback failed"
    return 1
  fi

  if ! git -C "$EC_APP_DIR" fetch --force --depth 1 origin "$RELEASE_SOURCE_COMMIT" \
     || ! git -C "$EC_APP_DIR" checkout --detach FETCH_HEAD \
     || ! git -C "$EC_APP_DIR" reset --hard "$RELEASE_SOURCE_COMMIT" \
     || ! git -C "$EC_APP_DIR" clean -fdx \
     || [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" != "$RELEASE_SOURCE_COMMIT" ]]; then
    rm -rf "$dir"
    rollback_transaction || die "Source switch failed and rollback failed"
    attest || true
    return 1
  fi

  if ! install -o root -g root -m 0755 "$dir/$RELEASE_ASSET_NAME" "${EC_APP_BIN}.new" \
     || [[ "$(sha256_file "${EC_APP_BIN}.new" 2>/dev/null || true)" != "$RELEASE_ASSET_SHA256" ]] \
     || ! mv -f "${EC_APP_BIN}.new" "$EC_APP_BIN" \
     || ! fsync_file_and_dir "$EC_APP_BIN"; then
    rm -rf "$dir"
    rollback_transaction || die "Runtime switch/durability failed and rollback failed"
    attest || true
    return 1
  fi
  rm -rf "$dir"

  if ! systemctl start "$EC_SERVICE_UNIT" \
     || ! systemctl is-active --quiet "$EC_SERVICE_UNIT" \
     || ! curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null \
     || ! run_smoke >/dev/null 2>&1; then
    rollback_transaction || die "Health failed and rollback failed"
    attest || true
    return 1
  fi

  if ! write_current_state "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "State commit failed and rollback failed"
    attest || true
    return 1
  fi

  if ! durable_remove "$TRANSACTION_FILE"; then
    rollback_transaction || die "Transaction finalization failed and rollback failed"
    attest || true
    return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"
  attest || true
}

bootstrap_state
recover_transaction
case "${1:-run}" in
  run|update) update_release ;;
  attest|health) attest ;;
  *) die "Usage: $0 [run|update|attest|health]" ;;
esac
