#!/bin/bash
set -Eeuo pipefail

export PATH="/usr/sbin:/usr/bin:/sbin:/bin"
export HOME="/root"
umask 077
unset BASH_ENV ENV CDPATH PYTHONPATH PYTHONHOME PYTHONSTARTUP PYTHONINSPECT LD_PRELOAD LD_LIBRARY_PATH
unset XDG_CONFIG_HOME CURL_HOME CURL_CA_BUNDLE SSL_CERT_FILE SSL_CERT_DIR
unset GIT_CONFIG_COUNT GIT_CONFIG_PARAMETERS GIT_CONFIG_GLOBAL GIT_CONFIG_SYSTEM GIT_DIR GIT_WORK_TREE

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

# This file is a privileged second-stage installer. Never execute it directly
# from a user-writable checkout. The documented bootstrap first copies these
# exact reviewed bytes into a root-owned private /run directory and verifies
# EC_INSTALLER_SHA256 before Bash parses this body as root.
INSTALLER_TRUSTED_STAGE="${EC_INSTALLER_TRUSTED_STAGE:-}"
INSTALLER_SOURCE_ROOT="${EC_INSTALLER_SOURCE_ROOT:-}"
EXPECTED_INSTALLER_SHA256="${EC_INSTALLER_SHA256:-}"
EXPECTED_AGENT_SHA256="${EC_NATIVE_AGENT_SHA256:-}"
AGENT_SOURCE_PATH="${EC_NATIVE_AGENT_BINARY:-}"

case "$INSTALLER_TRUSTED_STAGE" in
  /run/ec-deployment-attestation-installer.*) ;;
  *)
    echo "Refusing privileged execution without a trusted root-owned installer stage." >&2
    exit 1
    ;;
esac
[[ -d "$INSTALLER_TRUSTED_STAGE" && ! -L "$INSTALLER_TRUSTED_STAGE" ]] || {
  echo "Trusted installer staging directory is invalid." >&2
  exit 1
}
[[ "$(stat -c '%u:%a' -- "$INSTALLER_TRUSTED_STAGE")" == "0:700" ]] || {
  echo "Trusted installer staging directory must be root-owned mode 0700." >&2
  exit 1
}

cleanup_trusted_stage_early() {
  local rc=$?
  trap - EXIT
  rm -rf -- "$INSTALLER_TRUSTED_STAGE" || true
  exit "$rc"
}
trap cleanup_trusted_stage_early EXIT

[[ "${BASH_SOURCE[0]}" == "$INSTALLER_TRUSTED_STAGE/install-id-exergism.sh" ]] || {
  echo "Installer must execute from the verified root-owned staging pathname." >&2
  exit 1
}
[[ -f "${BASH_SOURCE[0]}" && ! -L "${BASH_SOURCE[0]}" ]] || {
  echo "Trusted installer snapshot is not a regular file." >&2
  exit 1
}
[[ "$(stat -c '%u:%a' -- "${BASH_SOURCE[0]}")" == "0:500" ]] || {
  echo "Trusted installer snapshot must be root-owned mode 0500." >&2
  exit 1
}
[[ "$EXPECTED_INSTALLER_SHA256" =~ ^[0-9a-f]{64}$ ]] || {
  echo "EC_INSTALLER_SHA256 must be the reviewed lowercase SHA-256 of the installer." >&2
  exit 1
}
actual_installer_sha256="$(sha256sum -- "${BASH_SOURCE[0]}" | awk '{print $1}')"
[[ "$actual_installer_sha256" == "$EXPECTED_INSTALLER_SHA256" ]] || {
  echo "Trusted installer SHA-256 does not match the reviewed digest." >&2
  exit 1
}
[[ -n "$INSTALLER_SOURCE_ROOT" ]] || {
  echo "EC_INSTALLER_SOURCE_ROOT must point to the reviewed checkout root." >&2
  exit 1
}
[[ "$EXPECTED_AGENT_SHA256" =~ ^[0-9a-f]{64}$ ]] || {
  echo "EC_NATIVE_AGENT_SHA256 must be the reviewed lowercase SHA-256 of the Native AOT binary." >&2
  exit 1
}
[[ -n "$AGENT_SOURCE_PATH" ]] || {
  echo "EC_NATIVE_AGENT_BINARY must point to the reviewed Native AOT binary." >&2
  exit 1
}

[[ -x /usr/bin/python3 ]] || {
  echo "Required dependency not found: /usr/bin/python3" >&2
  exit 1
}

/usr/bin/python3 -I - \
  "$INSTALLER_SOURCE_ROOT" \
  "$INSTALLER_TRUSTED_STAGE" \
  "$AGENT_SOURCE_PATH" \
  "$EXPECTED_AGENT_SHA256" <<'PY'
import hashlib
import os
import stat
import sys

source_root, stage, agent_source, expected_agent_sha256 = sys.argv[1:5]
O_NOFOLLOW = getattr(os, "O_NOFOLLOW", 0)
O_CLOEXEC = os.O_CLOEXEC

def identity(st):
    return (
        st.st_dev,
        st.st_ino,
        st.st_mode,
        st.st_size,
        st.st_mtime_ns,
        st.st_ctime_ns,
    )

