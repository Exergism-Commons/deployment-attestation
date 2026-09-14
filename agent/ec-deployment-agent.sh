#!/usr/bin/env bash
set -Eeuo pipefail

AGENT_VERSION="0.1.0-pre17"
CONFIG_FILE="${EC_ATTESTATION_CONFIG:-/etc/ec-deployment-attestation/service.env}"
ACTION="${1:-run}"

log()  { printf '\n==> %s\n' "$*"; }
warn() { printf 'WARN: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

command -v stat >/dev/null 2>&1 || die "Required command not found: stat"
[[ -f "$CONFIG_FILE" && ! -L "$CONFIG_FILE" && -r "$CONFIG_FILE" ]] ||
  die "Configuration must be a readable real file: $CONFIG_FILE"
config_owner="$(stat -Lc '%u' -- "$CONFIG_FILE")" ||
  die "Could not inspect configuration owner"
config_mode="$(stat -Lc '%a' -- "$CONFIG_FILE")" ||
  die "Could not inspect configuration mode"
[[ "$config_owner" == "$EUID" ]] ||
  die "Configuration must be owned by the effective user"
(( (8#$config_mode & 022) == 0 )) ||
  die "Configuration must not be writable by group or others"

set -a
# shellcheck disable=SC1090
source "$CONFIG_FILE"
set +a

required=(EC_SERVICE EC_REPOSITORY EC_ENVIRONMENT EC_RELEASE_TAG EC_APP_DIR EC_APP_BIN EC_SERVICE_UNIT EC_SOURCE_REVISION_FILE EC_LOCAL_URL EC_PUBLIC_URL)
for name in "${required[@]}"; do
  [[ -n "${!name:-}" ]] || die "Missing required configuration: $name"
done
for command in curl git python3 sha256sum systemctl systemd-run flock install awk sed tr date hostname uname mv rm findmnt nsenter sleep; do
  command -v "$command" >/dev/null 2>&1 || die "Required command not found: $command"
done

EC_HOST_ID="${EC_HOST_ID:-$(hostname -f 2>/dev/null || hostname)}"
EC_GITHUB_DOWNLOAD_BASE="${EC_GITHUB_DOWNLOAD_BASE:-https://github.com/${EC_REPOSITORY}/releases/download/${EC_RELEASE_TAG}}"
EC_RELEASE_MANIFEST="${EC_RELEASE_MANIFEST:-DEPLOYMENT_MANIFEST.json}"
EC_RELEASE_MANIFEST_VALIDATOR="${EC_RELEASE_MANIFEST_VALIDATOR:-/usr/local/libexec/ec-release-manifest-validator}"
EC_RELEASE_MANIFEST_SCHEMA="${EC_RELEASE_MANIFEST_SCHEMA:-/usr/local/libexec/release-manifest-v0.1.schema.json}"
EC_ATTESTATION_ENDPOINT="${EC_ATTESTATION_ENDPOINT:-}"
EC_HMAC_SECRET_FILE="${EC_HMAC_SECRET_FILE:-}"
EC_SMOKE_SCRIPT="${EC_SMOKE_SCRIPT:-}"
EC_SMOKE_TIMEOUT="${EC_SMOKE_TIMEOUT:-60}"
[[ "$EC_SMOKE_TIMEOUT" =~ ^[1-9][0-9]*$ ]] || die "EC_SMOKE_TIMEOUT must be a positive integer number of seconds"
EC_DOWNLOAD_TIMEOUT="${EC_DOWNLOAD_TIMEOUT:-120}"
[[ "$EC_DOWNLOAD_TIMEOUT" =~ ^[1-9][0-9]*$ ]] || die "EC_DOWNLOAD_TIMEOUT must be a positive integer number of seconds"
EC_STATE_DIR="${EC_STATE_DIR:-/var/lib/ec-deployment-attestation/${EC_SERVICE}}"
AGENT_COORDINATION_LOCK="/run/lock/ec-deployment-attestation-${EC_SERVICE//[^A-Za-z0-9_.-]/-}.agent.lock"
INSTALL_TRANSACTION_ROOT="/var/lib/ec-deployment-attestation/install"
EC_CHECK_PUBLIC="${EC_CHECK_PUBLIC:-1}"
[[ "$EC_CHECK_PUBLIC" =~ ^[01]$ ]] ||
  die "EC_CHECK_PUBLIC must be exactly 0 or 1"

python3 - \
  "$EC_SERVICE" "$EC_REPOSITORY" "$EC_RELEASE_TAG" \
  "$EC_APP_DIR" "$EC_APP_BIN" "$EC_SERVICE_UNIT" "$EC_SOURCE_REVISION_FILE" \
  "$EC_LOCAL_URL" "$EC_PUBLIC_URL" "$EC_GITHUB_DOWNLOAD_BASE" \
  "$EC_ATTESTATION_ENDPOINT" "$EC_HMAC_SECRET_FILE" "$EC_SMOKE_SCRIPT" \
  "$EC_STATE_DIR" "$EC_RELEASE_MANIFEST" \
  "$EC_RELEASE_MANIFEST_VALIDATOR" "$EC_RELEASE_MANIFEST_SCHEMA" <<'PY'
import ipaddress
import os
import pathlib
import re
import stat
import sys
import urllib.parse

(
    service, repository, release_tag,
    app_dir_raw, app_bin_raw, service_unit, revision_raw,
    local_url, public_url, download_base,
    attestation_endpoint, hmac_raw, smoke_raw,
    state_raw, manifest_name,
    manifest_validator_raw, manifest_schema_raw,
) = sys.argv[1:]

SAFE_SERVICE = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
SAFE_UNIT = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._@:-]*$")
SAFE_REPOSITORY = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
SAFE_RELEASE_TAG = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
SAFE_ASSET = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")

def fail(message):
    raise RuntimeError(message)

def absolute(name, raw):
    p = pathlib.Path(raw)
    if not p.is_absolute():
        fail(f"{name} must be an absolute path")
    return pathlib.Path(os.path.normpath(str(p)))

def under(path, root):
    path = os.path.normpath(str(path))
    root = os.path.normpath(str(root))
    try:
        return os.path.commonpath((path, root)) == root
    except ValueError:
        return False

def overlap(left, right):
    return under(left, right) or under(right, left)

def parse_http(name, raw):
    try:
        uri = urllib.parse.urlsplit(raw)
    except ValueError as exc:
        raise RuntimeError(f"{name} must be an absolute HTTP(S) URL") from exc
    if uri.scheme not in {"http", "https"} or not uri.hostname or uri.username or uri.password:
        fail(f"{name} must be an absolute HTTP(S) URL without userinfo")
    return uri

def is_loopback(uri):
    host = (uri.hostname or "").casefold()
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False

if not SAFE_SERVICE.fullmatch(service):
    fail("EC_SERVICE contains unsafe characters")
if not SAFE_REPOSITORY.fullmatch(repository):
    fail("EC_REPOSITORY must match owner/repository")
if not SAFE_RELEASE_TAG.fullmatch(release_tag):
    fail("EC_RELEASE_TAG contains unsafe characters")
if not SAFE_UNIT.fullmatch(service_unit):
    fail("EC_SERVICE_UNIT contains unsafe characters")
if not SAFE_ASSET.fullmatch(manifest_name):
    fail("EC_RELEASE_MANIFEST must be a safe single asset name")

app_dir = absolute("EC_APP_DIR", app_dir_raw)
app_bin = absolute("EC_APP_BIN", app_bin_raw)
revision = absolute("EC_SOURCE_REVISION_FILE", revision_raw)
state_dir = absolute("EC_STATE_DIR", state_raw)
manifest_validator = absolute("EC_RELEASE_MANIFEST_VALIDATOR", manifest_validator_raw)
manifest_schema = absolute("EC_RELEASE_MANIFEST_SCHEMA", manifest_schema_raw)
hmac = absolute("EC_HMAC_SECRET_FILE", hmac_raw) if hmac_raw else None
smoke = absolute("EC_SMOKE_SCRIPT", smoke_raw) if smoke_raw else None

if app_dir == pathlib.Path(app_dir.anchor):
    fail("EC_APP_DIR must not be a filesystem root")
if state_dir == pathlib.Path(state_dir.anchor):
    fail("EC_STATE_DIR must not be a filesystem root")
if overlap(app_dir, state_dir):
    fail("EC_STATE_DIR must not overlap EC_APP_DIR")
for name, path in (
    ("EC_APP_BIN", app_bin),
    ("EC_SOURCE_REVISION_FILE", revision),
    ("EC_RELEASE_MANIFEST_VALIDATOR", manifest_validator),
    ("EC_RELEASE_MANIFEST_SCHEMA", manifest_schema),
):
    if under(path, app_dir):
        fail(f"{name} must not be inside EC_APP_DIR")
if under(app_bin, state_dir):
    fail("EC_APP_BIN must not be inside EC_STATE_DIR")
if hmac is not None and under(hmac, app_dir):
    fail("EC_HMAC_SECRET_FILE must not be inside EC_APP_DIR")
if smoke is not None:
    if under(smoke, app_dir):
        fail("EC_SMOKE_SCRIPT must not be inside EC_APP_DIR")
    if under(smoke, state_dir):
        fail("EC_SMOKE_SCRIPT must not be inside EC_STATE_DIR")

local = parse_http("EC_LOCAL_URL", local_url)
if not is_loopback(local):
    fail("EC_LOCAL_URL must target loopback")
public = parse_http("EC_PUBLIC_URL", public_url)
if public.scheme != "https":
    fail("EC_PUBLIC_URL must use HTTPS")
download = parse_http("EC_GITHUB_DOWNLOAD_BASE", download_base)
if download.scheme != "https":
    fail("EC_GITHUB_DOWNLOAD_BASE must use HTTPS")

if attestation_endpoint:
    receiver = parse_http("EC_ATTESTATION_ENDPOINT", attestation_endpoint)
    if receiver.scheme != "https" and not is_loopback(receiver):
        fail("EC_ATTESTATION_ENDPOINT must use HTTPS unless loopback")
    if hmac is None:
        fail("EC_HMAC_SECRET_FILE is required when EC_ATTESTATION_ENDPOINT is configured")
    try:
        st = os.lstat(hmac)
    except OSError as exc:
        raise RuntimeError("EC_HMAC_SECRET_FILE is not readable") from exc
    if not stat.S_ISREG(st.st_mode):
        fail("EC_HMAC_SECRET_FILE must be a real regular file")
    try:
        secret = hmac.read_bytes()
    except OSError as exc:
        raise RuntimeError("EC_HMAC_SECRET_FILE is not readable") from exc
    if not secret.strip():
        fail("EC_HMAC_SECRET_FILE must not be empty")
PY

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
NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

if not p.is_absolute():
    raise RuntimeError("state path must be absolute")
p = pathlib.Path(os.path.normpath(str(p)))

def validate_existing_chain(path):
    current = path
    while True:
        try:
            st = os.lstat(current)
        except FileNotFoundError:
            pass
        else:
            if stat.S_ISLNK(st.st_mode):
                raise RuntimeError(f"unsafe symlink ancestor: {current}")
            if current != path and not stat.S_ISDIR(st.st_mode):
                raise RuntimeError(f"unsafe non-directory ancestor: {current}")
        if current == current.parent:
            return
        current = current.parent

validate_existing_chain(p)
existed = p.exists()
missing = []
cur = p
while not cur.exists():
    missing.append(cur)
    if cur == cur.parent:
        raise RuntimeError(f"cannot resolve existing ancestor: {p}")
    cur = cur.parent

st = os.lstat(cur)
if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
    raise RuntimeError(f"unsafe existing ancestor: {cur}")

for d in reversed(missing):
    os.mkdir(d, mode)
    os.chmod(d, mode)
    for sync in (d, d.parent):
        fd = os.open(sync, os.O_RDONLY | os.O_DIRECTORY | NOFOLLOW)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)

st = os.lstat(p)
if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
    raise RuntimeError(f"state path is not a real directory: {p}")
if existed and stat.S_IMODE(st.st_mode) != mode:
    raise RuntimeError(
        f"existing state directory permissions differ: {p} "
        f"expected={oct(mode)} actual={oct(stat.S_IMODE(st.st_mode))}"
    )

validate_existing_chain(p)
for d in (p, p.parent):
    fd = os.open(d, os.O_RDONLY | os.O_DIRECTORY | NOFOLLOW)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
PY
}

durable_mkdir_tree "$EC_STATE_DIR" 0700
durable_mkdir_tree "$BACKUP_DIR" 0700
exec 8>"$AGENT_COORDINATION_LOCK"
if [[ "${EC_AGENT_COORDINATION_LOCK_HELD:-0}" != 1 ]] && ! flock -n 8; then
  if [[ "$ACTION" == "recover" ]]; then
    die "Cannot coordinate recovery while installer or another coordinator holds the agent lock"
  fi
  log "Installer or another coordinator holds the agent lock"
  exit 0
fi
exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  if [[ "$ACTION" == "recover" ]]; then
    die "Cannot coordinate recovery while another agent invocation holds the lock"
  fi
  log "Another agent invocation holds the lock"
  exit 0
fi

# The systemd recovery dependency protects unit-started agents, but direct CLI
# invocations can bypass that dependency. Durable installer phases therefore
# remain an independent fail-closed admission gate after all volatile locks are
# acquired.
for phase in pending validated recovering recovered; do
  if [[ -d "${INSTALL_TRANSACTION_ROOT}/${EC_SERVICE}.${phase}" ]]; then
    die "Installer transaction phase '${phase}' is still actionable; run installer recovery before deployment-agent work"
  fi
done

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
  curl --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 10 \
    --max-time "$EC_DOWNLOAD_TIMEOUT" -fsSL \
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
  local commit binary active_state pid
  active_state="$(systemctl show "$EC_SERVICE_UNIT" -p ActiveState --value 2>/dev/null || true)"
  pid="$(service_main_pid 2>/dev/null || true)"

  # Never sample a transitional writer. If a live process exists in any unit
  # state, first prove its mount namespace fences both deployment artifacts.
  if [[ "$pid" =~ ^[1-9][0-9]*$ ]]; then
    live_runtime_invariants || die "Bootstrap refused: live service process does not match the fenced configured runtime"
  fi

  case "$active_state" in
    active)
      ;;
    inactive|failed)
      [[ -z "$pid" ]] || die "Bootstrap refused: non-active service still has a live MainPID"
      systemctl start "$EC_SERVICE_UNIT" || die "Bootstrap refused: could not start target service"
      systemctl is-active --quiet "$EC_SERVICE_UNIT" || die "Bootstrap refused: target service did not become active"
      live_runtime_invariants || die "Bootstrap refused: started service does not match the fenced configured runtime"
      ;;
    *)
      die "Bootstrap refused: target service is in transitional state ${active_state:-unknown}"
      ;;
  esac

  # A rollback baseline must already be operationally viable. These are the
  # same mandatory local/semantic checks used when validating recovery.
  curl -fsS --max-time 15 "$EC_LOCAL_URL" >/dev/null || die "Bootstrap refused: local health check failed"
  run_smoke >/dev/null 2>&1 || die "Bootstrap refused: semantic smoke check failed"
  live_runtime_invariants || die "Bootstrap refused: live runtime invariant lost during health validation"

  if [[ -r "$EC_SOURCE_REVISION_FILE" ]]; then commit="$(tr -d '\r\n' < "$EC_SOURCE_REVISION_FILE")"
  else commit="$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)"; fi
  [[ "$commit" =~ ^[0-9a-f]{40}$ ]] || die "Cannot bootstrap deployment revision"
  [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$commit" ]] || die "Bootstrap refused: checkout differs"
  source_tree_exact "$commit" || die "Bootstrap refused: source tree differs"
  binary="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$binary" =~ ^[0-9a-f]{64}$ ]] || die "Bootstrap refused: runtime digest invalid"
  fsync_checkout "$commit" || die "Bootstrap refused: source durability barrier failed"
  verify_runtime_exact "$binary" || die "Bootstrap refused: runtime durability barrier failed"
  live_runtime_invariants || die "Bootstrap refused: live runtime invariant lost before state commit"
  source_tree_exact "$commit" || die "Bootstrap refused: source changed before state commit"
  [[ "$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)" == "$binary" ]] || die "Bootstrap refused: runtime changed before state commit"
  write_current_state "$commit" "$binary" "" || die "Could not bootstrap durable state"
}
validate_manifest() {
  [[ -x "$EC_RELEASE_MANIFEST_VALIDATOR" ]] || {
    warn "Release-manifest validator is missing or not executable: $EC_RELEASE_MANIFEST_VALIDATOR"
    return 1
  }
  [[ -r "$EC_RELEASE_MANIFEST_SCHEMA" ]] || {
    warn "Release-manifest schema is missing or unreadable: $EC_RELEASE_MANIFEST_SCHEMA"
    return 1
  }
  "$EC_RELEASE_MANIFEST_VALIDATOR" "$EC_RELEASE_MANIFEST_SCHEMA" "$1" || return 1
  # Schema validation establishes the complete structural contract. These
  # additional checks bind that valid document to this service/channel/arch.
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

service_main_pid() {
  local pid
  pid="$(systemctl show "$EC_SERVICE_UNIT" -p MainPID --value 2>/dev/null)" || return 1
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || return 1
  printf '%s\n' "$pid"
}

running_runtime_sha256() {
  local pid pid_after digest
  pid="$(service_main_pid)" || return 1
  digest="$(sha256sum "/proc/$pid/exe" 2>/dev/null | awk '{print $1}')" || return 1
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || return 1
  pid_after="$(service_main_pid)" || return 1
  [[ "$pid_after" == "$pid" ]] || return 1
  printf '%s\n' "$digest"
}

bound_running_runtime_sha256() {
  local pid pid_after digest
  pid="$(service_main_pid)" || return 1
  digest="$(python3 - "$pid" "$EC_APP_BIN" <<'PY'
import hashlib
import os
import stat
import sys

pid, runtime = sys.argv[1:]
fd = None
try:
    fd = os.open(f"/proc/{pid}/exe", os.O_RDONLY)
    live = os.fstat(fd)
    configured = os.stat(runtime)
    if not stat.S_ISREG(configured.st_mode):
        raise RuntimeError("configured runtime is not a regular file")

    # No implicit wrapper model: MainPID must execute the exact filesystem
    # object configured as EC_APP_BIN. An atomic path replacement leaves the
    # running executable on the old inode and therefore fails this check.
    if (live.st_dev, live.st_ino) != (configured.st_dev, configured.st_ino):
        raise RuntimeError("MainPID executable does not match EC_APP_BIN")

    h = hashlib.sha256()
    while True:
        chunk = os.read(fd, 1024 * 1024)
        if not chunk:
            break
        h.update(chunk)

    # The opened executable object itself must remain stable while hashed.
    after = os.fstat(fd)
    if (after.st_dev, after.st_ino, after.st_size) != (live.st_dev, live.st_ino, live.st_size):
        raise RuntimeError("live executable changed while hashing")
    print(h.hexdigest())
except (FileNotFoundError, PermissionError, ProcessLookupError, OSError, RuntimeError):
    raise SystemExit(1)
finally:
    if fd is not None:
        os.close(fd)
PY
)" || return 1
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || return 1
  pid_after="$(service_main_pid)" || return 1
  [[ "$pid_after" == "$pid" ]] || return 1
  printf '%s\n' "$digest"
}

