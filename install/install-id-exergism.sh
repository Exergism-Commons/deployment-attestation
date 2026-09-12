#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE="id.exergism.org"
TARGET_UNIT="id-exergism.service"
AGENT_RUN_UNIT="ec-deployment-attestation@${SERVICE}.service"
TIMER_UNIT="ec-deployment-attestation@${SERVICE}.timer"
RECOVERY_UNIT="id-exergism-install-recovery.service"

AGENT="/usr/local/libexec/ec-deployment-agent"
SMOKE="/usr/local/libexec/id.exergism.org-smoke.sh"
VALIDATOR="/usr/local/libexec/ec-id-generation-validator"
RECOVERY_FINALIZER="/usr/local/libexec/ec-deployment-install-recovery-finalize"
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

MANIFEST_URL="https://github.com/Exergism-Commons/id/releases/download/runtime-main/DEPLOYMENT_MANIFEST.json"

for command in curl git python3 sha256sum systemctl systemd-run systemd-analyze flock jq install mktemp awk sed tr date hostname uname mv rm grep findmnt nsenter cp cat sleep; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Required dependency not found: $command" >&2
    exit 1
  }
done

exec 9>"$INSTALL_LOCK"
flock -n 9 || {
  echo "Another Deployment Attestation installation/recovery is already running." >&2
  exit 1
}

durable_sync_paths() {
  python3 - "$@" <<'PY'
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
  python3 - "$@" <<'PY'
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
  mv -f "$tmp" "$dest"
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

manager_version="$(systemctl show --property=Version --value 2>/dev/null || true)"
systemd_version="$(printf '%s\n' "$manager_version" | sed -n 's/^\([0-9][0-9]*\).*/\1/p')"
if [[ ! "$systemd_version" =~ ^[0-9]+$ ]] || (( systemd_version < 250 )); then
  echo "Running systemd manager >= 250 is required (found: ${manager_version:-unknown})." >&2
  exit 1
fi

# Recovery and isolated validation infrastructure are outside the generation
# transaction. Publish the complete recovery core durably *before* exposing any
# interlock that can make production units depend on it.
atomic_install_root_file "$ROOT/install/recover-id-exergism-install.sh" "$RECOVERY_HELPER" 0755
atomic_install_root_file "$ROOT/install/validate-id-exergism-generation.sh" "$VALIDATOR" 0755
atomic_install_root_file "$ROOT/install/finalize-id-exergism-recovery.sh" "$RECOVERY_FINALIZER" 0755
atomic_install_root_file "$ROOT/packaging/id-exergism-install-recovery.service" "$RECOVERY_UNIT_PATH" 0644
atomic_install_root_file "$ROOT/packaging/id-exergism-install-recovery-finalize.service" "$RECOVERY_FINALIZE_UNIT_PATH" 0644

durable_sync_paths   "$RECOVERY_HELPER"   "$VALIDATOR"   "$RECOVERY_FINALIZER"   "$RECOVERY_UNIT_PATH"   "$RECOVERY_FINALIZE_UNIT_PATH"   /usr/local/libexec   /etc/systemd/system
durable_sync_ancestor_chain   /usr/local/libexec   /etc/systemd/system

systemctl daemon-reload
systemctl enable "$RECOVERY_UNIT" >/dev/null

# The enablement link is part of the recovery core: make it durable before an
# interlock can require this unit on the next boot.
durable_sync_paths   /etc/systemd/system   /etc/systemd/system/multi-user.target.wants
durable_sync_ancestor_chain   /etc/systemd/system/multi-user.target.wants

# Only now expose resolver/updater dependencies on the already-durable recovery
# core. A power loss can no longer persist an interlock without its prerequisite.
atomic_install_root_file "$ROOT/packaging/id-exergism-install-recovery-interlock.conf" "$TARGET_RECOVERY_INTERLOCK" 0644
atomic_install_root_file "$ROOT/packaging/id-exergism-agent-recovery-interlock.conf" "$AGENT_RECOVERY_INTERLOCK" 0644
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

tmp_manifest="$(mktemp)"
trap 'rm -f "$tmp_manifest"' EXIT
curl --retry 3 --retry-all-errors --connect-timeout 10 -fsSL "$MANIFEST_URL" -o "$tmp_manifest"   || { echo "runtime-main does not publish DEPLOYMENT_MANIFEST.json; refusing to enable updater." >&2; exit 1; }
python3 - "$tmp_manifest" <<'PY'
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
trap - EXIT


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
  local unit="$1" cgroup
  if ! cgroup="$(systemctl show "$unit" --property=ControlGroup --value 2>/dev/null)"; then
    echo "Could not determine ControlGroup for $unit" >&2
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

unit_is_quiescent() {
  local unit="$1" load active main_pid cgroup_rc
  if ! load="$(systemctl show "$unit" --property=LoadState --value 2>/dev/null)"; then
    echo "Could not determine LoadState for $unit" >&2
    return 1
  fi
  [[ "$load" == "not-found" ]] && return 0
  [[ -n "$load" ]] || {
    echo "Could not determine LoadState for $unit" >&2
    return 1
  }

  if ! active="$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null)"; then
    echo "Could not determine ActiveState for $unit" >&2
    return 1
  fi
  [[ -n "$active" ]] || {
    echo "Could not determine ActiveState for $unit" >&2
    return 1
  }
  [[ "$active" == "inactive" || "$active" == "failed" ]] || return 1
  if [[ "$unit" == *.timer ]]; then
    return 0
  fi
  if ! main_pid="$(systemctl show "$unit" --property=MainPID --value 2>/dev/null)"; then
    echo "Could not determine MainPID for $unit" >&2
    return 1
  fi
  [[ -n "$main_pid" ]] || {
    echo "Could not determine MainPID for $unit" >&2
    return 1
  }
  [[ "$main_pid" == 0 ]] || return 1
  if unit_has_processes "$unit"; then
    return 1
  else
    cgroup_rc=$?
    (( cgroup_rc == 1 )) || return 1
  fi
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
  if ! EC_AGENT_COORDINATION_LOCK_HELD=1 EC_ATTESTATION_CONFIG="$ENV_FILE" "$ROOT/agent/ec-deployment-agent.sh" recover; then
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
  local pid path options
  if ! pid="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)"; then
    echo "Could not determine production resolver MainPID." >&2
    return 1
  fi
  [[ "$pid" =~ ^[1-9][0-9]*$ ]] || {
    echo "Production resolver has no live MainPID." >&2
    return 1
  }

  for path in "$APP_DIR" "$APP_BIN"; do
    options="$(nsenter --target "$pid" --mount -- findmnt -T "$path" -n -o OPTIONS 2>/dev/null)" || {
      echo "Could not inspect production mount options for $path." >&2
      return 1
    }
    case ",$options," in
      *,ro,*) ;;
      *)
        echo "Production resolver sees writable deployment artifact: $path ($options)" >&2
        return 1
        ;;
    esac
  done
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
  [[ ! -e "$INSTALL_PENDING_DIR" && ! -e "$INSTALL_VALIDATED_DIR" && ! -e "$INSTALL_RECOVERING_DIR" && ! -e "$INSTALL_RECOVERED_DIR" ]] || return 1

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
    if [[ -e "$path" || -L "$path" ]]; then
      present=1
      cp -a -- "$path" "$stage/backups/$key"
    fi
    printf '%s\n' "$present" > "$stage/${key}_present"
  done

  python3 - "$stage" <<'PY'