def copy_fd_verified(source_fd, destination, mode, label, require_executable=False):
    before = os.fstat(source_fd)
    if not stat.S_ISREG(before.st_mode):
        raise SystemExit(f"{label} must be a regular file")
    if require_executable and before.st_mode & 0o111 == 0:
        raise SystemExit(f"{label} must be executable")

    os.makedirs(os.path.dirname(destination), mode=0o700, exist_ok=True)
    dest_fd = os.open(
        destination,
        os.O_WRONLY | os.O_CREAT | os.O_EXCL | O_CLOEXEC | O_NOFOLLOW,
        mode,
    )
    digest = hashlib.sha256()
    offset = 0
    try:
        while True:
            chunk = os.pread(source_fd, 1024 * 1024, offset)
            if not chunk:
                break
            offset += len(chunk)
            digest.update(chunk)
            view = memoryview(chunk)
            while view:
                written = os.write(dest_fd, view)
                view = view[written:]
        os.fchmod(dest_fd, mode)
        os.fchown(dest_fd, 0, 0)
        os.fsync(dest_fd)
    finally:
        os.close(dest_fd)

    if identity(os.fstat(source_fd)) != identity(before):
        raise SystemExit(f"{label} changed while being snapshotted")

    verify = hashlib.sha256()
    offset = 0
    while True:
        chunk = os.pread(source_fd, 1024 * 1024, offset)
        if not chunk:
            break
        offset += len(chunk)
        verify.update(chunk)
    if verify.digest() != digest.digest() or identity(os.fstat(source_fd)) != identity(before):
        raise SystemExit(f"{label} changed during snapshot verification")
    return digest.hexdigest()

def open_repo_file_no_symlinks(root_fd, relative):
    parts = relative.split("/")
    if not parts or any(part in ("", ".", "..") for part in parts):
        raise SystemExit(f"unsafe repository input path: {relative}")
    current = os.dup(root_fd)
    try:
        for part in parts[:-1]:
            next_fd = os.open(
                part,
                os.O_RDONLY | os.O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW,
                dir_fd=current,
            )
            os.close(current)
            current = next_fd
        return os.open(
            parts[-1],
            os.O_RDONLY | O_CLOEXEC | O_NOFOLLOW,
            dir_fd=current,
        )
    finally:
        os.close(current)

repo_inputs = (
    ("install/validate-id-exergism-generation.sh", 0o500, "3964e637b5950eae6f17a77fef7119f342f8fa41767fafe0359c085981943f4d"),
    ("install/verify-id-exergism-artifact-fence.sh", 0o500, "6001cc876a7e2a9d390f7aa62ed86d5a355af005798faee8de9d6744c515e3a9"),
    ("agent/validate-release-manifest.py", 0o500, "1d9c3f889e89b8ff5acb8659504655e7237ad32f5e3e4c4bd29fb719150ab481"),
    ("spec/release-manifest-v0.1.schema.json", 0o400, "53338bbdbb822c8a94bfbc45246af56017165a0af14d1fea818c6fc9d23120b2"),
    ("install/finalize-id-exergism-recovery.sh", 0o500, "762df4fd1f6d7ff9f8f2fa0ad35a63225733dc2a340f8839362eb9bc028fd910"),
    ("packaging/id-exergism-install-recovery-finalize.service", 0o400, "758f1921d2f344d071a141fdd7611ba72f44dcaa71288e1fe07c7868278552b0"),
    ("install/recover-id-exergism-install.sh", 0o500, "578029289e593a66155923154319dadfb58774d48c536b77e90edae1993627e3"),
    ("packaging/id-exergism-install-recovery.service", 0o400, "6b4590d37a30c8f8567b07ee69e2f9f74454f9f7e39e5fd102ff4dbe9a732d6c"),
    ("packaging/id-exergism-install-recovery-interlock.conf", 0o400, "fed104865dbd437dbb9397f5e9982942ab691ec8befc6a2d693dd2e6f73001fd"),
    ("packaging/id-exergism-agent-recovery-interlock.conf", 0o400, "9a132fe45b037d2082ebec87948743180ffc19d1588a1d128937ddda912440d9"),
    ("examples/id.exergism.org.env.example", 0o400, "1b1e7318c4a6d1341185e27a79d46b117ecbb14b759e6ce194fc6a0795069659"),
    ("examples/id.exergism.org-smoke.sh", 0o500, "a78cd1f64258c1a4252a4c06a6a941b0490dbd5fc0f67159ddd5dfa71ff0d98c"),
    ("packaging/ec-deployment-attestation@.service", 0o400, "9c2f627a589dfa418f3334d6fe68722cc1c3f50d5324b331497e7676de93ff94"),
    ("packaging/ec-deployment-attestation@.timer", 0o400, "a27cdffdd9e4e8d4cfeebc78f7c83836d3944314ba0c0559229670c5dbfa5b56"),
    ("packaging/id-exergism-artifact-fence.conf", 0o400, "3c0eeb7e8ce627d199b6093a3f0a267a64f23bd73875ec779c1a7d9bebcda42e"),
)

repo_stage = os.path.join(stage, "repo")
os.makedirs(repo_stage, mode=0o700, exist_ok=False)
os.chmod(repo_stage, 0o700)
os.chown(repo_stage, 0, 0)