runtime_process_binding() {
  bound_running_runtime_sha256 >/dev/null
}

service_cgroup_has_processes() {
  local cgroup
  if ! cgroup="$(systemctl show "$EC_SERVICE_UNIT" -p ControlGroup --value 2>/dev/null)"; then
    return 2
  fi
  [[ -n "$cgroup" ]] || return 1
  python3 - "$cgroup" <<'PY'
import pathlib
import sys

root = pathlib.Path("/sys/fs/cgroup") / sys.argv[1].lstrip("/")
if not root.exists():
    raise SystemExit(1)
for procs in root.rglob("cgroup.procs"):
    try:
        if procs.read_text().strip():
            raise SystemExit(0)
    except FileNotFoundError:
        continue
    except PermissionError:
        raise SystemExit(2)
raise SystemExit(1)
PY
}

service_is_quiescent() {
  local load active main_pid cgroup_rc
  if ! load="$(systemctl show "$EC_SERVICE_UNIT" -p LoadState --value 2>/dev/null)"; then
    return 1
  fi
  [[ "$load" == "loaded" ]] || return 1
  if ! active="$(systemctl show "$EC_SERVICE_UNIT" -p ActiveState --value 2>/dev/null)"      || ! main_pid="$(systemctl show "$EC_SERVICE_UNIT" -p MainPID --value 2>/dev/null)"; then
    return 1
  fi
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ "$main_pid" == 0 ]] || return 1
  if service_cgroup_has_processes; then
    return 1
  else
    cgroup_rc=$?
    (( cgroup_rc == 1 )) || return 1
  fi
  return 0
}

