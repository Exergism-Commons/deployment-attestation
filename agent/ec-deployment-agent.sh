#!/usr/bin/env bash
set -Eeuo pipefail

AGENT_VERSION="0.1.0-pre13"
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
for command in curl git python3 sha256sum systemctl systemd-run flock install awk sed tr date hostname uname mv rm findmnt nsenter; do
  command -v "$command" >/dev/null 2>&1 || die "Required command not found: $command"
done

EC_HOST_ID="${EC_HOST_ID:-$(hostname -f 2>/dev/null || hostname)}"
EC_GITHUB_DOWNLOAD_BASE="${EC_GITHUB_DOWNLOAD_BASE:-https://github.com/${EC_REPOSITORY}/releases/download/${EC_RELEASE_TAG}}"
EC_RELEASE_MANIFEST="${EC_RELEASE_MANIFEST:-DEPLOYMENT_MANIFEST.json}"
EC_ATTESTATION_ENDPOINT="${EC_ATTESTATION_ENDPOINT:-}"
EC_HMAC_SECRET_FILE="${EC_HMAC_SECRET_FILE:-}"
EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"
EC_SMOKE_TIMEOUT="${EC_SMOKE_TIMEOUT:-60}"
[[ "$EC_SMOKE_TIMEOUT" =~ ^[1-9][0-9]*$ ]] || die "EC_SMOKE_TIMEOUT must be a positive integer number of seconds"
EC_STATE_DIR="${EC_STATE_DIR:-/var/lib/ec-deployment-attestation/${EC_SERVICE}}"
EC_CHECK_PUBLIC="${EC_CHECK_PUBLIC:-1}"

CURRENT_STATE_FILE="$EC_STATE_DIR/current-state.json"
TRANSACTION_FILE="$EC_STATE_DIR/transaction.json"
BACKUP_DIR="$EC_STATE_DIR/backups"
LOCK_FILE="$EC_STATE_DIR/agent.lock"

