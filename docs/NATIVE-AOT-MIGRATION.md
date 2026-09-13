# Native AOT agent migration

This branch ports the host deployment/attestation agent from Bash to a .NET 10 Native AOT executable.

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
- deterministic attestation construction and HMAC delivery.

The installer/recovery scripts remain shell in this migration. They are separate host-generation transactions and can be migrated independently after the agent reaches behavioral parity.

## AOT constraints

The project intentionally has no third-party NuGet dependencies and avoids runtime reflection/assembly loading.

JSON is parsed with `JsonDocument` and emitted with `Utf8JsonWriter`. The project sets:

- `TargetFramework=net10.0`;
- `PublishAot=true`;
- `IsAotCompatible=true`;
- `TreatWarningsAsErrors=true`.

CI publishes a real `linux-x64` Native AOT ELF and executes its dependency-free `self-test` command.

## Rollout rule

Do not replace the installed `/usr/local/libexec/ec-deployment-agent` with the AOT binary merely because it compiles.

The switch is allowed only after:

1. managed build and AOT analyzers are clean;
2. Native AOT publish succeeds;
3. the published ELF passes self-tests;
4. transaction/recovery parity is reviewed;
5. deployment packaging has a durable way to obtain the architecture-specific binary;
6. the stacked PR has no unresolved P1/P2 review findings.

Until then, the Bash agent remains the production reference implementation.


## Opt-in host installation

The existing installer can stage a reviewed Native AOT binary without changing the default production path:

```sh
sudo EC_NATIVE_AGENT_BINARY=/path/to/ec-deployment-agent \
  ./install/install-id-exergism.sh
```

The installer validates that the supplied path is a real executable file, installs it through the existing generation transaction, runs its dependency-free `self-test`, and keeps the same rollback artifact/journal semantics. If an installed agent already exists, pre-install transaction recovery prefers that installed generation so the implementation that created a journal is also the implementation that reconciles it.

Without `EC_NATIVE_AGENT_BINARY`, the installer continues to install the Bash reference agent. This is deliberate until the Native AOT PR has parity review and a durable release/distribution channel.