stop_service_quiescent() {
  local i
  systemctl stop "$EC_SERVICE_UNIT" >/dev/null 2>&1 || true
  for i in {1..30}; do
    service_is_quiescent && return 0
    sleep 1
  done
  warn "Target service did not become provably quiescent"
  return 1
}

artifact_write_fence() {
  local pid pid_after
  pid="$(service_main_pid)" || return 1

  # Audit the service mount namespace as a whole. Nested ReadWritePaths/bind
  # mounts below EC_APP_DIR violate the artifact fence even if the outer mount
  # remains read-only.
  nsenter --target "$pid" --mount -- python3 - "$EC_APP_DIR" "$EC_APP_BIN" <<'PY' || return 1
import os
import re
import sys

app_dir = os.path.normpath(sys.argv[1])
app_bin = os.path.normpath(sys.argv[2])
if not (app_dir.startswith("/") and app_bin.startswith("/")):
    raise SystemExit(1)
if "\n" in app_dir or "\n" in app_bin:
    raise SystemExit(1)

_octal = re.compile(r"\\([0-7]{3})")
def unescape(value):
    return _octal.sub(lambda m: chr(int(m.group(1), 8)), value)

mounts = []
with open("/proc/self/mountinfo", "r", encoding="utf-8") as fh:
    for raw in fh:
        left, sep, _right = raw.rstrip("\n").partition(" - ")
        if not sep:
            raise SystemExit(1)
        fields = left.split()
        if len(fields) < 6:
            raise SystemExit(1)
        mounts.append((os.path.normpath(unescape(fields[4])), set(fields[5].split(","))))

def contains(root, path):
    try:
        return os.path.commonpath((root, path)) == root
    except ValueError:
        return False

def deepest(path):
    candidates = [(target, opts) for target, opts in mounts if contains(target, path)]
    if not candidates:
        raise SystemExit(1)
    return max(candidates, key=lambda item: len(item[0]))

_source_target, source_opts = deepest(app_dir)
if "ro" not in source_opts or "rw" in source_opts:
    raise SystemExit(1)

for target, opts in mounts:
    if target == app_dir or (target != app_dir and contains(app_dir, target)):
        if "ro" not in opts or "rw" in opts:
            raise SystemExit(1)

_binary_target, binary_opts = deepest(app_bin)
if "ro" not in binary_opts or "rw" in binary_opts:
    raise SystemExit(1)
PY

  pid_after="$(service_main_pid)" || return 1
  [[ "$pid_after" == "$pid" ]] || return 1
}

