#!/usr/bin/env bash
set -Eeuo pipefail

TARGET_UNIT="${EC_ID_TARGET_UNIT:-id-exergism.service}"
APP_DIR="${EC_ID_APP_DIR:-/srv/id.exergism.org}"
APP_BIN="${EC_ID_APP_BIN:-/usr/local/bin/idresolver}"

for command in systemctl nsenter python3; do
  command -v "$command" >/dev/null 2>&1 || {
    echo "Artifact-fence audit dependency missing: $command" >&2
    exit 1
  }
done

pid="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)" || {
  echo "Could not determine production resolver MainPID." >&2
  exit 1
}
[[ "$pid" =~ ^[1-9][0-9]*$ ]] || {
  echo "Production resolver has no live MainPID." >&2
  exit 1
}

# Audit the live process mount namespace, not merely the unit-file settings.
# Every mount at/below the source tree and the deepest mount covering the
# runtime must be read-only.
nsenter --target "$pid" --mount -- python3 - "$APP_DIR" "$APP_BIN" <<'PY'
import os
import re
import sys

app_dir = os.path.normpath(sys.argv[1])
app_bin = os.path.normpath(sys.argv[2])
if not (app_dir.startswith("/") and app_bin.startswith("/")):
    raise SystemExit("deployment artifact paths must be absolute")

_octal = re.compile(r"\\([0-7]{3})")
def unescape_mountinfo(value):
    return _octal.sub(lambda m: chr(int(m.group(1), 8)), value)

mounts = []
with open("/proc/self/mountinfo", "r", encoding="utf-8") as fh:
    for raw in fh:
        left, sep, _right = raw.rstrip("\n").partition(" - ")
        if not sep:
            raise SystemExit("malformed mountinfo")
        fields = left.split()
        if len(fields) < 6:
            raise SystemExit("malformed mountinfo fields")
        target = os.path.normpath(unescape_mountinfo(fields[4]))
        options = set(fields[5].split(","))
        mounts.append((target, options))

def contains(root, path):
    try:
        return os.path.commonpath((root, path)) == root
    except ValueError:
        return False

def deepest_cover(path):
    candidates = [(target, opts) for target, opts in mounts if contains(target, path)]
    if not candidates:
        raise SystemExit(f"no mount covers {path}")
    return max(candidates, key=lambda item: len(item[0]))

source_cover, source_opts = deepest_cover(app_dir)
if "ro" not in source_opts or "rw" in source_opts:
    raise SystemExit(f"source root is writable via {source_cover}: {sorted(source_opts)}")

for target, options in mounts:
    if target == app_dir or (contains(app_dir, target) and target != app_dir):
        if "ro" not in options or "rw" in options:
            raise SystemExit(f"writable source submount {target}: {sorted(options)}")

binary_cover, binary_opts = deepest_cover(app_bin)
if "ro" not in binary_opts or "rw" in binary_opts:
    raise SystemExit(f"runtime is writable via {binary_cover}: {sorted(binary_opts)}")
PY

pid_after="$(systemctl show "$TARGET_UNIT" --property=MainPID --value 2>/dev/null)" || {
  echo "Could not re-check production resolver MainPID after fence audit." >&2
  exit 1
}
[[ "$pid_after" == "$pid" ]] || {
  echo "Production resolver changed PID during artifact-fence audit." >&2
  exit 1
}
systemctl is-active --quiet "$TARGET_UNIT"