durable_mkdir_tree() {
  local target="$1" mode="${2:-0700}"
  python3 - "$target" "$mode" <<'PY'
import os, pathlib, stat, sys
p = pathlib.Path(sys.argv[1])
mode = int(sys.argv[2], 8)
if not p.is_absolute():
    raise RuntimeError("state path must be absolute")
missing = []
cur = p
while not cur.exists():
    missing.append(cur)
    cur = cur.parent
if not cur.is_dir() or cur.is_symlink():
    raise RuntimeError(f"unsafe existing ancestor: {cur}")
for d in reversed(missing):
    os.mkdir(d, mode)
    os.chmod(d, mode)
    fd = os.open(d.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
st = p.lstat()
if not stat.S_ISDIR(st.st_mode):
    raise RuntimeError(f"state path is not a directory: {p}")
os.chmod(p, mode)
for d in (p, p.parent):
    fd = os.open(d, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
PY
}

durable_mkdir_tree "$EC_STATE_DIR" 0700
durable_mkdir_tree "$BACKUP_DIR" 0700
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
import os, pathlib, stat, sys
p = pathlib.Path(sys.argv[1])
st = p.lstat()
if not stat.S_ISREG(st.st_mode):
    raise RuntimeError(f"not a regular file: {p}")
fd = os.open(p, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
try:
    os.fsync(fd)
finally:
    os.close(fd)
dfd = os.open(p.parent, os.O_RDONLY | os.O_DIRECTORY)
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
fd, tmp = tempfile.mkstemp(prefix=p.name + ".", dir=p.parent)
try:
    os.fchmod(fd, mode)
    with os.fdopen(fd, "wb") as f:
        f.write(data); f.flush(); os.fsync(f.fileno())
    os.replace(tmp, p)
    dfd = os.open(p.parent, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(dfd)
    finally:
        os.close(dfd)
finally:
    try: os.unlink(tmp)
    except FileNotFoundError: pass
PY
}

durable_remove() {
  python3 - "$1" <<'PY'
import os, pathlib, sys
p = pathlib.Path(sys.argv[1])
try: p.unlink()
except FileNotFoundError: raise SystemExit(0)
fd = os.open(p.parent, os.O_RDONLY | os.O_DIRECTORY)
try: os.fsync(fd)
finally: os.close(fd)
PY
}

json_field() {
  python3 - "$1" "$2" <<'PY'
import json, pathlib, sys
v = json.loads(pathlib.Path(sys.argv[1]).read_text()).get(sys.argv[2])
print("" if v is None else ("true" if v is True else "false" if v is False else v))
PY
}

source_tree_exact() {
  local commit="$1"
  python3 - "$EC_APP_DIR" "$commit" <<'PY'
import hashlib, os, pathlib, stat, subprocess, sys
root = pathlib.Path(sys.argv[1]).absolute()
root_commit = sys.argv[2]

def git(repo, *args, binary=False):
    return subprocess.check_output(["git", "-C", str(repo), *args], stderr=subprocess.DEVNULL, text=not binary)

def blob_oid(data, algorithm):
    h = hashlib.new(algorithm)
    h.update(f"blob {len(data)}\0".encode("ascii")); h.update(data)
    return h.hexdigest()

def entries(repo, commit):
    raw = git(repo, "ls-tree", "-rz", "--full-tree", commit, binary=True)
    for rec in raw.split(b"\0"):
        if not rec: continue
        meta, raw_path = rec.split(b"\t", 1)
        mode, kind, oid = meta.decode("ascii").split()
        yield mode, kind, oid, os.fsdecode(raw_path)

def disk_files(repo, submods):
    found = set()
    for dirpath, dirnames, filenames in os.walk(repo, topdown=True, followlinks=False):
        cur = pathlib.Path(dirpath)
        if cur == repo:
            if ".git" in dirnames: dirnames.remove(".git")
            filenames = [n for n in filenames if n != ".git"]
        for name in list(dirnames):
            p = cur / name
            rel = os.path.relpath(p, repo)
            if rel in submods:
                dirnames.remove(name)
                continue
            if p.is_symlink():
                found.add(rel)
                dirnames.remove(name)
        for name in filenames:
            found.add(os.path.relpath(cur / name, repo))
    return found

def verify(repo, commit):
    rst = repo.lstat()
    if not stat.S_ISDIR(rst.st_mode):
        raise RuntimeError(f"repository path is not a real directory: {repo}")
    head = git(repo, "rev-parse", "HEAD").strip()
    if head != commit:
        raise RuntimeError(f"HEAD mismatch in {repo}: {head} != {commit}")
    algorithm = git(repo, "rev-parse", "--show-object-format").strip()
    if algorithm not in {"sha1", "sha256"}:
        raise RuntimeError(f"unsupported object format: {algorithm}")
    expected = set(); submods = {}
    for mode, kind, oid, rel in entries(repo, commit):
        p = repo / rel
        if mode == "160000" and kind == "commit":
            st = p.lstat()
            if not stat.S_ISDIR(st.st_mode):
                raise RuntimeError(f"gitlink is not a real directory: {rel}")
            submods[rel] = oid
            continue
        if kind != "blob":
            raise RuntimeError(f"unexpected tree object {kind}: {rel}")
        expected.add(rel)
        st = p.lstat()
        if mode == "120000":
            if not stat.S_ISLNK(st.st_mode):
                raise RuntimeError(f"expected symlink: {rel}")
            target = os.readlink(os.fsencode(p))
            data = target if isinstance(target, bytes) else os.fsencode(target)
        elif mode in {"100644", "100755"}:
            if not stat.S_ISREG(st.st_mode):
                raise RuntimeError(f"expected regular file: {rel}")
            if bool(st.st_mode & stat.S_IXUSR) != (mode == "100755"):
                raise RuntimeError(f"executable bit mismatch: {rel}")
            data = p.read_bytes()
        else:
            raise RuntimeError(f"unsupported mode {mode}: {rel}")
        if blob_oid(data, algorithm) != oid:
            raise RuntimeError(f"tracked bytes differ: {rel}")
    actual = disk_files(repo, set(submods))
    if actual != expected:
        raise RuntimeError(f"worktree file set differs; extra={sorted(actual-expected)!r} missing={sorted(expected-actual)!r}")
    for rel, oid in submods.items():
        verify(repo / rel, oid)

verify(root, root_commit)
PY
}

prepare_gitlinks() {
  local commit="$1"
  python3 - "$EC_APP_DIR" "$commit" <<'PY'
import os, pathlib, shutil, stat, subprocess, sys
root = pathlib.Path(sys.argv[1]).absolute(); commit = sys.argv[2]
raw = subprocess.check_output(["git","-C",str(root),"ls-tree","-rz","--full-tree",commit])
for rec in raw.split(b"\0"):
    if not rec: continue
    meta, raw_path = rec.split(b"\t", 1)
    mode, kind, _ = meta.decode("ascii").split()
    if mode != "160000" or kind != "commit": continue
    p = root / os.fsdecode(raw_path)
    try: st = p.lstat()
    except FileNotFoundError: continue
    if stat.S_ISLNK(st.st_mode) or not stat.S_ISDIR(st.st_mode):
        if stat.S_ISDIR(st.st_mode): shutil.rmtree(p)
        else: p.unlink()
        continue
    try:
        subprocess.check_output(["git","-C",str(p),"rev-parse","--git-dir"], stderr=subprocess.DEVNULL)
    except Exception:
        shutil.rmtree(p)
PY
}

clean_populated_submodules() {
  git -C "$EC_APP_DIR" submodule foreach --recursive \
    'git reset --hard HEAD >/dev/null && git clean -ffdx >/dev/null' || return 1
}

sync_submodules() {
  local commit="$1"
  prepare_gitlinks "$commit" || return 1
  git -C "$EC_APP_DIR" submodule sync --recursive || return 1
  git -C "$EC_APP_DIR" submodule update --init --recursive --force || return 1
  clean_populated_submodules || return 1
}

fsync_checkout() {
  local commit="$1"
  python3 - "$EC_APP_DIR" "$commit" <<'PY'
import os, pathlib, stat, subprocess, sys
root = pathlib.Path(sys.argv[1]).absolute(); root_commit = sys.argv[2]
NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

def git(repo, *args, binary=False):
    return subprocess.check_output(["git","-C",str(repo),*args], stderr=subprocess.DEVNULL, text=not binary)

def fsync_regular(p):
    st = p.lstat()
    if not stat.S_ISREG(st.st_mode): return
    fd = os.open(p, os.O_RDONLY | NOFOLLOW)
    try: os.fsync(fd)
    finally: os.close(fd)

def fsync_dir(p):
    fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)

def git_path(repo, arg):
    return pathlib.Path(git(repo, "rev-parse", "--path-format=absolute", arg).strip())

def sync_git_root(path):
    path = path.absolute(); dirs = []
    for dirpath, _, filenames in os.walk(path, topdown=False, followlinks=False):
        d = pathlib.Path(dirpath)
        for name in filenames:
            p = d / name
            try: fsync_regular(p)
            except FileNotFoundError as exc: raise RuntimeError(f"Git metadata disappeared: {p}") from exc
        dirs.append(d)
    for d in dirs: fsync_dir(d)
    fsync_dir(path.parent)

def entries(repo, commit):
    raw = git(repo, "ls-tree", "-rz", "--full-tree", commit, binary=True)
    for rec in raw.split(b"\0"):
        if not rec: continue
        meta, raw_path = rec.split(b"\t", 1)
        mode, kind, oid = meta.decode("ascii").split()
        yield mode, kind, oid, os.fsdecode(raw_path)

def add_parent_chain(dirs, p, repo):
    parent = p.parent
    while True:
        dirs.add(parent)
        if parent == repo: break
        parent = parent.parent

def sync_repo(repo, commit):
    st = repo.lstat()
    if not stat.S_ISDIR(st.st_mode):
        raise RuntimeError(f"repository path is not a real directory: {repo}")
    if git(repo, "rev-parse", "HEAD").strip() != commit:
        raise RuntimeError(f"HEAD changed during fsync: {repo}")
    dirs = {repo}; submods = []
    for mode, kind, oid, rel in entries(repo, commit):
        p = repo / rel
        if mode == "160000" and kind == "commit":
            pst = p.lstat()
            if not stat.S_ISDIR(pst.st_mode):
                raise RuntimeError(f"gitlink is not a real directory during fsync: {rel}")
            add_parent_chain(dirs, p, repo)
            gitfile = p / ".git"
            try:
                gst = gitfile.lstat()
            except FileNotFoundError as exc:
                raise RuntimeError(f"submodule gitfile missing during fsync: {rel}") from exc
            if stat.S_ISREG(gst.st_mode):
                fsync_regular(gitfile)
                add_parent_chain(dirs, gitfile, repo)
            elif stat.S_ISDIR(gst.st_mode):
                fsync_dir(gitfile)
                add_parent_chain(dirs, gitfile, repo)
            else:
                raise RuntimeError(f"unsafe submodule .git entry during fsync: {rel}")
            submods.append((p, oid))
            continue
        if kind != "blob":
            raise RuntimeError(f"unexpected tree entry: {rel}")
        pst = p.lstat()
        if stat.S_ISREG(pst.st_mode): fsync_regular(p)
        add_parent_chain(dirs, p, repo)
    for d in sorted(dirs, key=lambda x: len(x.parts), reverse=True): fsync_dir(d)
    seen = set()
    for meta in (git_path(repo, "--git-dir"), git_path(repo, "--git-common-dir")):
        key = str(meta)
        if key not in seen:
            sync_git_root(meta); seen.add(key)
    for subrepo, subcommit in submods: sync_repo(subrepo, subcommit)

sync_repo(root, root_commit)
PY
}

verify_runtime_exact() {
  local expected="$1" actual
  [[ -x "$EC_APP_BIN" ]] || return 1
  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual" == "$expected" ]] || return 1
  fsync_file_and_dir "$EC_APP_BIN" || return 1
  actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual" == "$expected" ]]
}

write_current_state() {
  local commit="$1" binary="$2" manifest="$3" body
  body="$(python3 - "$commit" "$binary" "$manifest" "$EC_RELEASE_TAG" <<'PY'
import json, sys
c,b,m,t=sys.argv[1:]
print(json.dumps({"source_commit":c,"binary_sha256":b,"release_manifest_sha256":m or None,"release_tag":t},sort_keys=True,separators=(",",":")))
PY
)" || return 1
  atomic_write "$CURRENT_STATE_FILE" "$body" 0600 || return 1
  atomic_write "$EC_SOURCE_REVISION_FILE" "$commit"$'\n' 0644 || return 1
}

bootstrap_state() {
  [[ -f "$CURRENT_STATE_FILE" ]] && return 0
  local commit binary
  if [[ -r "$EC_SOURCE_REVISION_FILE" ]]; then commit="$(tr -d '\r\n' < "$EC_SOURCE_REVISION_FILE")"
  else commit="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"; fi
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || die "Cannot bootstrap deployment revision"
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || die "Bootstrap refused: checkout differs"
  source_tree_exact "$commit" || die "Bootstrap refused: source tree differs"
  binary="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$binary" =~ ^[0-9a-f]{64}$ ]] || die "Bootstrap refused: runtime digest invalid"
  fsync_checkout "$commit" || die "Bootstrap refused: source durability barrier failed"
  verify_runtime_exact "$binary" || die "Bootstrap refused: runtime durability barrier failed"
  write_current_state "$commit" "$binary" "" || die "Could not bootstrap durable state"
}

