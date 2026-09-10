#!/usr/bin/env python3
from pathlib import Path

agent = Path('agent/ec-deployment-agent.sh')
s = agent.read_text()

s = s.replace('AGENT_VERSION="0.1.0-pre10"', 'AGENT_VERSION="0.1.0-pre11"', 1)
s = s.replace(
    'for command in curl git python3 sha256sum systemctl systemd-run flock install awk sed tr date hostname uname mv rm; do',
    'for command in curl git python3 sha256sum systemctl systemd-run flock install awk sed tr date hostname uname mv rm findmnt nsenter; do',
    1,
)

needle = '''run_smoke() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  local unit rc=0
  unit="ec-smoke-${EC_SERVICE//[^A-Za-z0-9_.-]/-}-$$-${RANDOM}.service"
  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
  systemd-run --quiet --wait --collect --unit="$unit" \\
    --property=Type=exec \\
    --property=ExitType=cgroup \\
    --property=KillMode=control-group \\
    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \\
    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \\
    --setenv="EC_LOCAL_URL=${EC_LOCAL_URL}" \\
    "$EC_SMOKE_SCRIPT" || rc=$?
  return "$rc"
}

verify_baseline() {'''
replacement = '''run_smoke() {
  [[ -z "$EC_SMOKE_SCRIPT" ]] && return 0
  [[ -x "$EC_SMOKE_SCRIPT" ]] || return 1
  local unit rc=0
  unit="ec-smoke-${EC_SERVICE//[^A-Za-z0-9_.-]/-}-$$-${RANDOM}.service"
  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
  systemd-run --quiet --wait --collect --unit="$unit" \\
    --property=Type=exec \\
    --property=ExitType=cgroup \\
    --property=KillMode=control-group \\
    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \\
    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \\
    --setenv="EC_LOCAL_URL=${EC_LOCAL_URL}" \\
    "$EC_SMOKE_SCRIPT" || rc=$?
  return "$rc"
}

artifact_write_fence() {
  local pid path options
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  pid="$(systemctl show "$EC_SERVICE_UNIT" -p MainPID --value 2>/dev/null)" || return 1
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1

  # Observe the service's *actual mount namespace*, not merely unit-file text.
  # A healthy deployment requires both owner-controlled artifacts to be mounted
  # read-only for the running service. The root agent remains outside this
  # namespace and can still perform a later transactional update.
  for path in "$EC_APP_DIR" "$EC_APP_BIN"; do
    [[ "$path" == /* && "$path" != *$'\\n'* ]] || return 1
    options="$(nsenter --target "$pid" --mount -- findmnt -T "$path" -n -o OPTIONS 2>/dev/null)" || return 1
    case ",$options," in
      *,ro,*) ;;
      *) return 1 ;;
    esac
  done
}

verify_baseline() {'''
if needle not in s:
    raise RuntimeError('run_smoke/verify_baseline boundary not found')
s = s.replace(needle, replacement, 1)

old = '''verify_baseline() {
  local commit binary
  commit="$(json_field "$CURRENT_STATE_FILE" source_commit)" || return 1
  binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)" || return 1
  [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary" =~ ^[0-9a-f]{64}$ ]] || return 1
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || return 1
  [[ "$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)" == "$binary" ]] || return 1
  source_tree_exact "$commit"
}'''
new = '''verify_baseline() {
  local commit binary
  # If the service is active, establish the write-stable boundary *before*
  # accepting any source/runtime bytes as the rollback baseline.
  if systemctl is-active --quiet "$EC_SERVICE_UNIT"; then
    artifact_write_fence || return 1
  fi
  commit="$(json_field "$CURRENT_STATE_FILE" source_commit)" || return 1
  binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)" || return 1
  [[ "$commit" =~ ^[0-9a-f]{40}$ && "$binary" =~ ^[0-9a-f]{64}$ ]] || return 1
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || return 1
  [[ "$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)" == "$binary" ]] || return 1
  source_tree_exact "$commit"
}'''
if old not in s:
    raise RuntimeError('verify_baseline body not found')
s = s.replace(old, new, 1)