live_runtime_snapshot_sha256() {
  local pid_before pid_after digest
  pid_before="$(service_main_pid)" || return 1
  artifact_write_fence || return 1
  digest="$(bound_running_runtime_sha256)" || return 1
  pid_after="$(service_main_pid)" || return 1
  [[ "$pid_after" == "$pid_before" ]] || return 1
  printf '%s\n' "$digest"
}

live_runtime_invariants() {
  live_runtime_snapshot_sha256 >/dev/null
}

verify_baseline() {
  local commit binary load active
  # Determine service state explicitly. A manager/DBus failure or a transitional
  # state must never be interpreted as "inactive" because that would skip the
  # write-stable fence while accepting rollback baseline bytes.
  load="$(systemctl show "$EC_SERVICE_UNIT" -p LoadState --value 2>/dev/null)" || return 1
  [[ "$load" == "loaded" ]] || return 1
  active="$(systemctl show "$EC_SERVICE_UNIT" -p ActiveState --value 2>/dev/null)" || return 1
  case "$active" in
    active) live_runtime_invariants || return 1 ;;
    inactive|failed) ;;
    *) return 1 ;;
  esac
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

reconcile_stale_git_locks() {
  local rollback_commit="${1:-}"
  # Recovery derives metadata only from the verified Git topology: the current
  # checkout plus the durable rollback target. Never trust arbitrary .git
  # markers discovered by walking the filesystem.
  python3 - "$EC_APP_DIR" "$rollback_commit" <<'PY'
import os
import pathlib
import stat
import subprocess
import sys

app = pathlib.Path(os.path.normpath(sys.argv[1]))
rollback_commit = sys.argv[2].strip()
NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)

