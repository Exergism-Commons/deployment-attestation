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

A reviewed architecture-specific binary is mandatory:

```sh
sudo EC_NATIVE_AGENT_BINARY=/path/to/ec-deployment-agent \
  ./install/install-id-exergism.sh
```

The installer requires the supplied path to be a real executable file, pins the exact candidate into root-owned process-private staging, validates its configuration, runs its dependency-free `self-test`, and installs that pinned object through the durable generation transaction.

Pre-install transaction recovery is always executed by the already-pinned Native AOT candidate. Supported historical journal formats are migrated/reconciled there; unsupported formats fail closed. The installer does not execute a legacy agent during migration.

There is intentionally no Bash fallback. This prevents security fixes, recovery semantics, quiescence checks, and attestation handling from diverging across two implementations.

## C# conventions

The executable uses top-level statements for the entry point. Closed domains use enums internally; persisted/wire values and other repeated contract strings are centralized as `SNAKE_CASE` constants. This keeps protocol spellings reviewable in one place and avoids magic strings across the state machine.