old = '''post_start_integrity() {
  local commit="$1" binary="$2"
  source_tree_exact "$commit" || return 1
  fsync_checkout "$commit" || return 1
  verify_runtime_exact "$binary"
}'''
new = '''artifact_integrity() {
  local commit="$1" binary="$2"
  source_tree_exact "$commit" || return 1
  fsync_checkout "$commit" || return 1
  verify_runtime_exact "$binary"
}

post_start_integrity() {
  local commit="$1" binary="$2"
  # Once this succeeds, the long-lived service cannot race either the source
  # traversal or runtime hashing: both deployment artifacts are read-only in
  # that service's live mount namespace.
  artifact_write_fence || return 1
  artifact_integrity "$commit" "$binary"
}'''
if old not in s:
    raise RuntimeError('post_start_integrity body not found')
s = s.replace(old, new, 1)

old = '''  post_start_integrity "$commit" "$binary" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  post_start_integrity "$commit" "$binary" || return 1
}'''
new = '''  artifact_integrity "$commit" "$binary" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  artifact_integrity "$commit" "$binary" || return 1
}'''
if old not in s:
    raise RuntimeError('finalize_quiescent integrity sequence not found')
s = s.replace(old, new, 1)

old = '''collect_checks() {
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
new = '''collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true artifact_fence=false source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual

  # The smoke hook runs first and cannot leave descendants behind. Before any
  # artifact evidence is accepted, verify that the *running service* sees the
  # source tree and runtime through read-only mounts. This provides one stable
  # post-smoke view rather than two racy measurements around a writable service.
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    service_smoke=false
    run_smoke >/dev/null 2>&1 && service_smoke=true
  fi

  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd=true
  artifact_write_fence && artifact_fence=true
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
  python3 - "$actual" "$systemd" "$local_http" "$public_https" "$release_revision" "$service_smoke" "$artifact_fence" "$source_tree" "$state_integrity" "$runtime_digest" "$runtime_present" <<'PY'
import json,sys
actual=sys.argv[1] if len(sys.argv[1]) == 64 else None
n=["systemd","local_http","public_https","release_revision","service_smoke","artifact_fence","source_tree","state_integrity","runtime_digest","runtime_present"]
checks=dict(zip(n,[x=="true" for x in sys.argv[2:]]))
print(json.dumps({"runtime_sha256":actual,"checks":checks},sort_keys=True,separators=(",",":")))
PY
}'''
if old not in s:
    raise RuntimeError('collect_checks body not found')
s = s.replace(old, new, 1)

s = s.replace(
    'mandatory=("systemd","local_http","release_revision","service_smoke","source_tree","state_integrity","runtime_digest","runtime_present")',
    'mandatory=("systemd","local_http","release_revision","service_smoke","artifact_fence","source_tree","state_integrity","runtime_digest","runtime_present")',
    1,
)

for invariant in [
    'AGENT_VERSION="0.1.0-pre11"',
    'artifact_write_fence()',
    'nsenter --target "$pid" --mount -- findmnt -T "$path"',
    'artifact_integrity()',
    'artifact_write_fence || return 1',
    'artifact_fence=false',
    '"artifact_fence"',
]:
    if invariant not in s:
        raise RuntimeError(f'missing agent invariant: {invariant}')
agent.write_text(s)

# Install a service-local mount-namespace fence for the id resolver. The root
# updater runs outside the service namespace, so this blocks only service-side
# mutation and does not prevent future transactional deployments.
fence = Path('packaging/id-exergism-artifact-fence.conf')
fence.write_text('''# Managed by Exergism Commons Deployment Attestation.\n# The resolver must never mutate its deployment source or runtime.\n[Service]\nReadOnlyPaths=/srv/id.exergism.org\nReadOnlyPaths=/usr/local/bin/idresolver\n''')

installer = Path('install/install-id-exergism.sh')
i = installer.read_text()
i = i.replace(
    'SERVICE="id.exergism.org"\nMANIFEST_URL=',
    'SERVICE="id.exergism.org"\nTARGET_UNIT="id-exergism.service"\nFENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"\nFENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"\nMANIFEST_URL=',
    1,
)
i = i.replace(
    'for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep; do',
    'for command in curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter; do',
    1,
)
i = i.replace(
    'systemd-run --version >/dev/null\n',
    'systemd-run --version >/dev/null\nfindmnt --version >/dev/null\nnsenter --version >/dev/null\n',
    1,
)
needle = '''install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" \\
  /etc/systemd/system/ec-deployment-attestation@.timer

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then'''
replacement = '''install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" \\
  /etc/systemd/system/ec-deployment-attestation@.timer

install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then'''
if needle not in i:
    raise RuntimeError('installer install boundary not found')