def git(repo, *args, check=True):
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=120,
        check=False,
    )
    if check and result.returncode != 0:
        raise RuntimeError(f"git failed in {repo}: {result.stderr.strip()}")
    return result

def lexical_under(path, root):
    path = os.path.normpath(str(path))
    root = os.path.normpath(str(root))
    try:
        return os.path.commonpath((path, root)) == root
    except ValueError:
        return False

def safe_tree_path(repo, relative):
    pure = pathlib.PurePosixPath(relative)
    parts = pure.parts
    if (
        not relative
        or pure.is_absolute()
        or any(part in {"", ".", "..", ".git"} for part in parts)
    ):
        raise RuntimeError(f"unsafe Git tree path: {relative}")
    candidate = pathlib.Path(os.path.normpath(str(repo.joinpath(*parts))))
    if not lexical_under(candidate, repo) or candidate == repo:
        raise RuntimeError(f"Git tree path escapes repository: {relative}")
    return candidate

def git_roots(repo):
    roots = []
    for flag in ("--git-dir", "--git-common-dir"):
        result = git(repo, "rev-parse", "--path-format=absolute", flag)
        value = result.stdout.strip()
        if not value:
            raise RuntimeError(f"Git returned empty {flag} for {repo}")
        root = pathlib.Path(os.path.normpath(value))
        if not root.is_absolute():
            raise RuntimeError(f"Git returned non-absolute {flag} for {repo}")
        if root not in roots:
            roots.append(root)
    return roots

def validate_real_directory_chain(path, boundary):
    if not lexical_under(path, boundary):
        raise RuntimeError(f"Git metadata escapes hierarchy: {path}")
    current = path
    while True:
        try:
            st = os.lstat(current)
        except OSError as exc:
            raise RuntimeError(f"cannot inspect Git metadata path: {current}") from exc
        if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
            raise RuntimeError(f"Git metadata path is not a real directory: {current}")
        if current == boundary:
            return
        if current == current.parent:
            raise RuntimeError(f"Git metadata escaped boundary: {path}")
        current = current.parent

def validate_roots(repo, own_roots, parent_roots):
    if not own_roots:
        raise RuntimeError("Git metadata hierarchy is empty")

    if parent_roots is None:
        embedded = repo / ".git"
        try:
            st = os.lstat(embedded)
        except OSError as exc:
            raise RuntimeError("top-level repository must have in-tree .git directory") from exc
        if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
            raise RuntimeError("top-level repository must have a real in-tree .git directory")
        for root in own_roots:
            validate_real_directory_chain(root, embedded)
        return

    parents = list(dict.fromkeys(parent_roots))
    for root in own_roots:
        if root in parents:
            raise RuntimeError("submodule Git metadata aliases ancestor metadata")

    allowed = list(parents)
    embedded = repo / ".git"
    try:
        embedded_st = os.lstat(embedded)
    except FileNotFoundError:
        embedded_st = None
    if embedded_st is not None and stat.S_ISDIR(embedded_st.st_mode) and not stat.S_ISLNK(embedded_st.st_mode):
        allowed.append(embedded)

    for root in own_roots:
        candidates = [boundary for boundary in allowed if lexical_under(root, boundary)]
        if not candidates:
            raise RuntimeError(f"submodule Git metadata escapes verified hierarchy: {root}")
        boundary = max(candidates, key=lambda value: len(str(value)))
        validate_real_directory_chain(root, boundary)

def gitlinks(repo, commit):
    result = subprocess.run(
        ["git", "-C", str(repo), "ls-tree", "-rz", "--full-tree", commit],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        timeout=30,
        check=False,
    )
    if result.returncode != 0:
        raise RuntimeError(f"cannot read Git tree {commit} in {repo}")
    links = []
    for record in result.stdout.split(b"\0"):
        if not record:
            continue
        try:
            meta, raw_path = record.split(b"\t", 1)
            mode, kind, oid = meta.decode("ascii").split()
            relative = raw_path.decode("utf-8", "strict")
        except (ValueError, UnicodeError) as exc:
            raise RuntimeError("malformed git ls-tree record") from exc
        if mode == "160000" and kind == "commit":
            links.append((relative, oid))
    return links

def populated_marker(submodule):
    try:
        st = os.lstat(submodule)
    except FileNotFoundError:
        return None
    if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
        return None
    marker = submodule / ".git"
    try:
        marker_st = os.lstat(marker)
    except FileNotFoundError:
        return None
    if stat.S_ISLNK(marker_st.st_mode):
        return None
    if not (stat.S_ISREG(marker_st.st_mode) or stat.S_ISDIR(marker_st.st_mode)):
        return None
    return marker

def collect(repo, commit, parent_roots, mode):
    repo = pathlib.Path(os.path.normpath(str(repo)))
    own_roots = git_roots(repo)
    validate_roots(repo, own_roots, parent_roots)
    hierarchy = list(dict.fromkeys([*(parent_roots or []), *own_roots]))

    for relative, target_oid in gitlinks(repo, commit):
        child = safe_tree_path(repo, relative)
        if populated_marker(child) is None:
            continue

        child_own = git_roots(child)
        validate_roots(child, child_own, hierarchy)

        child_commit = target_oid
        if mode == "current":
            head = git(child, "rev-parse", "HEAD", check=False)
            if head.returncode != 0 or not head.stdout.strip():
                hierarchy.extend(root for root in child_own if root not in hierarchy)
                continue
            child_commit = head.stdout.strip()
        else:
            target = git(child, "cat-file", "-e", f"{target_oid}^{{commit}}", check=False)
            if target.returncode != 0:
                head = git(child, "rev-parse", "HEAD", check=False)
                if head.returncode != 0 or not head.stdout.strip():
                    hierarchy.extend(root for root in child_own if root not in hierarchy)
                    continue
                child_commit = head.stdout.strip()

        child_roots = collect(child, child_commit, hierarchy, mode)
        hierarchy.extend(root for root in child_roots if root not in hierarchy)

    return hierarchy