# Bind the AOT candidate before traversing the mutable checkout.
agent_fd = os.open(agent_source, os.O_RDONLY | O_CLOEXEC | O_NOFOLLOW)
root_fd = os.open(source_root, os.O_RDONLY | os.O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
try:
    for relative, mode, expected_sha256 in repo_inputs:
        source_fd = open_repo_file_no_symlinks(root_fd, relative)
        try:
            actual_sha256 = copy_fd_verified(
                source_fd,
                os.path.join(repo_stage, relative),
                mode,
                f"repository input {relative}",
            )
            if actual_sha256 != expected_sha256:
                raise SystemExit(
                    f"repository input digest mismatch for {relative}: "
                    f"expected={expected_sha256} actual={actual_sha256}"
                )
        finally:
            os.close(source_fd)
finally:
    os.close(root_fd)

try:
    actual_agent_sha256 = copy_fd_verified(
        agent_fd,
        os.path.join(stage, "native-agent"),
        0o500,
        "EC_NATIVE_AGENT_BINARY",
        require_executable=True,
    )
    if actual_agent_sha256 != expected_agent_sha256:
        raise SystemExit(
            "EC_NATIVE_AGENT_BINARY SHA-256 mismatch: "
            f"expected={expected_agent_sha256} actual={actual_agent_sha256}"
        )
finally:
    os.close(agent_fd)

for directory, _, _ in os.walk(stage, topdown=False):
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
PY

[[ -d "$INSTALLER_TRUSTED_STAGE/repo" &&
   ! -L "$INSTALLER_TRUSTED_STAGE/repo" ]] || {
  echo "Trusted repository snapshot is missing." >&2
  exit 1
}
[[ "$(stat -c '%u:%a' -- "$INSTALLER_TRUSTED_STAGE/repo")" == "0:700" ]] || {
  echo "Trusted repository snapshot must be root-owned mode 0700." >&2
  exit 1
}
[[ -f "$INSTALLER_TRUSTED_STAGE/native-agent" &&
   ! -L "$INSTALLER_TRUSTED_STAGE/native-agent" ]] || {
  echo "Trusted Native AOT snapshot is missing." >&2
  exit 1
}
[[ "$(stat -c '%u:%a' -- "$INSTALLER_TRUSTED_STAGE/native-agent")" == "0:500" ]] || {
  echo "Trusted Native AOT snapshot must be root-owned mode 0500." >&2
  exit 1
}

SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
AGENT_RUN_UNIT="ec-deployment-attestation@${SERVICE}.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"
RECOVERY_UNIT="id-exergism-install-recovery.service"

AGENT="/usr/local/libexec/ec-deployment-agent"
AGENT_SOURCE="$INSTALLER_TRUSTED_STAGE/native-agent"
AGENT_INSTALL_SOURCE="$AGENT_SOURCE"
NATIVE_AGENT_STAGE="$INSTALLER_TRUSTED_STAGE/work"
REPO_INPUT_STAGE="$INSTALLER_TRUSTED_STAGE/repo"
TMP_MANIFEST=""
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
VALIDATOR="/usr/local/libexec/ec-id-generation-validator"
RECOVERY_FINALIZER="/usr/local/libexec/ec-deployment-install-recovery-finalize"
ARTIFACT_FENCE_AUDITOR="/usr/local/libexec/ec-id-production-artifact-fence"
MANIFEST_VALIDATOR="/usr/local/libexec/ec-release-manifest-validator"
MANIFEST_SCHEMA="/usr/local/libexec/release-manifest-v0.1.schema.json"
APP_DIR="/srv/id.exergism.org"
APP_BIN="/usr/local/bin/idresolver"
AGENT_SERVICE_UNIT="/etc/systemd/system/ec-deployment-attestation@.service"
AGENT_TIMER_UNIT="/etc/systemd/system/ec-deployment-attestation@.timer"
ENV_FILE="/etc/ec-deployment-attestation/${SERVICE}.env"
FENCE_DROPIN_DIR="/etc/systemd/system/${TARGET_UNIT}.d"
FENCE_DROPIN="${FENCE_DROPIN_DIR}/90-ec-deployment-attestation-artifact-fence.conf"

RECOVERY_HELPER="/usr/local/libexec/ec-deployment-install-recovery"
RECOVERY_UNIT_PATH="/etc/systemd/system/${RECOVERY_UNIT}"
RECOVERY_FINALIZE_UNIT="id-exergism-install-recovery-finalize.service"
RECOVERY_FINALIZE_UNIT_PATH="/etc/systemd/system/${RECOVERY_FINALIZE_UNIT}"
TARGET_RECOVERY_INTERLOCK="${FENCE_DROPIN_DIR}/80-ec-deployment-attestation-install-recovery.conf"
AGENT_RECOVERY_DROPIN_DIR="/etc/systemd/system/${AGENT_RUN_UNIT}.d"
AGENT_RECOVERY_INTERLOCK="${AGENT_RECOVERY_DROPIN_DIR}/80-ec-deployment-attestation-install-recovery.conf"
LEGACY_TIMER_RECOVERY_INTERLOCK="/etc/systemd/system/${TIMER_UNIT}.d/80-ec-deployment-attestation-install-recovery.conf"

INSTALL_STATE_PARENT="/var/lib/ec-deployment-attestation"
INSTALL_STATE_ROOT="${INSTALL_STATE_PARENT}/install"
INSTALL_PENDING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.pending"
INSTALL_VALIDATED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.validated"
INSTALL_RECOVERING_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovering"
INSTALL_RECOVERED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.recovered"
INSTALL_COMMITTED_DIR="${INSTALL_STATE_ROOT}/${SERVICE}.committed"
INSTALL_LOCK="/run/lock/ec-deployment-attestation-install.lock"
AGENT_COORDINATION_LOCK="/run/lock/ec-deployment-attestation-${SERVICE}.agent.lock"
BOOT_ID_FILE="/proc/sys/kernel/random/boot_id"

path_exists_any() {
  [[ -e "$1" || -L "$1" ]]
}

require_real_phase_or_absent() {
  local path="$1" owner mode
  if path_exists_any "$path"; then
    [[ -d "$path" && ! -L "$path" ]] || {
      echo "CRITICAL: installer transaction path is not a real directory: $path" >&2
      return 1
    }
    owner="$(stat -c '%u' -- "$path")" || return 1
    mode="$(stat -c '%a' -- "$path")" || return 1
    [[ "$owner" == 0 && "$mode" == 700 ]] || {
      echo "CRITICAL: installer transaction path is not root-owned mode 0700: $path" >&2
      return 1
    }
  fi
}

validate_phase_paths() {
  require_real_phase_or_absent "$INSTALL_PENDING_DIR"
  require_real_phase_or_absent "$INSTALL_VALIDATED_DIR"
  require_real_phase_or_absent "$INSTALL_RECOVERING_DIR"
  require_real_phase_or_absent "$INSTALL_RECOVERED_DIR"
  require_real_phase_or_absent "$INSTALL_COMMITTED_DIR"
}

MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"

for command in curl git python3 sha256sum systemctl systemd-run systemd-analyze flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp cat sleep stat; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required dependency not found: $command" >&2
    exit 1
  }
