#!/usr/bin/env python3
from pathlib import Path

p = Path("agent/ec-deployment-agent.sh")
s = p.read_text()

old_version = 'AGENT_VERSION="0.1.0-pre11"'
new_version = 'AGENT_VERSION="0.1.0-pre12"'
assert s.count(old_version) == 1, "unexpected agent version marker"
s = s.replace(old_version, new_version, 1)

needle = '''    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \\\n    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \\\n'''
replacement = '''    --property="RuntimeMaxSec=${EC_SMOKE_TIMEOUT}s" \\\n    --property="ReadOnlyPaths=${EC_APP_DIR}" \\\n    --property="ReadOnlyPaths=${EC_APP_BIN}" \\\n    --setenv="EC_PUBLIC_URL=${EC_PUBLIC_URL}" \\\n'''
assert s.count(needle) == 1, "run_smoke property block not found exactly once"
s = s.replace(needle, replacement, 1)

comment_old = '''  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
'''
comment_new = '''  # ExitType=cgroup keeps the transient unit alive until every descendant in
  # the smoke cgroup exits. RuntimeMaxSec fails closed and KillMode=control-group
  # contains daemonizing/new-session descendants within the reviewed boundary.
  # The transient smoke unit also sees both deployment artifacts read-only, so
  # health/attestation checks cannot mutate source/runtime after journal removal.
'''
assert s.count(comment_old) == 1, "run_smoke comment block not found exactly once"
s = s.replace(comment_old, comment_new, 1)

p.write_text(s)
print("Applied Codex round-4 smoke fence hardening")
