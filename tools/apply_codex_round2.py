#!/usr/bin/env python3
from pathlib import Path

p = Path("agent/ec-deployment-agent.sh")
s = p.read_text()
if 'AGENT_VERSION="0.1.0-pre10"' not in s:
    raise RuntimeError("expected pre10 agent base")


def between(text, start, end, replacement):
    i = text.index(start)
    j = text.index(end, i)
    return text[:i] + replacement.rstrip() + "\n\n" + text[j:]

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
  # `committed` means state/source/runtime are durably paired, but the final
  # long-lived service instance is not yet proven. Keep the rollback journal
  # across that restart so a crash or failed startup remains recoverable.
  mark_transaction_committed "$commit" "$binary" "$manifest" || return 1
  if ! resume_committed_service "$commit" "$binary"; then
    systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
    return 1
  fi
  durable_remove "$TRANSACTION_FILE" || return 1
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
       && resume_committed_service "$commit" "$binary"; then
      durable_remove "$TRANSACTION_FILE" || return 1
      return 0
    fi
    warn "Committed candidate failed final service verification; restoring last-known-good pair"
  fi
  rollback_transaction || die "Interrupted/invalid transaction could not be safely recovered; refusing a new baseline"
}'''
s = between(s, 'rollback_transaction() {', 'collect_checks() {', rollback_recovery)

collect = r'''collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual

  # The smoke hook runs before *all* observed health/artifact evidence. With
  # ExitType=cgroup, returning from run_smoke also proves its containment cgroup
  # is empty, so the following values form one consistent post-hook snapshot.
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    service_smoke=false
    run_smoke >/dev/null 2>&1 && service_smoke=true
  fi

  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd=true
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_http=true
  if [[ "$EC_CHECK_PUBLIC" == 1 ]]; then
    public_https=false; curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_https=true
  fi
  [[ "$deployed" == "$expected" ]] && release_revision=true
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

old = '''  if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Quiescent state commit failed and rollback failed"; attest || true; return 1
  fi
  if ! resume_committed_service "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256"; then
    rollback_transaction || die "Final service verification failed and rollback failed"; attest || true; return 1
  fi
  if ! mark_transaction_committed "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Could not persist committed recovery phase and rollback failed"; attest || true; return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"'''
new = '''  if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Quiescent state commit failed and rollback failed"; attest || true; return 1
  fi
  if ! mark_transaction_committed "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Could not persist committed recovery phase and rollback failed"; attest || true; return 1
  fi
  if ! resume_committed_service "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256"; then
    rollback_transaction || die "Final service verification failed and rollback failed"; attest || true; return 1
  fi
  if ! durable_remove "$TRANSACTION_FILE"; then
    warn "Final service is healthy but recovery journal removal failed; leaving committed recovery state for retry"
    return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"'''
if old not in s:
    raise RuntimeError("forward finalization block not found")
s = s.replace(old, new, 1)

for required in [
    'AGENT_VERSION="0.1.0-pre10"',
    'ExitType=cgroup',
    'mark_transaction_committed "$RELEASE_SOURCE_COMMIT"',
    'resume_committed_service "$RELEASE_SOURCE_COMMIT"',
    'durable_remove "$TRANSACTION_FILE"',
    'Committed candidate failed final service verification',
    'form one consistent post-hook snapshot',
    '"runtime_sha256":actual',
]:
    if required not in s:
        raise RuntimeError(f"missing invariant: {required}")

p.write_text(s)
print("Tightened committed recovery phase")