done

cleanup_installer_temporaries() {
  if [[ -n "$TMP_MANIFEST" ]]; then
    rm -f -- "$TMP_MANIFEST" || true
    TMP_MANIFEST=""
  fi
  if [[ -n "$INSTALLER_TRUSTED_STAGE" ]]; then
    rm -rf -- "$INSTALLER_TRUSTED_STAGE" || true
    INSTALLER_TRUSTED_STAGE=""
    NATIVE_AGENT_STAGE=""
    REPO_INPUT_STAGE=""
  fi
}
trap cleanup_installer_temporaries EXIT

# All executable/configuration inputs were pinned by the first-stage bootstrap.
# The trusted stage never reopens the original checkout or AOT source.
mkdir -m 0700 "$NATIVE_AGENT_STAGE"
chown root:root "$NATIVE_AGENT_STAGE"

if [[ "${1:-}" == "--bootstrap-self-test" ]]; then
  cleanup_installer_temporaries
  trap - EXIT
  exit 0
fi

exec 9>"$INSTALL_LOCK"
flock -n 9 || {
  echo "Another Deployment Attestation installation/recovery is already running." >&2
  exit 1
}

durable_sync_paths() {
  /usr/bin/python3 -I - "$@" <<'PY'
import os
import pathlib
import stat
import sys

for raw in sys.argv[1:]:
    p = pathlib.Path(raw)
    try:
        st = os.lstat(p)
    except FileNotFoundError:
        continue
    if stat.S_ISREG(st.st_mode):
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    elif stat.S_ISDIR(st.st_mode):
        fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
PY
}

durable_sync_ancestor_chain() {
  /usr/bin/python3 -I - "$@" <<'PY'
import os
import pathlib
import sys

seen = set()
for raw in sys.argv[1:]:
    p = pathlib.Path(raw).resolve(strict=False)
    if not p.exists():
        p = p.parent
    if p.exists() and p.is_file():
        p = p.parent
    while True:
        key = str(p)
        if key not in seen and p.exists() and p.is_dir():
            fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
            seen.add(key)
        if p == p.parent:
            break
        p = p.parent
PY
}

atomic_install_root_file() {
  local source="$1" dest="$2" mode="$3"
  local dir base tmp
  dir="${dest%/*}"
  base="${dest##*/}"
  tmp="$(mktemp "${dir}/.${base}.tmp.XXXXXX")"
  trap 'rm -f "$tmp"' RETURN

  install -o root -g root -m "$mode" "$source" "$tmp"
  # Content reaches stable storage before the rename. Until rename, the old
  # destination remains intact; after rename, fsync the directory entry.
  durable_sync_paths "$tmp"
  mv -fT -- "$tmp" "$dest"
  durable_sync_paths "$dest" "$dir"

  trap - RETURN
}