validate_manifest() {
  python3 - "$1" "$EC_REPOSITORY" "$EC_RELEASE_TAG" "$2" <<'PY'
import json, pathlib, re, sys
p,repo,tag,arch=sys.argv[1:]; o=json.loads(pathlib.Path(p).read_text())
assert o.get("schema_version")=="0.1"
assert o.get("repository")==repo
assert o.get("release_tag")==tag
c=o.get("source_commit",""); assert re.fullmatch(r"[0-9a-f]{40}",c)
a=o.get("assets",{}).get(arch); assert isinstance(a,dict)
n=a.get("name",""); d=a.get("sha256","")
assert re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*",n)
assert re.fullmatch(r"[0-9a-f]{64}",d)
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
  local unit rc=0
  unit="ec-smoke-${EC_SERVICE//[^A-Za-z0-9_.-]/-}-$$-${RANDOM}.service"
  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
  # The transient smoke unit also sees both deployment artifacts read-only and
  # executes as a DynamicUser with no privilege escalation. It therefore cannot
  # ask PID 1 to create/start system units that escape this cgroup/mount fence,
  # while ordinary descendants remain contained until ExitType=cgroup completes.
  systemd-run --quiet --wait --collect --unit="$unit" \
    --property=Type=exec \
    --property=ExitType=cgroup \
    --property=KillMode=control-group \
    --property=DynamicUser=yes \
    --property=NoNewPrivileges=yes \
    --property=ProtectSystem=strict \
    --property=ProtectHome=yes \
    --property=ProtectControlGroups=yes \
    --property=ProtectKernelTunables=yes \
    --property=ProtectKernelModules=yes \
    --property=PrivateDevices=yes \
    --property=RestrictSUIDSGID=yes \
    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \
    --property="ReadOnlyPaths=${EC_APP_DIR}" \
    --property="ReadOnlyPaths=${EC_APP_BIN}" \
    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \
    --setenv="EC_LOCAL_URL=${EC_LOCAL_URL}" \
    "$EC_SMOKE_SCRIPT" || rc=$?
  return "$rc"
}