current_head = git(app, "rev-parse", "HEAD").stdout.strip()
roots = collect(app, current_head, None, "current")
if rollback_commit and rollback_commit != current_head:
    rollback_roots = collect(app, rollback_commit, None, "rollback")
    roots.extend(root for root in rollback_roots if root not in roots)

protected = [app, *roots]

def resolved(path):
    try:
        return pathlib.Path(path).resolve(strict=False)
    except (OSError, RuntimeError):
        return None

def under(child, parent):
    child = resolved(child)
    parent = resolved(parent)
    if child is None or parent is None:
        return False
    try:
        child.relative_to(parent)
        return True
    except ValueError:
        return False

def points_into_protected(value, cwd):
    try:
        p = pathlib.Path(value)
        if not p.is_absolute():
            p = cwd / p
        p = p.resolve(strict=False)
    except (OSError, RuntimeError):
        return True
    return any(under(p, root) or under(root, p) for root in protected)

def read_environment(proc):
    try:
        raw = (proc / "environ").read_bytes()
    except (FileNotFoundError, ProcessLookupError):
        return None
    except PermissionError as exc:
        raise RuntimeError(f"cannot inspect environment for git process {proc.name}") from exc
    env = {}
    for item in raw.split(b"\0"):
        if b"=" in item:
            key, value = item.split(b"=", 1)
            env[os.fsdecode(key)] = os.fsdecode(value)
    return env

def process_is_git(proc):
    names = []
    try:
        raw = (proc / "cmdline").read_bytes()
        argv = [os.fsdecode(x) for x in raw.split(b"\0") if x]
    except (FileNotFoundError, ProcessLookupError):
        return None, None
    except PermissionError as exc:
        raise RuntimeError(f"cannot inspect cmdline for process {proc.name}") from exc
    if argv:
        names.append(pathlib.Path(argv[0]).name)
    try:
        target = os.readlink(proc / "exe")
        if target.endswith(" (deleted)"):
            target = target[:-10]
        names.append(pathlib.Path(target).name)
    except (FileNotFoundError, ProcessLookupError):
        pass
    except PermissionError as exc:
        raise RuntimeError(f"cannot inspect executable for process {proc.name}") from exc
    return any(name == "git" or name.startswith("git-") for name in names), argv