i = i.replace(needle, replacement, 1)
i = i.replace(
    'systemctl daemon-reload\nsystemctl enable --now "ec-deployment-attestation@${SERVICE}.timer"',
    '''systemctl daemon-reload
# Apply the service-local read-only artifact namespace before the updater can
# accept any running deployment as a stable baseline.
systemctl restart "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT" || { echo "Target service failed after artifact fence installation" >&2; exit 1; }
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null || { echo "Target service health failed after artifact fence installation" >&2; exit 1; }
systemctl enable --now "ec-deployment-attestation@${SERVICE}.timer"''',
    1,
)
installer.write_text(i)

ci = Path('.github/workflows/ci.yml')
c = ci.read_text()
c = c.replace('AGENT_VERSION="0.1.0-pre10"', 'AGENT_VERSION="0.1.0-pre11"', 1)
c = c.replace(
    "          grep -Fq 'post_start_integrity' agent/ec-deployment-agent.sh\n",
    "          grep -Fq 'artifact_write_fence' agent/ec-deployment-agent.sh\n          grep -Fq 'nsenter --target \"$pid\" --mount -- findmnt -T \"$path\"' agent/ec-deployment-agent.sh\n          grep -Fq 'artifact_fence' agent/ec-deployment-agent.sh\n          grep -Fq 'post_start_integrity' agent/ec-deployment-agent.sh\n",
    1,
)
c = c.replace(
    "          grep -Fq 'curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep' install/install-id-exergism.sh\n          grep -Fq 'systemd-run --version' install/install-id-exergism.sh\n",
    "          grep -Fq 'curl git python3 sha256sum systemctl systemd-run flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter' install/install-id-exergism.sh\n          grep -Fq 'systemd-run --version' install/install-id-exergism.sh\n          grep -Fq 'findmnt --version' install/install-id-exergism.sh\n          grep -Fq 'nsenter --version' install/install-id-exergism.sh\n          grep -Fq 'ReadOnlyPaths=/srv/id.exergism.org' packaging/id-exergism-artifact-fence.conf\n          grep -Fq 'ReadOnlyPaths=/usr/local/bin/idresolver' packaging/id-exergism-artifact-fence.conf\n          grep -Fq '90-ec-deployment-attestation-artifact-fence.conf' install/install-id-exergism.sh\n",
    1,
)
ci.write_text(c)

threat = Path('docs/THREAT-MODEL.md')
t = threat.read_text()
t = t.replace(
    '- failed activation can be rolled back to a previously recorded source/runtime pair;\n',
    '- failed activation can be rolled back to a previously recorded source/runtime pair;\n- the long-lived service is fenced from writing the deployed source tree or runtime while those artifacts are being attested;\n',
    1,
)
t = t.replace(
    '- the host root account, local filesystem and systemd configuration are trusted until compromise;\n',
    '- the host root account, local filesystem and systemd configuration are trusted until compromise;\n- the production service runs inside the systemd mount namespace installed by Deployment Attestation, with the deployment source and runtime mounted read-only;\n',
    1,
)
t = t.replace(
    'A public health failure after successful local activation should be reported as degraded according to service policy, but should not necessarily trigger automatic rollback if the failure can be external to the host (DNS, upstream network, certificate propagation).\n',
    '''A public health failure after successful local activation should be reported as degraded according to service policy, but should not necessarily trigger automatic rollback if the failure can be external to the host (DNS, upstream network, certificate propagation).

## Service artifact write fence

A running service is not considered a stable measurement source merely because the agent hashes an artifact twice. The id deployment installer places the resolver in a systemd mount namespace where both `/srv/id.exergism.org` and `/usr/local/bin/idresolver` are `ReadOnlyPaths`. The agent verifies the *live service namespace* through `MainPID`, `nsenter` and `findmnt` before accepting a running baseline, before post-start finalization, and before declaring an attestation healthy. The root-owned updater remains outside that namespace and can mutate those paths only while carrying out its journaled transaction.

If the live read-only fence is absent, the observation is unhealthy and an active service cannot be accepted as a new rollback baseline. Root-level mutation remains outside the v0.1 threat model.\n''',
    1,
)
threat.write_text(t)

print('Applied Codex round-3 artifact-fence hardening')