install -d -m 0755 /usr/local/libexec
install -d -m 0755 /etc/ec-deployment-attestation
install -d -m 0700 /etc/ec-deployment-attestation/secrets
install -d -o root -g root -m 0755 "$FENCE_DROPIN_DIR"
install -d -o root -g root -m 0755 "$AGENT_RECOVERY_DROPIN_DIR"
install -d -o root -g root -m 0700 "$INSTALL_STATE_PARENT"
install -d -o root -g root -m 0700 "$INSTALL_STATE_ROOT"
durable_sync_ancestor_chain   /usr/local/libexec   /etc/ec-deployment-attestation/secrets   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   "$INSTALL_STATE_ROOT"

validate_phase_paths

manager_version="$(systemctl show --property=Version --value 2>/dev/null || true)"
systemd_version="$(printf '%s\n' "$manager_version" | sed -n 's/^\([0-9][0-9]*\).*/\1/p')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "Running systemd manager >= 250 is required (found: ${manager_version:-unknown})." >&2
  exit 1
fi

# Recovery and isolated validation infrastructure are outside the generation
# transaction. Publish the complete recovery core durably *before* exposing any
# interlock that can make production units depend on it.
# Publish every dependency referenced by the new recovery helper first. If an
# older interrupted install is already actionable, a crash before helper
# replacement must still leave either the complete old recovery generation or
# a host where the old helper continues to run; never expose a helper whose
# finalizer/validator does not yet exist durably.
atomic_install_root_file "$REPO_INPUT_STAGE/install/validate-id-exergism-generation.sh" "$VALIDATOR" 0755
atomic_install_root_file "$REPO_INPUT_STAGE/install/verify-id-exergism-artifact-fence.sh" "$ARTIFACT_FENCE_AUDITOR" 0755
atomic_install_root_file "$REPO_INPUT_STAGE/agent/validate-release-manifest.py" "$MANIFEST_VALIDATOR" 0755
atomic_install_root_file "$REPO_INPUT_STAGE/spec/release-manifest-v0.1.schema.json" "$MANIFEST_SCHEMA" 0644
atomic_install_root_file "$REPO_INPUT_STAGE/install/finalize-id-exergism-recovery.sh" "$RECOVERY_FINALIZER" 0755
atomic_install_root_file "$REPO_INPUT_STAGE/packaging/id-exergism-install-recovery-finalize.service" "$RECOVERY_FINALIZE_UNIT_PATH" 0644
durable_sync_paths   "$VALIDATOR"   "$ARTIFACT_FENCE_AUDITOR"   "$MANIFEST_VALIDATOR"   "$MANIFEST_SCHEMA"   "$RECOVERY_FINALIZER"   "$RECOVERY_FINALIZE_UNIT_PATH"   /usr/local/libexec   /etc/systemd/system
durable_sync_ancestor_chain   /usr/local/libexec   /etc/systemd/system
systemctl daemon-reload

# Only after the helper dependency generation is durable and known to systemd
# may the helper that references it become visible.
atomic_install_root_file "$REPO_INPUT_STAGE/install/recover-id-exergism-install.sh" "$RECOVERY_HELPER" 0755
atomic_install_root_file "$REPO_INPUT_STAGE/packaging/id-exergism-install-recovery.service" "$RECOVERY_UNIT_PATH" 0644
durable_sync_paths   "$RECOVERY_HELPER"   "$RECOVERY_UNIT_PATH"   /usr/local/libexec   /etc/systemd/system
durable_sync_ancestor_chain   /usr/local/libexec   /etc/systemd/system
systemctl daemon-reload
systemctl enable "$RECOVERY_UNIT" >/dev/null

# The enablement link is part of the recovery core: make it durable before an
# interlock can require this unit on the next boot.
durable_sync_paths   /etc/systemd/system   /etc/systemd/system/multi-user.target.wants
durable_sync_ancestor_chain   /etc/systemd/system/multi-user.target.wants

# Only now expose resolver/updater dependencies on the already-durable recovery
# core. A power loss can no longer persist an interlock without its prerequisite.
atomic_install_root_file "$REPO_INPUT_STAGE/packaging/id-exergism-install-recovery-interlock.conf" "$TARGET_RECOVERY_INTERLOCK" 0644
atomic_install_root_file "$REPO_INPUT_STAGE/packaging/id-exergism-agent-recovery-interlock.conf" "$AGENT_RECOVERY_INTERLOCK" 0644
rm -f "$LEGACY_TIMER_RECOVERY_INTERLOCK"

durable_sync_paths   "$TARGET_RECOVERY_INTERLOCK"   "$AGENT_RECOVERY_INTERLOCK"   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   /etc/systemd/system
durable_sync_ancestor_chain   "$FENCE_DROPIN_DIR"   "$AGENT_RECOVERY_DROPIN_DIR"   /etc/systemd/system

systemctl daemon-reload

# Any old transaction from a failed invocation must be resolved before a new
# baseline can be captured. Every non-final phase, including .recovered, is
# actionable until recorded runtime-state restoration has completed.
if [[ -d "$INSTALL_PENDING_DIR" || -d "$INSTALL_VALIDATED_DIR" || -d "$INSTALL_RECOVERING_DIR" || -d "$INSTALL_RECOVERED_DIR" ]]; then
  echo "Recovering interrupted Deployment Attestation installation before continuing." >&2
  EC_INSTALL_LOCK_HELD=1 "$RECOVERY_HELPER" normal
