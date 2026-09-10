#!/usr/bin/env python3
from pathlib import Path

p = Path("agent/ec-deployment-agent.sh")
s = p.read_text()


def between(text, start, end, replacement):
    i = text.index(start)
    j = text.index(end, i)
    return text[:i] + replacement.rstrip() + "\n\n" + text[j:]

s = s.replace('AGENT_VERSION="0.1.0-pre9"', 'AGENT_VERSION="0.1.0-pre10"', 1)
s = s.replace(
    'for command in curl git python3 sha256sum systemctl flock install awk sed tr date hostname uname mv rm setsid; do',
    'for command in curl git python3 sha256sum systemctl systemd-run flock install awk sed tr date hostname uname mv rm; do',
    1,
)
needle = 'EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"\nEC_STATE_DIR='
if needle in s:
    s = s.replace(
        needle,
        'EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"\nEC_SMOKE_TIMEOUT="${EC_SMOKE_TIMEOUT:-60}"\n[[ "$EC_SMOKE_TIMEOUT" =~ ^[1-9][0-9]*$ ]] || die "EC_SMOKE_TIMEOUT must be a positive integer number of seconds"\nEC_STATE_DIR=',
        1,
    )

run_smoke = r'''run_smoke() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  local unit rc=0
  unit="ec-smoke-${EC_SERVICE//[^A-Za-z0-9_.-]/-}-$$-${RANDOM}.service"
  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
  systemd-run --quiet --wait --collect --unit="$unit" \
    --property=Type=exec \
    --property=ExitType=cgroup \
    --property=KillMode=control-group \
    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \
    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \
    --setenv="EC_LOCAL_URL=${EC_LOCAL_URL}" \
    "$EC_SMOKE_SCRIPT" || rc=$?
  return "$rc"
}'''
s = between(s, 'run_smoke() {', 'verify_baseline() {', run_smoke)

transaction = r'''write_transaction() {
  local oldc="$1" oldb="$2" oldm="$3" backup="$4" newc="$5" newb="$6" newm="$7" body
  body="$(python3 - "$oldc" "$oldb" "$oldm" "$backup" "$newc" "$newb" "$newm" <<'PY'
import json,sys
a,b,c,d,e,f,g=sys.argv[1:]
print(json.dumps({"schema_version":"0.1","phase":"activating","old_source_commit":a,"old_binary_sha256":b,"old_release_manifest_sha256":c or None,"backup_binary":d,"new_source_commit":e,"new_binary_sha256":f,"new_release_manifest_sha256":g},sort_keys=True,separators=(",",":")))
PY
)" || return 1
  atomic_write "$TRANSACTION_FILE" "$body" 0600
}

mark_transaction_committed() {
  local commit="$1" binary="$2" manifest="$3" body
  [[ -f "$TRANSACTION_FILE" ]] || return 1
  body="$(python3 - "$TRANSACTION_FILE" "$commit" "$binary" "$manifest" <<'PY'
import json,pathlib,sys
path,commit,binary,manifest=sys.argv[1:]
o=json.loads(pathlib.Path(path).read_text())
if o.get("schema_version") != "0.1": raise RuntimeError("invalid transaction schema")
o["phase"]="committed"
o["new_source_commit"]=commit
o["new_binary_sha256"]=binary
o["new_release_manifest_sha256"]=manifest or None
print(json.dumps(o,sort_keys=True,separators=(",",":")))
PY
)" || return 1
  atomic_write "$TRANSACTION_FILE" "$body" 0600
}'''
s = between(s, 'write_transaction() {', 'switch_source() {', transaction)

finalization = r'''# Commit candidate state only while the normal service writer is stopped.
# The recovery journal deliberately survives this boundary and the final
# long-lived start. It is retained in phase=committed as a last-known-good
# recovery point instead of being deleted before that final instance is proven.
finalize_quiescent() {
  local commit="$1" binary="$2" manifest="$3"
  systemctl stop "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && return 1
  post_start_integrity "$commit" "$binary" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  post_start_integrity "$commit" "$binary" || return 1
}

resume_committed_service() {
  local commit="$1" binary="$2"
  systemctl start "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null || return 1
  run_smoke >/dev/null 2>&1 || return 1
  post_start_integrity "$commit" "$binary"
}'''
start = '# The journal is removed only while the normal service writer is stopped.'
if start not in s:
    start = 'finalize_quiescent() {'
s = between(s, start, 'rollback_transaction() {', finalization)