def git_process_references_checkout(proc, argv):
    try:
        cwd = (proc / "cwd").resolve()
    except (FileNotFoundError, ProcessLookupError):
        return False
    except PermissionError as exc:
        raise RuntimeError(f"cannot inspect cwd for git process {proc.name}") from exc
    if any(under(cwd, root) or under(root, cwd) for root in protected):
        return True

    env = read_environment(proc)
    if env is None:
        return False

    def inspect_value(value):
        return bool(value) and points_into_protected(value, cwd)

    i = 1
    while i < len(argv):
        arg = argv[i]
        value = None
        if arg.startswith("--git-dir=") or arg.startswith("--work-tree=") or arg.startswith("--separate-git-dir="):
            value = arg.split("=", 1)[1]
        elif arg in ("--git-dir", "--work-tree", "--separate-git-dir", "-C"):
            if i + 1 >= len(argv):
                raise RuntimeError(f"malformed git selector in process {proc.name}")
            value = argv[i + 1]
            i += 1
        elif arg.startswith("-C") and arg != "-C":
            value = arg[2:]
        elif arg == "-c":
            if i + 1 >= len(argv):
                raise RuntimeError(f"malformed git -c selector in process {proc.name}")
            config = argv[i + 1]
            i += 1
            if "=" in config:
                key, config_value = config.split("=", 1)
                if key.lower() == "core.worktree" and inspect_value(config_value):
                    return True
        elif arg.startswith("-c") and arg != "-c":
            config = arg[2:]
            if "=" in config:
                key, config_value = config.split("=", 1)
                if key.lower() == "core.worktree" and inspect_value(config_value):
                    return True
        elif arg.startswith("--config-env="):
            spec = arg.split("=", 1)[1]
            if "=" not in spec:
                raise RuntimeError(f"malformed --config-env in process {proc.name}")
            key, env_name = spec.split("=", 1)
            config_value = env.get(env_name)
            if not env_name or config_value is None:
                raise RuntimeError(f"unresolvable --config-env in process {proc.name}")
            if key.lower() == "core.worktree" and inspect_value(config_value):
                return True
        elif arg.startswith("-") and "=" in arg:
            option_value = arg.split("=", 1)[1]
            if not option_value:
                raise RuntimeError(f"malformed Git option in process {proc.name}")
            if inspect_value(option_value):
                return True
        elif not arg.startswith("-") and inspect_value(arg):
            return True
        if value is not None and inspect_value(value):
            return True
        i += 1

    for key in (
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
        "GIT_OBJECT_DIRECTORY", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL",
    ):
        if inspect_value(env.get(key)):
            return True
    for value in env.get("GIT_ALTERNATE_OBJECT_DIRECTORIES", "").split(os.pathsep):
        if inspect_value(value):
            return True

    count = env.get("GIT_CONFIG_COUNT")
    if count is not None:
        try:
            count_i = int(count)
        except ValueError as exc:
            raise RuntimeError(f"malformed GIT_CONFIG_COUNT in process {proc.name}") from exc
        if count_i < 0 or count_i > 10000:
            raise RuntimeError(f"unsafe GIT_CONFIG_COUNT in process {proc.name}")
        for n in range(count_i):
            key = env.get(f"GIT_CONFIG_KEY_{n}")
            value = env.get(f"GIT_CONFIG_VALUE_{n}")
            if key is None or value is None:
                raise RuntimeError(f"incomplete Git config environment in process {proc.name}")
            if key.lower() == "core.worktree" and inspect_value(value):
                return True

    probe_env = {
        "PATH": os.environ.get("PATH", "/usr/bin:/bin"),
        "LANG": "C",
        "LC_ALL": "C",
    }
    for key in (
        "HOME", "XDG_CONFIG_HOME", "GIT_DIR", "GIT_WORK_TREE",
        "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_COUNT",
        "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_SYSTEM", "GIT_CONFIG_GLOBAL",
        "GIT_CONFIG_NOSYSTEM", "GIT_CEILING_DIRECTORIES",
        "GIT_DISCOVERY_ACROSS_FILESYSTEM",
    ):
        if key in env:
            probe_env[key] = env[key]
    for key, value in env.items():
        if key.startswith("GIT_CONFIG_KEY_") or key.startswith("GIT_CONFIG_VALUE_"):
            probe_env[key] = value

    probe_args = ["git"]
    i = 1
    while i < len(argv):
        arg = argv[i]
        if arg == "--":
            break
        if arg in ("-C", "-c", "--git-dir", "--work-tree"):
            if i + 1 >= len(argv):
                raise RuntimeError(f"malformed Git global selector in process {proc.name}")
            probe_args.extend((arg, argv[i + 1]))
            i += 2
            continue
        if (
            (arg.startswith("-C") and arg != "-C")
            or (arg.startswith("-c") and arg != "-c")
            or arg.startswith("--git-dir=")
            or arg.startswith("--work-tree=")
        ):
            probe_args.append(arg)
            i += 1
            continue
        if arg.startswith("--config-env="):
            spec = arg.split("=", 1)[1]
            if "=" not in spec:
                raise RuntimeError(f"malformed --config-env in process {proc.name}")
            _config_key, env_name = spec.split("=", 1)
            if not env_name or env_name not in env:
                raise RuntimeError(f"unresolvable --config-env in process {proc.name}")
            probe_env[env_name] = env[env_name]
            probe_args.append(arg)
            i += 1
            continue
        if arg.startswith("-"):
            i += 1
            continue
        break

    resolved_any = False
    resolved_git_dir = False
    resolved_common_dir = False
    for selector in ("--show-toplevel", "--git-dir", "--git-common-dir"):
        try:
            probe = subprocess.run(
                [
                    *probe_args, "-c", "safe.directory=*",
                    "rev-parse", "--path-format=absolute", selector,
                ],
                cwd=str(cwd),
                env=probe_env,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=2,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise RuntimeError(f"timed out resolving Git paths for process {proc.name}") from exc
        if probe.returncode != 0:
            continue
        effective = probe.stdout.strip()
        if not effective:
            raise RuntimeError(f"Git returned empty {selector} for process {proc.name}")
        resolved_any = True
        resolved_git_dir = resolved_git_dir or selector == "--git-dir"
        resolved_common_dir = resolved_common_dir or selector == "--git-common-dir"
        if points_into_protected(effective, cwd):
            return True

    if resolved_any and not (resolved_git_dir and resolved_common_dir):
        raise RuntimeError(f"cannot resolve complete Git metadata paths for process {proc.name}")
    if not resolved_any and ("GIT_CONFIG_PARAMETERS" in env or "GIT_CONFIG_COUNT" in env):
        raise RuntimeError(f"cannot resolve live Git configuration for process {proc.name}")

    try:
        fds = list((proc / "fd").iterdir())
    except (FileNotFoundError, ProcessLookupError):
        return False
    except PermissionError as exc:
        raise RuntimeError(f"cannot inspect fds for git process {proc.name}") from exc
    for fd in fds:
        try:
            target = fd.resolve(strict=False)
        except (FileNotFoundError, ProcessLookupError):
            continue
        except PermissionError as exc:
            raise RuntimeError(f"cannot resolve fd for git process {proc.name}") from exc
        if any(under(target, root) for root in protected):
            return True
    return False

def assert_no_related_git():
    for proc in pathlib.Path("/proc").iterdir():
        if not proc.name.isdigit():
            continue
        result = process_is_git(proc)
        if result == (None, None):
            continue
        is_git, argv = result
        if is_git and git_process_references_checkout(proc, argv):
            raise RuntimeError(f"live git process {proc.name} still references deployment checkout")

def lock_is_open(lock):
    try:
        lst = os.lstat(lock)
    except FileNotFoundError:
        return False
    wanted = (lst.st_dev, lst.st_ino)
    for proc in pathlib.Path("/proc").iterdir():
        if not proc.name.isdigit():
            continue
        try:
            fds = list((proc / "fd").iterdir())
        except (FileNotFoundError, ProcessLookupError):
            continue
        except PermissionError as exc:
            raise RuntimeError(f"cannot inspect process {proc.name} fds before lock cleanup") from exc
        for fd in fds:
            try:
                st = fd.stat()
            except (FileNotFoundError, ProcessLookupError):
                continue
            except PermissionError as exc:
                raise RuntimeError(f"cannot inspect fd for process {proc.name}") from exc
            if (st.st_dev, st.st_ino) == wanted:
                return True
    return False

def lock_files(root):
    for dirpath, dirnames, filenames in os.walk(root, topdown=True, followlinks=False):
        base = pathlib.Path(dirpath)
        kept = []
        for name in dirnames:
            child = base / name
            try:
                st = os.lstat(child)
            except FileNotFoundError:
                continue
            if stat.S_ISDIR(st.st_mode) and not stat.S_ISLNK(st.st_mode):
                kept.append(name)
        dirnames[:] = kept
        for name in filenames:
            if name.endswith(".lock"):
                yield base / name

assert_no_related_git()
for root in roots:
    st = os.lstat(root)
    if not stat.S_ISDIR(st.st_mode) or stat.S_ISLNK(st.st_mode):
        raise RuntimeError(f"git metadata root is not a real directory: {root}")
    for lock in sorted(lock_files(root), key=str):
        try:
            st = os.lstat(lock)
        except FileNotFoundError:
            continue
        if not stat.S_ISREG(st.st_mode) or stat.S_ISLNK(st.st_mode):
            raise RuntimeError(f"refusing to remove non-regular git lock: {lock}")
        assert_no_related_git()
        if lock_is_open(lock):
            raise RuntimeError(f"refusing to remove open git lock: {lock}")
        parent_fd = os.open(lock.parent, os.O_RDONLY | os.O_DIRECTORY | NOFOLLOW)
        try:
            os.unlink(lock.name, dir_fd=parent_fd)
            os.fsync(parent_fd)
        finally:
            os.close(parent_fd)
assert_no_related_git()
PY
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
  local commit="$1" binary="$2" live_digest
  # Prove both sides of the byte-exact integrity traversal. A systemd restart
  # or exec transition between these barriers cannot make a different process
  # inherit evidence gathered for the previous MainPID.
  live_digest="$(live_runtime_snapshot_sha256)" || return 1
  [[ "$live_digest" == "$binary" ]] || return 1
  artifact_integrity "$commit" "$binary" || return 1
  live_digest="$(live_runtime_snapshot_sha256)" || return 1
  [[ "$live_digest" == "$binary" ]]
}