fi
rm -rf "$INSTALL_COMMITTED_DIR"
durable_sync_paths "$INSTALL_STATE_ROOT"

TMP_MANIFEST="$(mktemp "$NATIVE_AGENT_STAGE/manifest.XXXXXX")"
tmp_manifest="$TMP_MANIFEST"
curl -q --retry 3 --retry-all-errors --connect-timeout 10 --max-time 120 \
  --proto '=https' --proto-redir '=https' -fsSL "$MANIFEST_URL" -o "$tmp_manifest"   || { echo "runtime-main does not publish DEPLOYMENT_MANIFEST.json; refusing to enable updater." >&2; exit 1; }
/usr/bin/python3 -I "$REPO_INPUT_STAGE/agent/validate-release-manifest.py" "$REPO_INPUT_STAGE/spec/release-manifest-v0.1.schema.json" "$tmp_manifest" || {
  echo "runtime-main publishes a schema-invalid DEPLOYMENT_MANIFEST.json; refusing to enable updater." >&2
  exit 1
}
/usr/bin/python3 -I - "$tmp_manifest" <<'PY'
import json, pathlib, re, sys
m = json.loads(pathlib.Path(sys.argv[1]).read_text())
assert m.get("schema_version") == "0.1"
assert m.get("repository") == "Exergism-Commons/id"
assert m.get("release_tag") == "runtime-main"
assert re.fullmatch(r"[0-9a-f]{40}", m.get("source_commit", ""))
assets = m.get("assets")
assert isinstance(assets, dict)
for arch in ("amd64", "arm64"):
    a = assets.get(arch)
    assert isinstance(a, dict)
    assert re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", a.get("name", ""))
    assert re.fullmatch(r"[0-9a-f]{64}", a.get("sha256", ""))
PY
rm -f "$tmp_manifest"
TMP_MANIFEST=""

# AGENT_INSTALL_SOURCE was bound to a descriptor-verified byte snapshot at
# installer admission, before any lengthy recovery/publication work.

native_preflight_config="$ENV_FILE"
if [[ ! -e "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
  native_preflight_config="$NATIVE_AGENT_STAGE/preflight.env"
  install -o root -g root -m 0600     "$REPO_INPUT_STAGE/examples/id.exergism.org.env.example"     "$native_preflight_config"
fi
EC_ATTESTATION_CONFIG="$native_preflight_config" "$AGENT_INSTALL_SOURCE" validate-config
"$AGENT_INSTALL_SOURCE" self-test

current_boot_id="$(cat "$BOOT_ID_FILE")"
[[ "$current_boot_id" =~ ^[0-9a-fA-F-]{36}$ ]] || {
  echo "Could not read a valid kernel boot ID." >&2
  exit 1
}

query_timer_enablement_state() {
  local load state rc
  if ! load="$(systemctl show "$TIMER_UNIT" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not query timer LoadState; refusing mutation." >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf 'not-found\n'
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Timer LoadState query returned no value; refusing mutation." >&2
    return 1
  }

  if state="$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null)"; then
    rc=0
  else
    rc=$?
  fi
  case "$state" in
    enabled|enabled-runtime)
      (( rc == 0 )) || {
        echo "Timer enablement query returned $state with rc=$rc; refusing mutation." >&2
        return 1
      }
      ;;
    disabled)
      (( rc != 0 )) || {
        echo "Timer enablement query returned disabled with unexpected rc=0; refusing mutation." >&2
        return 1
      }
      ;;
    *)
      echo "Could not determine exact timer enablement state (value=$state, rc=$rc); refusing mutation." >&2
      return 1
      ;;
  esac
  printf '%s\n' "$state"
}

timer_enablement_state="$(query_timer_enablement_state)"

unit_has_processes() {
  local cgroup="$1"
  [[ -n "$cgroup" ]] || return 1
  /usr/bin/python3 -I - "$cgroup" <<'PY'
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

unit_is_quiescent() {
  local unit="$1"
  local load active main_pid cgroup cgroup_rc
  local load_after active_after main_pid_after cgroup_after

  if ! load="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine LoadState for $unit" >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    load_after="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)" || return 1
    [[ "$load_after" == "not-found" ]] || return 1
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Could not determine LoadState for $unit" >&2
    return 1
  }

  active="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null)" || return 1
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  [[ "$main_pid" == 0 ]] || return 1

  if unit_has_processes "$cgroup"; then
    return 1
  else
    cgroup_rc=$?
    (( cgroup_rc == 1 )) || return 1
  fi

  # The cgroup walk is not an atomic systemd snapshot. Re-sample every
  # state component after it so a concurrent queued/manual start cannot be
  # accepted from the stale inactive/MainPID=0 observation.
  load_after="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)" || return 1
  active_after="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)" || return 1
  main_pid_after="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)" || return 1
  cgroup_after="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null)" || return 1

  [[ "$load_after" == "$load" ]] || return 1
  [[ "$active_after" == "inactive" || "$active_after" == "failed" ]] || return 1
  [[ "$main_pid_after" == 0 ]] || return 1
  [[ "$cgroup_after" == "$cgroup" ]] || return 1
  return 0
}