rollback_recovery = r'''rollback_transaction() {
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
  switch_source "$commit" 0 || return 1

  install -o root -g root -m 0755 "$backup" "${EC_APP_BIN}.rollback" || return 1
  [[ "$(sha256_file "${EC_APP_BIN}.rollback" 2>/dev/null || true)" == "$binary" ]] || return 1
  mv -f "${EC_APP_BIN}.rollback" "$EC_APP_BIN" || return 1
  verify_runtime_exact "$binary" || return 1

  systemctl start "$EC_SERVICE_UNIT" || return 1
  if ! systemctl is-active --quiet "$EC_SERVICE_UNIT" \
     || ! curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null \
     || ! run_smoke >/dev/null 2>&1 \
     || ! post_start_integrity "$commit" "$binary"; then
    systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
    return 1
  fi

  if ! finalize_quiescent "$commit" "$binary" "$manifest"; then
    systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
    return 1
  fi
  if ! resume_committed_service "$commit" "$binary"; then
    systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
    return 1
  fi
  # Normalize the persistent recovery journal to the restored pair. Keeping a
  # committed recovery point means a later service-induced artifact drift can
  # still be repaired automatically instead of becoming an unrecoverable base.
  mark_transaction_committed "$commit" "$binary" "$manifest" || return 1
}

recover_transaction() {
  [[ -f "$TRANSACTION_FILE" ]] || return 0
  local phase commit binary state_commit state_binary
  phase="$(json_field "$TRANSACTION_FILE" phase 2>/dev/null || true)"
  if [[ "$phase" == "committed" ]]; then
    commit="$(json_field "$TRANSACTION_FILE" new_source_commit 2>/dev/null || true)"
    binary="$(json_field "$TRANSACTION_FILE" new_binary_sha256 2>/dev/null || true)"
    state_commit="$(json_field "$CURRENT_STATE_FILE" source_commit 2>/dev/null || true)"
    state_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256 2>/dev/null || true)"
    if [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary" =~ ^[0-9a-f]{64}$ \
       && "$state_commit" == "$commit" && "$state_binary" == "$binary" ]] \
       && verify_baseline \
       && systemctl is-active --quiet "$EC_SERVICE_UNIT" \
       && curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null \
       && run_smoke >/dev/null 2>&1 \
       && post_start_integrity "$commit" "$binary"; then
      return 0
    fi
    warn "Committed deployment failed recovery verification; restoring last-known-good pair"
  fi
  rollback_transaction || die "Interrupted/invalid transaction could not be safely recovered; refusing a new baseline"
}'''
s = between(s, 'rollback_transaction() {', 'collect_checks() {', rollback_recovery)

collect = r'''collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd=true
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_http=true
  if [[ "$EC_CHECK_PUBLIC" == 1 ]]; then
    public_https=false; curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_https=true
  fi
  [[ "$deployed" == "$expected" ]] && release_revision=true

  # Smoke runs before the artifact snapshot. ExitType=cgroup guarantees the
  # reviewed hook containment boundary is empty before source/runtime evidence
  # is sampled for both checks and the attestation payload.
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    service_smoke=false
    run_smoke >/dev/null 2>&1 && service_smoke=true
  fi

  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual" =~ ^[0-9a-f]{64}$ ]] && runtime_present=true
  if [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$deployed" ]] && source_tree_exact "$deployed"; then source_tree=true; fi
  [[ "$actual" == "$state_binary" ]] && state_integrity=true
  [[ "$actual" == "$expected_binary" ]] && runtime_digest=true
  python3 - "$actual" "$systemd" "$local_http" "$public_https" "$release_revision" "$service_smoke" "$source_tree" "$state_integrity" "$runtime_digest" "$runtime_present" <<'PY'
import json,sys
actual=sys.argv[1] if len(sys.argv[1]) == 64 else None
n=["systemd","local_http","public_https","release_revision","service_smoke","source_tree","state_integrity","runtime_digest","runtime_present"]
checks=dict(zip(n,[x=="true" for x in sys.argv[2:]]))
print(json.dumps({"runtime_sha256":actual,"checks":checks},sort_keys=True,separators=(",",":")))
PY
}'''
s = between(s, 'collect_checks() {', 'status_from_checks() {', collect)

attest = r'''attest() {
  local dir deployed state_binary snapshot actual checks status body
  dir="$(mktemp -d)"
  if ! load_release_snapshot "$dir"; then rm -rf "$dir"; warn "Could not load release manifest"; return 2; fi
  rm -rf "$dir"
  deployed="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  state_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  [[ "$deployed" =~ ^[0-9a-f]{40}$ && "$state_binary" =~ ^[0-9a-f]{64}$ ]] || return 2
  snapshot="$(collect_checks "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$deployed" "$state_binary")" || return 2
  checks="$(python3 - "$snapshot" <<'PY'
import json,sys
print(json.dumps(json.loads(sys.argv[1])["checks"],sort_keys=True,separators=(",",":")))
PY
)" || return 2
  actual="$(python3 - "$snapshot" <<'PY'
import json,sys
print(json.loads(sys.argv[1]).get("runtime_sha256") or "")
PY
)" || return 2
  status="$(status_from_checks "$checks")"
  body="$(build_attestation "$deployed" "$RELEASE_SOURCE_COMMIT" "$status" "$checks" "$RELEASE_MANIFEST_SHA256" "$actual" "$RELEASE_ASSET_SHA256")"
  send_attestation "$body"
  [[ "$status" == healthy ]]
}'''
s = between(s, 'attest() {', 'update_release() {', attest)

old_tail = '''if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
  rollback_transaction || die "Quiescent finalization failed and rollback failed"; attest || true; return 1
fi
if ! resume_committed_service; then
  warn "Release pair was committed exactly, but service restart failed"
  attest || true
  return 1
fi

log "Activated release source $RELEASE_SOURCE_COMMIT"
  attest || true'''
new_tail = '''  if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Quiescent state commit failed and rollback failed"; attest || true; return 1
  fi
  if ! resume_committed_service "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256"; then
    rollback_transaction || die "Final service verification failed and rollback failed"; attest || true; return 1
  fi
  if ! mark_transaction_committed "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Could not persist committed recovery phase and rollback failed"; attest || true; return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"
  attest || true'''
if old_tail not in s:
    raise RuntimeError("update_release finalization tail not found")
s = s.replace(old_tail, new_tail, 1)

for required in [
    'AGENT_VERSION="0.1.0-pre10"',
    'ExitType=cgroup',
    'RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s',
    '"phase":"activating"',
    'mark_transaction_committed',
    '"phase" == "committed"',
    '"runtime_sha256":actual',
    'snapshot="$(collect_checks',
    'resume_committed_service "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256"',
]:
    if required not in s:
        raise RuntimeError(f"missing invariant after patch: {required}")

p.write_text(s)
print("Applied Codex round-2 agent hardening")