# Commit candidate state only while the normal service writer is stopped.
# The recovery journal deliberately survives this boundary and the final
# long-lived start. It is retained in phase=committed as a last-known-good
# recovery point instead of being deleted before that final instance is proven.
finalize_quiescent() {
  local commit="$1" binary="$2" manifest="$3"
  stop_service_quiescent || return 1
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
  stop_service_quiescent || return 1
  reconcile_stale_git_locks "$commit" || return 1
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
  local systemd=false local_http=false public_https=true release_revision=false service_smoke=true artifact_fence=false runtime_process=false source_tree=false state_integrity=false runtime_digest=false runtime_present=false
  local actual disk_actual final_actual

  # The smoke hook runs first and cannot leave descendants behind. Accept live
  # artifact evidence only from one stable service instance that both sees the
  # artifacts read-only and executes the exact EC_APP_BIN inode.
  if [[ -n "$EC_SMOKE_SCRIPT" ]]; then
    service_smoke=false
    run_smoke >/dev/null 2>&1 && service_smoke=true
  fi

  systemctl is-active --quiet "$EC_SERVICE_UNIT" && systemd=true
  actual="$(live_runtime_snapshot_sha256 2>/dev/null || true)"
  if [[ "$actual" =~ ^[0-9a-f]{64}$ ]]; then
    artifact_fence=true
    runtime_process=true
  else
    # Preserve evidence about what MainPID actually executes even when the
    # configured-path binding or mount fence is invalid.
    actual="$(running_runtime_sha256 2>/dev/null || true)"
  fi
  curl -fsS --max-time 10 "$EC_LOCAL_URL" >/dev/null 2>&1 && local_http=true
  if [[ "$EC_CHECK_PUBLIC" == 1 ]]; then
    public_https=false; curl -fsS --max-time 15 "$EC_PUBLIC_URL" >/dev/null 2>&1 && public_https=true
  fi
  [[ "$deployed" == "$expected" ]] && release_revision=true

  # deployed_runtime_sha256 is the digest of the executable held by MainPID,
  # not merely bytes present at EC_APP_BIN. Keep the configured-file digest as
  # an independent consistency input.
  disk_actual="$(sha256_file "$EC_APP_BIN" 2>/dev/null || true)"
  [[ "$actual" =~ ^[0-9a-f]{64}$ ]] && runtime_present=true
  if [[ "$(git -C "$EC_APP_DIR" rev-parse HEAD 2>/dev/null || true)" == "$deployed" ]] && source_tree_exact "$deployed"; then source_tree=true; fi
  [[ "$runtime_process" == true && "$actual" == "$state_binary" && "$disk_actual" == "$state_binary" ]] && state_integrity=true
  [[ "$runtime_process" == true && "$actual" == "$expected_binary" && "$disk_actual" == "$expected_binary" ]] && runtime_digest=true

  # Close the observation window with a second live snapshot. If the service
  # restarted, exec'd a wrapper, or lost its mount fence during the remaining
  # checks, invalidate all runtime-bound evidence instead of signing stale facts.
  if [[ "$runtime_process" == true ]]; then
    final_actual="$(live_runtime_snapshot_sha256 2>/dev/null || true)"
    if [[ ! "$final_actual" =~ ^[0-9a-f]{64}$ || "$final_actual" != "$actual" ]]; then
      actual="$final_actual"
      artifact_fence=false
      runtime_process=false
      state_integrity=false
      runtime_digest=false
      [[ "$actual" =~ ^[0-9a-f]{64}$ ]] || runtime_present=false
    fi
  fi

  python3 - "$actual" "$systemd" "$local_http" "$public_https" "$release_revision" "$service_smoke" "$artifact_fence" "$runtime_process" "$source_tree" "$state_integrity" "$runtime_digest" "$runtime_present" <<'PY'
import json,sys
actual=sys.argv[1] if len(sys.argv[1]) == 64 else None
n=["systemd","local_http","public_https","release_revision","service_smoke","artifact_fence","runtime_process","source_tree","state_integrity","runtime_digest","runtime_present"]
checks=dict(zip(n,[x=="true" for x in sys.argv[2:]]))
print(json.dumps({"runtime_sha256":actual,"checks":checks},sort_keys=True,separators=(",",":")))
PY
}

status_from_checks() {
  python3 - "$1" <<'PY'
import json,sys
c=json.loads(sys.argv[1]); mandatory=("systemd","local_http","release_revision","service_smoke","artifact_fence","runtime_process","source_tree","state_integrity","runtime_digest","runtime_present")
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

  if ! stop_service_quiescent; then
    rm -rf "$dir"; rollback_transaction || die "Stop/quiescence failed and rollback failed"; return 1
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

case "$ACTION" in
  recover)
    # Installer coordination path: reconcile only an already-journaled
    # deployment transaction. Do not bootstrap or contact release/attestation
    # endpoints, so the installer can establish a stable baseline first.
    recover_transaction
    ;;
  run|update)
    bootstrap_state
    recover_transaction
    update_release
    ;;
  attest|health)
    bootstrap_state
    recover_transaction
    attest
    ;;
  *) die "Usage: $0 [run|update|attest|health|recover]" ;;
esac