artifact_write_fence() {
  local pid path options
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  pid="$(systemctl show "$EC_SERVICE_UNIT" -p MainPID --value 2>/dev/null)" || return 1
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1

  # Observe the service's actual mount namespace, not merely unit-file text.
  # A healthy deployment requires both owner-controlled artifacts to be mounted
  # read-only for the running service. The root agent remains outside this
  # namespace and can still perform a later transactional update.
  for path in "$EC_APP_DIR" "$EC_APP_BIN"; do
    [[ "$path" == /* && "$path" != *$'\n'* ]] || return 1
    options="$(nsenter --target "$pid" --mount -- findmnt -T "$path" -n -o OPTIONS 2>/dev/null)" || return 1
    case ",$options," in
      *,ro,*) ;;
      *) return 1 ;;
    esac
  done
}

verify_baseline() {
  local commit binary
  # If the service is active, establish the write-stable boundary before
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
}

write_transaction() {
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
}

switch_source() {
  local commit="$1" fetch_first="${2:-0}"
  if [[ "$fetch_first" == 1 ]]; then
    git -C "$EC_APP_DIR" fetch --force --depth 1 origin "$commit" || return 1
    git -C "$EC_APP_DIR" checkout --detach FETCH_HEAD || return 1
  fi
  git -C "$EC_APP_DIR" reset --hard "$commit" || return 1
  git -C "$EC_APP_DIR" clean -ffdx || return 1
  sync_submodules "$commit" || return 1
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || return 1
  source_tree_exact "$commit" || return 1
  fsync_checkout "$commit"
}

artifact_integrity() {
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
}

# Commit candidate state only while the normal service writer is stopped.
# The recovery journal deliberately survives this boundary and the final
# long-lived start. It is retained in phase=committed as a last-known-good
# recovery point instead of being deleted before that final instance is proven.
finalize_quiescent() {
  local commit="$1" binary="$2" manifest="$3"
  systemctl stop "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" && return 1
  artifact_integrity "$commit" "$binary" || return 1
  write_current_state "$commit" "$binary" "$manifest" || return 1
  artifact_integrity "$commit" "$binary" || return 1
}

resume_committed_service() {
  local commit="$1" binary="$2"
  systemctl start "$EC_SERVICE_UNIT" || return 1
  systemctl is-active --quiet "$EC_SERVICE_UNIT" || return 1
  curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null || return 1
  run_smoke >/dev/null 2>&1 || return 1
  post_start_integrity "$commit" "$binary"
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
}

collect_checks() {
  local expected="$1" expected_binary="$2" deployed="$3" state_binary="$4"
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true artifact_fence=false source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual

  # The smoke hook runs first and cannot leave descendants behind. Before any
  # artifact evidence is accepted, verify that the running service sees the
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
}

status_from_checks() {
  python3 - "$1" <<'PY'
import json,sys
c=json.loads(sys.argv[1]); mandatory=("systemd","local_http","release_revision","service_smoke","artifact_fence","source_tree","state_integrity","runtime_digest","runtime_present")
print("unhealthy" if not all(c.get(k,False) for k in mandatory) else "degraded" if not c.get("public_https",False) else "healthy")
PY
}

build_attestation() {
  python3 - "$AGENT_VERSION" "$EC_SERVICE" "$EC_REPOSITORY" "$EC_ENVIRONMENT" "$EC_RELEASE_TAG" "$1" "$2" "$3" "$4" "$EC_HOST_ID" "$5" "$6" "$7" <<'PY'
import datetime,hashlib,json,sys
agent,service,repo,env,tag,deployed,expected,status,checks,host,manifest,actual,expected_runtime=sys.argv[1:]
p={"schema_version":"0.1","service":service,"repository":repo,"environment":env,"release_tag":tag,"deployed_commit":deployed,"expected_commit":expected,"release_manifest_sha256":manifest,"deployed_runtime_sha256":actual or None,"expected_runtime_sha256":expected_runtime,"status":status,"checks":json.loads(checks),"observed_at":datetime.datetime.now(datetime.timezone.utc).isoformat().replace("+00:00","Z"),"agent_version":agent,"host_id":host}
p["observation_id"]=hashlib.sha256(json.dumps(p,sort_keys=True,separators=(",",":")).encode()).hexdigest()
print(json.dumps(p,sort_keys=True,separators=(",",":")))
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
p,t,b=sys.argv[1:]; key=pathlib.Path(p).read_bytes().strip()
print(hmac.new(key,(t+"."+b).encode(),hashlib.sha256).hexdigest())
PY
)"
  curl --retry 3 --retry-all-errors --connect-timeout 10 -fsS \
    -H 'Content-Type: application/json' -H "X-EC-Timestamp: $ts" \
    -H "X-EC-Signature: sha256=$sig" -H "Idempotency-Key: $oid" \
    --data-binary "$body" "$EC_ATTESTATION_ENDPOINT" >/dev/null
}

attest() {
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
}

update_release() {
  recover_transaction
  if ! verify_baseline; then attest || true; die "Current source/runtime pair differs from durable state or source tree is not byte-exact"; fi

  local dir current current_binary downloaded oldc oldb oldm backup
  dir="$(mktemp -d)"
  if ! load_release_snapshot "$dir"; then rm -rf "$dir"; die "Could not load valid deployment manifest"; fi
  current="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  current_binary="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  if [[ "$current" == "$RELEASE_SOURCE_COMMIT" && "$current_binary" == "$RELEASE_ASSET_SHA256" ]]; then
    rm -rf "$dir"; log "Already running exact source/runtime pair $current"; attest || true; return 0
  fi

  download "$RELEASE_ASSET_NAME" "$dir/$RELEASE_ASSET_NAME" || { rm -rf "$dir"; die "Runtime download failed"; }
  downloaded="$(sha256_file "$dir/$RELEASE_ASSET_NAME")"
  [[ "$downloaded" == "$RELEASE_ASSET_SHA256" ]] || { rm -rf "$dir"; die "Runtime does not match manifest digest"; }

  oldc="$(json_field "$CURRENT_STATE_FILE" source_commit)"
  oldb="$(json_field "$CURRENT_STATE_FILE" binary_sha256)"
  oldm="$(json_field "$CURRENT_STATE_FILE" release_manifest_sha256)"
  [[ "$oldc" =~ ^[0-9a-f]{40}$ && "$oldb" =~ ^[0-9a-f]{64}$ ]] || { rm -rf "$dir"; die "Invalid rollback baseline"; }

  backup="$BACKUP_DIR/runtime-${oldc}-${oldb:0:16}"
  install -o root -g root -m 0755 "$EC_APP_BIN" "$backup" || { rm -rf "$dir"; die "Backup failed"; }
  [[ "$(sha256_file "$backup")" == "$oldb" ]] || { rm -rf "$dir"; die "Backup digest mismatch"; }
  fsync_file_and_dir "$backup" || { rm -rf "$dir"; die "Could not durably persist rollback binary"; }
  write_transaction "$oldc" "$oldb" "$oldm" "$backup" "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256" || { rm -rf "$dir"; die "Could not durably persist transaction"; }

  if ! systemctl stop "$EC_SERVICE_UNIT" || systemctl is-active --quiet "$EC_SERVICE_UNIT"; then
    rm -rf "$dir"; rollback_transaction || die "Stop failed and rollback failed"; return 1
  fi

  if ! switch_source "$RELEASE_SOURCE_COMMIT" 1; then
    rm -rf "$dir"; rollback_transaction || die "Source switch/durability failed and rollback failed"; attest || true; return 1
  fi

  if ! install -o root -g root -m 0755 "$dir/$RELEASE_ASSET_NAME" "${EC_APP_BIN}.new" \
     || [[ "$(sha256_file "${EC_APP_BIN}.new" 2>/dev/null || true)" != "$RELEASE_ASSET_SHA256" ]] \
     || ! mv -f "${EC_APP_BIN}.new" "$EC_APP_BIN" \
     || ! verify_runtime_exact "$RELEASE_ASSET_SHA256"; then
    rm -rf "$dir"; rollback_transaction || die "Runtime switch/durability failed and rollback failed"; attest || true; return 1
  fi
  rm -rf "$dir"

  if ! systemctl start "$EC_SERVICE_UNIT" \
     || ! systemctl is-active --quiet "$EC_SERVICE_UNIT" \
     || ! curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null \
     || ! run_smoke >/dev/null 2>&1 \
     || ! post_start_integrity "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256"; then
    rollback_transaction || die "Health/post-start integrity failed and rollback failed"; attest || true; return 1
  fi

  if ! finalize_quiescent "$RELEASE_SOURCE_COMMIT" "$RELEASE_ASSET_SHA256" "$RELEASE_MANIFEST_SHA256"; then
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