stop_and_wait_quiescent() {
  local unit="$1" i
  systemctl stop "$unit" >/dev/null 2>&1 || true
  for i in {1..30}; do
    unit_is_quiescent "$unit" && return 0
    sleep 1
  done
  echo "Unit did not become quiescent: $unit" >&2
  return 1
}

capture_active_baseline() {
  local unit="$1" state load
  if ! load="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine LoadState for baseline unit $unit" >&2
    return 1
  fi
  if [[ "$load" == "not-found" ]]; then
    printf '0\n'
    return 0
  fi
  [[ -n "$load" ]] || {
    echo "Could not determine LoadState for baseline unit $unit" >&2
    return 1
  }

  if ! state="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)"; then
    echo "Could not determine ActiveState for baseline unit $unit" >&2
    return 1
  fi
  case "$state" in
    active) printf '1\n' ;;
    inactive|failed) printf '0\n' ;;
    *)
      echo "Refusing to capture installer baseline while $unit is in ActiveState=${state:-unknown}." >&2
      return 1
      ;;
  esac
}

# Capture timer state before quiescing it, but never measure the resolver while
# a deployment updater may have temporarily stopped it.
timer_was_active="$(capture_active_baseline "$TIMER_UNIT")"

# Arm restoration before the first stop. Failures before a durable installer
# journal exists must not silently leave a previously-active timer disabled.
pretransaction_journal_published=0
pretransaction_restore_timer=1
restore_pretransaction_timer_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$pretransaction_journal_published" == 0 && "$pretransaction_restore_timer" == 1 ]]; then
    if [[ "$timer_was_active" == 1 ]]; then
      systemctl start "$TIMER_UNIT" >/dev/null 2>&1 || rc=1
    else
      stop_and_wait_quiescent "$TIMER_UNIT" || rc=1
    fi
  fi
  cleanup_installer_temporaries
  exit "$rc"
}
trap restore_pretransaction_timer_on_exit EXIT

stop_and_wait_quiescent "$TIMER_UNIT"
if ! stop_and_wait_quiescent "$AGENT_RUN_UNIT"; then
  pretransaction_restore_timer=0
  echo "Updater could not be quiesced; leaving timer stopped for safety." >&2
  exit 1
fi

# With the systemd updater quiesced, acquire the cross-process agent lock. A
# direct/manual agent that is not represented by the unit now makes installation
# fail closed instead of racing baseline capture or later rollback.
exec 8>"$AGENT_COORDINATION_LOCK"
if ! flock -n 8; then
  pretransaction_restore_timer=0
  echo "A direct deployment agent invocation is still running; leaving updater/timer quiesced." >&2
  exit 1
fi

# Reconcile the updater's own durable transaction before measuring target state.
if [[ -r "$ENV_FILE" ]]; then
  # Reconcile any supported historical updater journal with the already-pinned
  # Native AOT candidate. The installer never executes a legacy agent as part of
  # migration; unsupported journal formats fail closed.
  recovery_agent="$AGENT_INSTALL_SOURCE"
  if ! EC_AGENT_COORDINATION_LOCK_HELD=1 EC_ATTESTATION_CONFIG="$ENV_FILE" "$recovery_agent" recover; then
    pretransaction_restore_timer=0
    echo "Deployment updater transaction could not be recovered; leaving updater/timer quiesced." >&2
    exit 1
  fi
fi
if ! stop_and_wait_quiescent "$AGENT_RUN_UNIT"; then
  pretransaction_restore_timer=0
  echo "Updater did not remain quiescent after transaction recovery; leaving timer stopped." >&2
  exit 1
fi

target_was_active="$(capture_active_baseline "$TARGET_UNIT")"

verify_production_artifact_fence() {
  "$ARTIFACT_FENCE_AUDITOR"
}

artifact_path() {
  case "$1" in
    agent) printf '%s\n' "$AGENT" ;;
    smoke) printf '%s\n' "$SMOKE" ;;
    service_unit) printf '%s\n' "$AGENT_SERVICE_UNIT" ;;
    timer_unit) printf '%s\n' "$AGENT_TIMER_UNIT" ;;
    env) printf '%s\n' "$ENV_FILE" ;;
    fence) printf '%s\n' "$FENCE_DROPIN" ;;
    *) return 1 ;;
  esac
}

