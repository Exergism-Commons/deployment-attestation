#!/usr/bin/env python3
from pathlib import Path
import re
import textwrap

AGENT = Path("agent/ec-deployment-agent.sh")
SCHEMA = Path("spec/attestation-v0.1.schema.json")
INSTALLER = Path("install/install-id-exergism.sh")
CI = Path(".github/workflows/ci.yml")


def literal(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match, got {count}")
    return text.replace(old, new, 1)


def replace_function(text: str, name: str, next_name: str, body: str) -> str:
    pattern = rf"(?ms)^{re.escape(name)}\(\) \{{\n.*?^\}}\n\n(?={re.escape(next_name)}\(\) \{{)"
    replacement = textwrap.dedent(body).lstrip("\n").rstrip() + "\n\n"
    out, count = re.subn(pattern, lambda _: replacement, text, count=1)
    if count != 1:
        raise SystemExit(f"function {name}: expected exactly one match, got {count}")
    return out


s = AGENT.read_text()
s = literal(s, 'AGENT_VERSION="0.1.0-pre8"', 'AGENT_VERSION="0.1.0-pre9"', "agent version")
s = literal(
    s,
    "for command in curl git python3 sha256sum systemctl flock install awk sed tr date hostname uname mv rm; do",
    "for command in curl git python3 sha256sum systemctl flock install awk sed tr date hostname uname mv rm setsid; do",
    "agent dependency list",
)

s = replace_function(
    s,
    "run_smoke",
    "verify_baseline",
    r'''
run_smoke() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  local pid rc=0
  EC_PUBLIC_URL="$EC_PUBLIC_URL" EC_LOCAL_URL="$EC_LOCAL_URL" setsid "$EC_SMOKE_SCRIPT" &
  pid=$!
  wait "$pid" || rc=$?
  # Smoke hooks are synchronous. Kill ordinary descendants that attempted to
  # outlive the hook so they cannot race deployment finalization.
  kill -TERM -- "-$pid" >/dev/null 2>&1 || true
  kill -KILL -- "-$pid" >/dev/null 2>&1 || true
  return "$rc"
}
''',
)

# Persist the normal submodule worktree's .git linkage in addition to the
# resolved git-dir/common-dir. Match the fsync-specific gitlink arm only.
gitlink_pattern = re.compile(
    r'''(?ms)^(?P<i>[ \t]+)if mode == "160000" and kind == "commit":\n'''
    r'''(?P=i)    pst = p\.lstat\(\)\n'''
    r'''(?P=i)    if not stat\.S_ISDIR\(pst\.st_mode\):\n'''
    r'''(?P=i)        raise RuntimeError\(f"gitlink is not a real directory during fsync: \{rel\}"\)\n'''
    r'''(?P=i)    add_parent_chain\(dirs, p, repo\)\n'''
    r'''(?P=i)    submods\.append\(\(p, oid\)\)\n'''
    r'''(?P=i)    continue'''
)


def gitlink_replacement(match: re.Match[str]) -> str:
    i = match.group("i")
    body = [
        'if mode == "160000" and kind == "commit":',
        '    pst = p.lstat()',
        '    if not stat.S_ISDIR(pst.st_mode):',
        '        raise RuntimeError(f"gitlink is not a real directory during fsync: {rel}")',
        '    add_parent_chain(dirs, p, repo)',
        '    gitfile = p / ".git"',
        '    try:',
        '        gst = gitfile.lstat()',
        '    except FileNotFoundError as exc:',
        '        raise RuntimeError(f"submodule gitfile missing during fsync: {rel}") from exc',
        '    if stat.S_ISREG(gst.st_mode):',
        '        fsync_regular(gitfile)',
        '        add_parent_chain(dirs, gitfile, repo)',
        '    elif stat.S_ISDIR(gst.st_mode):',
        '        fsync_dir(gitfile)',
        '        add_parent_chain(dirs, gitfile, repo)',
        '    else:',
        '        raise RuntimeError(f"unsafe submodule .git entry during fsync: {rel}")',
        '    submods.append((p, oid))',
        '    continue',
    ]
    return "\n".join(i + line for line in body)

s, count = gitlink_pattern.subn(gitlink_replacement, s, count=1)
if count != 1:
    raise SystemExit(f"fsync gitlink arm: expected exactly one match, got {count}")

s = replace_function(
    s,
    "post_start_integrity",
    "rollback_transaction",
    r'''
post_start_integrity() {
  local commit="$1" binary="$2"
  source_tree_exact "$commit" || return 1
  fsync_checkout "$commit" || return 1
  verify_runtime_exact "$binary"
}

# The journal is removed only while the normal service writer is stopped.
# Source and runtime are checked both before and after the durable state write.
finalize_quiescent() {
  local commit="$1" binary="$2" manifest="$3"
  systemctl stop "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && return 1
  post_start_integrity "$commit" "$binary" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  post_start_integrity "$commit" "$binary" || return 1
  durable_remove "$TRANSACTION_FILE" || return 1
}

resume_committed_service() {
  systemctl start "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null
}
''',
)

s = replace_function(
    s,
    "rollback_transaction",
    "recover_transaction",
    r'''
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
  if ! resume_committed_service; then
    warn "Rollback pair was committed exactly, but service restart failed"
    return 1
  fi
}
''',
)

s = replace_function(
    s,
    "collect_checks",
    "status_from_checks",
    r'''
collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual
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
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then service_smoke=false; run_smoke >/dev/null 2>&1 && service_smoke=true; fi
  python3 - "$systemd" "$local_http" "$public_https" "$release_revision" "$service_smoke" "$source_tree" "$state_integrity" "$runtime_digest" "$runtime_present" <<'PY'
import json,sys
n=["systemd","local_http","public_https","release_revision","service_smoke","source_tree","state_integrity","runtime_digest","runtime_present"]
print(json.dumps(dict(zip(n,[x=="true" for x in sys.argv[1:]])),sort_keys=True,separators=(",",":")))
PY
}
''',
)

s = replace_function(
    s,
    "status_from_checks",
    "build_attestation",
    r'''
status_from_checks() {
  python3 - "$1" <<'PY'
import json,sys
c=json.loads(sys.argv[1]); mandatory=("systemd","local_http","release_revision","service_smoke","source_tree","state_integrity","runtime_digest","runtime_present")
print("unhealthy" if not all(c.get(k,False) for k in mandatory) else "degraded" if not c.get("public_https",False) else "healthy")
PY
}
''',
)

s = replace_function(
    s,
    "build_attestation",
    "send_attestation",
    r'''
build_attestation() {
  python3 - "$AGENT_VERSION" "$EC_SERVICE" "$EC_REPOSITORY" "$EC_ENVIRONMENT" "$EC_RELEASE_TAG" "$1" "$2" "$3" "$4" "$EC_HOST_ID" "$5" "$6" "$7" <<'PY'
import datetime,hashlib,json,sys
agent,service,repo,env,tag,deployed,expected,status,checks,host,manifest,actual,expected_runtime=sys.argv[1:]
p={"schema_version":"0.1","service":service,"repository":repo,"environment":env,"release_tag":tag,"deployed_commit":deployed,"expected_commit":expected,"release_manifest_sha256":manifest,"deployed_runtime_sha256":actual or None,"expected_runtime_sha256":expected_runtime,"status":status,"checks":json.loads(checks),"observed_at":datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00","Z"),"agent_version":agent,"host_id":host}
p["observation_id"]=hashlib.sha256(json.dumps(p,sort_keys=True,separators=(",",":")).encode()).hexdigest()
print(json.dumps(p,sort_keys=True,separators=(",",":")))
PY
}
''',
)

s = replace_function(
    s,
    "attest",
    "update_release",
    r'''
attest() {
  local dir deployed state_binary actual checks status body
  dir="$(mktemp -d)"
  if ! load_release_snapshot "$dir"; then rm -rf "$dir"; warn "Could not load release manifest"; return 2; fi
  rm -rf "$dir"
  deployed="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  state_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  [[ "$deployed" =~ ^[0-9a-f]{40}$ && "$state_binary" =~ ^[0-9a-f]{64}$ ]] || return 2
  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual" =~ ^[0-9a-f]{64}$ ]] || actual=""
  checks="$(collect_checks "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$deployed" "$state_binary")"
  status="$(status_from_checks "$checks")"
  body="$(build_attestation "$deployed" "$RELEASE_SOURCE_COMMIT" "$status" "$checks" "$RELEASE_MANIFEST_SHA256" "$actual" "$RELEASE_ASSET_SHA256")"
  send_attestation "$body"
  [[ "$status" == healthy ]]
}
''',
)

# Replace only the final state/journal sequence after the first successful
# health/post-start validation.
final_pattern = re.compile(
    r'''(?ms)^  if ! write_current_state "\$RELEASE_SOURCE_COMMIT" "\$RELEASE_ASSET_SHA256" "\$RELEASE_MANIFEST_SHA256"; then\n.*?^  log "Activated release source \$RELEASE_SOURCE_COMMIT"'''
)
final_replacement = textwrap.dedent(r'''
  if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
    rollback_transaction || die "Quiescent finalization failed and rollback failed"; attest || true; return 1
  fi
  if ! resume_committed_service; then
    warn "Release pair was committed exactly, but service restart failed"
    attest || true
    return 1
  fi

  log "Activated release source $RELEASE_SOURCE_COMMIT"
''').strip("\n")
s, count = final_pattern.subn(lambda _: final_replacement, s, count=1)
if count != 1:
    raise SystemExit(f"update_release finalization: expected exactly one match, got {count}")

AGENT.write_text(s)

schema = SCHEMA.read_text()
schema = literal(
    schema,
    '"deployed_runtime_sha256": {"type": "string", "pattern": "^[0-9a-f]{64}$"}',
    '"deployed_runtime_sha256": {"type": ["string", "null"], "pattern": "^[0-9a-f]{64}$"}',
    "schema deployed runtime",
)
SCHEMA.write_text(schema)

installer = INSTALLER.read_text()
installer = literal(
    installer,
    "curl git python3 sha256sum systemctl flock jq install mktemp awk sed tr date hostname uname mv rm grep",
    "curl git python3 sha256sum systemctl flock jq install mktemp awk sed tr date hostname uname mv rm grep setsid",
    "installer dependency list",
)
INSTALLER.write_text(installer)

ci = CI.read_text()
ci = literal(
    ci,
    "          assert att['properties']['observation_id']['pattern'] == '^[0-9a-f]{64}$'\n",
    "          assert att['properties']['observation_id']['pattern'] == '^[0-9a-f]{64}$'\n"
    "          assert set(att['properties']['deployed_runtime_sha256']['type']) == {'string', 'null'}\n",
    "CI schema assertion",
)
ci = literal(
    ci,
    "          grep -Fq 'post_start_integrity' agent/ec-deployment-agent.sh\n",
    "          grep -Fq 'post_start_integrity' agent/ec-deployment-agent.sh\n"
    "          grep -Fq 'finalize_quiescent' agent/ec-deployment-agent.sh\n"
    "          grep -Fq 'resume_committed_service' agent/ec-deployment-agent.sh\n"
    "          grep -Fq 'setsid \"$EC_SMOKE_SCRIPT\"' agent/ec-deployment-agent.sh\n"
    "          grep -Fq 'submodule gitfile missing during fsync' agent/ec-deployment-agent.sh\n"
    "          grep -Fq 'runtime_present' agent/ec-deployment-agent.sh\n",
    "CI implementation assertions",
)
ci = literal(
    ci,
    "curl git python3 sha256sum systemctl flock jq install mktemp awk sed tr date hostname uname mv rm grep' install/install-id-exergism.sh",
    "curl git python3 sha256sum systemctl flock jq install mktemp awk sed tr date hostname uname mv rm grep setsid' install/install-id-exergism.sh",
    "CI installer dependency assertion",
)
CI.write_text(ci)

print("Applied latest Codex fixes")
