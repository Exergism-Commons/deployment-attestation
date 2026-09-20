# Native AOT agent

The host deployment/attestation agent is a .NET 10 Native AOT executable. The former Bash agent has been retired to keep one security-critical state machine and one test surface.

## Boundary

The Native AOT executable owns the runtime state machine:

- configuration parsing;
- global/state locking;
- installer-phase admission;
- release-manifest validation and download;
- byte-exact Git worktree verification;
- source/runtime durability barriers;
- systemd lifecycle and semantic smoke execution;
- live MainPID/runtime binding and mount-namespace artifact fences;
- activation journal creation;
- rollback and committed-phase recovery;
- health collection;
- deterministic attestation construction and HMAC delivery;
- independent durable self-health via `agent-health.json`, `health` and `status`.

The installer/recovery/finalization scripts remain shell. They implement the host-generation transaction around installation and systemd recovery; they are not a second deployment-agent implementation.

## AOT constraints

The project intentionally has no third-party NuGet dependencies and avoids runtime reflection/assembly loading.

JSON is parsed with `JsonDocument` and emitted with `Utf8JsonWriter`. The project sets:

- `TargetFramework=net10.0`;
- `PublishAot=true`;
- `IsAotCompatible=true`;
- `TreatWarningsAsErrors=true`.

CI runs a dedicated MSTest unit-test project first, then publishes a real `linux-x64` Native AOT ELF and executes its dependency-free `self-test` command. Unit tests cover pure protocol/state logic and regression cases; `self-test` remains a native-binary smoke test rather than a substitute for unit testing.

## Installation contract

The repository has one deployment-agent implementation: the Native AOT executable.

A reviewed architecture-specific binary is mandatory, and the privileged installer must itself be staged and verified before execution. Do **not** run the installer directly from a user-writable checkout.

Obtain the reviewed SHA-256 values for both the installer and Native AOT binary through a channel independent of the local checkout, then use the same root-only executable staging boundary as the main README. Do not stage executable bootstrap bytes under `/run`; hardened hosts may mount it `noexec`:

```sh
SOURCE_ROOT="$PWD"
INSTALLER_SHA256="<reviewed lowercase SHA-256 of install/install-id-exergism.sh>"
AGENT="/path/to/reviewed/ec-deployment-agent"
AGENT_SHA256="<reviewed lowercase SHA-256 of the Native AOT agent>"

sudo install -d -o root -g root -m 0700 /var/lib/ec-deployment-attestation
sudo install -d -o root -g root -m 0700 /var/lib/ec-deployment-attestation/bootstrap
STAGE="$(sudo mktemp -d /var/lib/ec-deployment-attestation/bootstrap/installer.XXXXXX)"
sudo chmod 0700 "$STAGE"
sudo chown root:root "$STAGE"
if findmnt -n -o OPTIONS -T "$STAGE" | tr ',' '\n' | grep -Fxq noexec; then
  echo "Trusted installer stage filesystem is mounted noexec." >&2
  sudo rm -rf -- "$STAGE"
  exit 1
fi
sudo install -o root -g root -m 0500 \
  "$SOURCE_ROOT/install/install-id-exergism.sh" \
  "$STAGE/install-id-exergism.sh"

if ! printf '%s  %s\n' "$INSTALLER_SHA256" "$STAGE/install-id-exergism.sh" | sudo sha256sum -c -; then
  sudo rm -rf -- "$STAGE"
  exit 1
fi

sudo env -i \
  PATH=/usr/sbin:/usr/bin:/sbin:/bin \
  EC_INSTALLER_TRUSTED_STAGE="$STAGE" \
  EC_INSTALLER_SOURCE_ROOT="$SOURCE_ROOT" \
  EC_INSTALLER_SHA256="$INSTALLER_SHA256" \
  EC_NATIVE_AGENT_BINARY="$AGENT" \
  EC_NATIVE_AGENT_SHA256="$AGENT_SHA256" \
  "$STAGE/install-id-exergism.sh"
```

The root-owned staged installer re-verifies its own digest, treats the checkout as untrusted input, authenticates every repository-sourced helper/configuration file against the SHA-256 table embedded in the reviewed installer, pins the exact Native AOT candidate matching `EC_NATIVE_AGENT_SHA256`, validates its configuration, runs its dependency-free `self-test`, and installs that pinned object through the durable generation transaction.

Pre-install transaction recovery is always executed by the already-pinned Native AOT candidate. Supported historical journal formats are migrated/reconciled there; unsupported formats fail closed. The installer does not execute a legacy agent during migration.

There is intentionally no Bash fallback. This prevents security fixes, recovery semantics, quiescence checks, and attestation handling from diverging across two implementations.

## C# conventions

The executable uses top-level statements for the entry point. Closed domains use enums internally; persisted/wire values and other repeated contract strings are centralized as `SNAKE_CASE` constants. This keeps protocol spellings reviewable in one place and avoids magic strings across the state machine.