import os
import pathlib
import stat
import sys

root = pathlib.Path(sys.argv[1])
for p in root.rglob("*"):
    st = os.lstat(p)
    if stat.S_ISREG(st.st_mode):
        fd = os.open(p, os.O_RDONLY)
        try:
            os.fsync(fd)
        finally:
            os.close(fd)
for p in sorted((q for q in root.rglob("*") if q.is_dir()), key=lambda q: len(q.parts), reverse=True):
    fd = os.open(p, os.O_RDONLY | os.O_DIRECTORY)
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

  mv "$stage" "$INSTALL_PENDING_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

persist_installed_generation() {
  local timer_wants="/etc/systemd/system/timers.target.wants"
  durable_sync_paths     "$AGENT" "$SMOKE" "$AGENT_SERVICE_UNIT" "$AGENT_TIMER_UNIT"     "$ENV_FILE" "$FENCE_DROPIN"     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR"     /etc/systemd/system "$timer_wants"
  durable_sync_ancestor_chain     /usr/local/libexec /etc/ec-deployment-attestation "$FENCE_DROPIN_DIR" "$timer_wants"
}

mark_generation_validated() {
  mv "$INSTALL_PENDING_DIR" "$INSTALL_VALIDATED_DIR"
  durable_sync_paths "$INSTALL_STATE_ROOT"
}

finalize_install_transaction() {
  rm -rf "$INSTALL_COMMITTED_DIR"
  mv "$INSTALL_VALIDATED_DIR" "$INSTALL_COMMITTED_DIR"
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

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" "$AGENT"
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" "$SMOKE"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" "$AGENT_SERVICE_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" "$AGENT_TIMER_UNIT"
install -o root -g root -m 0644 "$ROOT/packaging/id-exergism-artifact-fence.conf" "$FENCE_DROPIN"
if [[ ! -e "$ENV_FILE" && ! -L "$ENV_FILE" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" "$ENV_FILE"
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
! systemctl is-active --quiet "$TIMER_UNIT"

persist_installed_generation

# validated means the on-disk generation is complete, semantically healthy and
# durable. Recovery may allow reads/activation while the installer lock is held,
# because no mixed generation can exist beyond this atomic transition.
mark_generation_validated

systemctl start "$TARGET_UNIT"
systemctl is-active --quiet "$TARGET_UNIT"
curl -fsS --max-time 15 http://127.0.0.1:8080/ >/dev/null
EC_LOCAL_URL=http://127.0.0.1:8080 "$SMOKE"
verify_production_artifact_fence

systemctl start "$TIMER_UNIT"
systemctl is-active --quiet "$TIMER_UNIT"

finalize_install_transaction
install_complete=1
trap - EXIT

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