create_install_transaction() {
  local stage key path present
  for phase_path in "$INSTALL_PENDING_DIR" "$INSTALL_VALIDATED_DIR" "$INSTALL_RECOVERING_DIR" "$INSTALL_RECOVERED_DIR"; do
    path_exists_any "$phase_path" && return 1
  done

  stage="$(mktemp -d "${INSTALL_STATE_ROOT}/.${SERVICE}.pending.XXXXXX")"
  install -d -o root -g root -m 0700 "$stage/backups"

  printf '2\n' > "$stage/schema_version"
  printf '%s\n' "$current_boot_id" > "$stage/origin_boot_id"
  printf '%s\n' "$timer_enablement_state" > "$stage/timer_enablement_state"
  printf '%s\n' "$timer_was_active" > "$stage/timer_was_active"
  printf '%s\n' "$target_was_active" > "$stage/target_was_active"

  for key in agent smoke service_unit timer_unit env fence; do
    path="$(artifact_path "$key")"
    present=0
    if path_exists_any "$path"; then
      [[ -f "$path" || -L "$path" ]] || {
        echo "Refusing to journal non-file artifact for $key: $path" >&2
        rm -rf -- "$stage"
        return 1
      }
      present=1
      cp -aT -- "$path" "$stage/backups/$key"
    fi
    printf '%s\n' "$present" > "$stage/${key}_present"
  done

  /usr/bin/python3 -I - "$stage" <<'PY'
import os
import pathlib
import stat
import sys

root = pathlib.Path(sys.argv[1])
directories = []
for p in root.rglob("*"):
    st = os.lstat(p)
    if stat.S_ISREG(st.st_mode):
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
    elif stat.S_ISDIR(st.st_mode):
        directories.append(p)
for p in sorted(directories, key=lambda q: len(q.parts), reverse=True):
    fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
fd = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
try:
    os.fsync(fd)
finally:
    os.close(fd)
PY

  mv -T -- "$stage" "$INSTALL_PENDING_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

persist_installed_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"
  durable_sync_paths     "$AGENT" "$SMOKE" "$AGENT_SERVICE_UNIT" "$AGENT_TIMER_UNIT"     "$ENV_FILE" "$FENCE_DROPIN"     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR"     /etc/systemd/system "$timer_wants"
  durable_sync_ancestor_chain     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR" "$timer_wants"
}

mark_generation_validated() {
  mv -T -- "$INSTALL_PENDING_DIR" "$INSTALL_VALIDATED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

finalize_install_transaction() {
  rm -rf "$INSTALL_COMMITTED_DIR"
  mv -T -- "$INSTALL_VALIDATED_DIR" "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
  rm -rf "$INSTALL_COMMITTED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

install_complete=0
rollback_install_on_exit() {
  local rc=$?
  trap - EXIT
  if [[ "$install_complete" == 0 ]] &&      [[ -d "$INSTALL_PENDING_DIR" || -d "$INSTALL_VALIDATED_DIR" || -d "$INSTALL_RECOVERING_DIR" || -d "$INSTALL_RECOVERED_DIR" ]]; then
    echo "Installation failed; restoring the durable previous generation." >&2
    if ! EC_INSTALL_LOCK_HELD=1 EC_AGENT_COORDINATION_LOCK_HELD=1 "$RECOVERY_HELPER" normal; then
      # Recovery retained an actionable journal and deliberately left services
      # quiesced. Never bypass that fail-closed state.
      rc=1
    fi
  fi
  cleanup_installer_temporaries
  exit "$rc"
}

create_install_transaction
pretransaction_journal_published=1
trap - EXIT
trap rollback_install_on_exit EXIT

# Quiesce every actor that could observe or mutate the generation while it is
# pending. Do not trust is-active alone: activating/deactivating units can still
# own live processes. Require a terminal state, MainPID=0 and an empty cgroup.
stop_and_wait_quiescent "$TIMER_UNIT"
stop_and_wait_quiescent "$AGENT_RUN_UNIT"
stop_and_wait_quiescent "$TARGET_UNIT"

install -o root -g root -m 0755 "$AGENT_INSTALL_SOURCE" "$AGENT"
install -o root -g root -m 0755 "$REPO_INPUT_STAGE/examples/id.exergism.org-smoke.sh" "$SMOKE"
install -o root -g root -m 0644 "$REPO_INPUT_STAGE/packaging/ec-deployment-attestation@.service" "$AGENT_SERVICE_UNIT"
install -o root -g root -m 0644 "$REPO_INPUT_STAGE/packaging/ec-deployment-attestation@.timer" "$AGENT_TIMER_UNIT"
install -o root -g root -m 0644 "$REPO_INPUT_STAGE/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"
if [[ ! -e "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
  install -o root -g root -m 0640 "$REPO_INPUT_STAGE/examples/id.exergism.org.env.example" "$ENV_FILE"
fi

systemctl daemon-reload
systemd-analyze verify "$TARGET_UNIT" "$AGENT_RUN_UNIT" "$TIMER_UNIT" >/dev/null

# Validate the pending generation without starting any interlocked production
# unit. A transient resolver gets the same user, source, binary and read-only
# artifact boundaries on an isolated loopback port.
"$VALIDATOR"

# Successful installation intentionally makes the updater persistently enabled,
# but it remains stopped until the production resolver has activated safely.
systemctl enable "$TIMER_UNIT" >/dev/null
[[ "$(systemctl is-enabled "$TIMER_UNIT" 2>/dev/null || true)" == "enabled" ]]
unit_is_quiescent "$TIMER_UNIT" || {
  echo "Timer did not remain provably quiescent after enablement." >&2
  exit 1
}

persist_installed_generation

# validated means the on-disk generation is complete, semantically healthy and
# durable. Recovery may allow reads/activation while the installer lock is held,
# because no mixed generation can exist beyond this atomic transition.
mark_generation_validated

systemctl start "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -q -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null
EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"
verify_production_artifact_fence

systemctl start "$TIMER_UNIT"
systemctl is-active --quiet "$TIMER_UNIT"

finalize_install_transaction
install_complete=1
cleanup_installer_temporaries
trap - EXIT

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
