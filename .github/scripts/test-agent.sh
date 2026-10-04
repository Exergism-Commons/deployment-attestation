#!/usr/bin/env bash
set -euo pipefail

# Privileged policy/staging admission and snapshot guards.
sudo -- /usr/bin/python3 -I tests/test_self_update.py

project='tests/Exergism.DeploymentAttestation.Agent.Tests/Exergism.DeploymentAttestation.Agent.Tests.csproj'
dotnet="$(readlink -f "$(command -v dotnet)")"

# Keep the normal suite unprivileged. Successful immutable-checkout sealing
# requires CAP_LINUX_IMMUTABLE and runs separately on the hosted Linux VM.
"$dotnet" test "$project" -c Release --filter 'TestCategory!=PrivilegedLinux'

privileged_home="$(mktemp -d "${RUNNER_TEMP:-${TMPDIR:-/tmp}}/ec-agent-privileged-tests.XXXXXX")"
sudo -- env \
  DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$dotnet")}" \
  DOTNET_CLI_HOME="$privileged_home" \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_GENERATE_ASPNET_CERTIFICATE=false \
  "$dotnet" test "$project" -c Release --no-build --no-restore \
    --filter 'TestCategory=PrivilegedLinux' \
    --results-directory "$privileged_home/results"
