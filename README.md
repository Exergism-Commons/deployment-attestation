# Exergism Commons Deployment Attestation

`deployment-attestation` defines a reusable protocol and reference host agent for proving what Exergism Commons services are actually running in production.

The repository deliberately separates **deployment authority** from **semantic/application authority**. A service repository produces a release; the host verifies and activates it; the host then emits a signed observation describing what is actually running. A receiver may project that observation into GitHub Deployment/Check status without giving the production host GitHub write credentials.

## Goals

- deploy only release-bound, content-verified revisions;
- bind source revision and architecture-specific runtime digest through one release manifest snapshot;
- survive process termination/reboot during activation without rebasing rollback onto partial state;
- record the exact source commit, runtime digest and release manifest observed on a host;
- perform local, public and service-specific health checks;
- roll back automatically when activation fails;
- emit HMAC-authenticated, idempotent attestations to an external receiver;
- make production state actionable in GitHub without committing heartbeat noise to repositories;
- reuse the same protocol for `id.exergism.org`, governance, funding and future EC services.

## Repository layout

- `spec/release-manifest-v0.1.schema.json` — atomic release snapshot binding source commit to runtime digests.
- `spec/attestation-v0.1.schema.json` — wire contract for host observations.
- `src/Exergism.DeploymentAttestation.Agent/` — .NET 10 Native AOT deployment/attestation agent.
- `tests/Exergism.DeploymentAttestation.Agent.Tests/` — unit tests for protocol, recovery guards and health semantics.
- `packaging/` — systemd service/timer templates.
- `examples/id.exergism.org.env.example` — configuration for the current PID resolver deployment.
- `examples/id.exergism.org-smoke.sh` — semantic smoke checks for the resolver.
- `docs/ARCHITECTURE.md` — trust and data-flow model.
- `docs/THREAT-MODEL.md` — explicit security assumptions and non-goals.

## Core trust invariant

The production host MUST NOT deploy `git pull main` directly and MUST NOT independently combine mutable `SOURCE_COMMIT`, checksum and binary assets. A release channel publishes one `DEPLOYMENT_MANIFEST.json` response that binds the exact source commit and SHA-256 digest of every architecture-specific runtime asset. The agent first snapshots that single manifest, then accepts only a runtime matching the digest in that snapshot and checks out the exact commit named by it. If a rolling release is being republished concurrently, mismatched old/new artifacts fail closed rather than producing a mixed deployment.

Activation is a durable transaction. Before mutating source or runtime, the agent verifies the currently recorded source/runtime pair, fsyncs a rollback binary, and atomically persists `transaction.json`. Any later run that finds this file restores the old pair before establishing a new baseline. `current-state.json` is atomically committed only after the new runtime passes mandatory health checks; failure to record that state is itself an activation failure and triggers rollback.

The Native AOT agent is the sole deployment/attestation agent implementation. The installer requires an explicit reviewed `EC_NATIVE_AGENT_BINARY` and its lowercase `EC_NATIVE_AGENT_SHA256`, pins the exact reviewed bytes into root-owned staging, validates them, and installs the snapshot as `/usr/local/libexec/ec-deployment-agent`. The agent maintains independent self-health in `agent-health.json`; `ec-deployment-agent health` evaluates freshness, the last completed cycle, timer state, transaction state and attestation delivery without recursively trusting the target-service attestation, while `ec-deployment-agent status` emits the same self-health document for observability without using health as an exit gate.

### Privileged installer bootstrap

Never run `sudo ./install/install-id-exergism.sh` from a user-writable checkout. Bash parses shell input incrementally, so a mutable script cannot safely establish its own privilege boundary.

Obtain `INSTALLER_SHA256` from the reviewed release/commit metadata through a channel independent of the local checkout, then stage and verify the installer with trusted system tools before executing it:

```bash
SOURCE_ROOT="$PWD"
INSTALLER_SHA256="<reviewed lowercase SHA-256 of install/install-id-exergism.sh>"
AGENT="/path/to/reviewed/ec-deployment-agent"
AGENT_SHA256="<reviewed lowercase SHA-256 of the Native AOT agent>"

STAGE="$(sudo mktemp -d /run/ec-deployment-attestation-installer.XXXXXX)"
sudo chmod 0700 "$STAGE"
sudo chown root:root "$STAGE"
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

The privileged installer refuses to run unless it is executing from that root-owned private stage and its staged bytes match `EC_INSTALLER_SHA256`. It then authenticates every repository-sourced helper/configuration input against the SHA-256 table embedded in those reviewed installer bytes before installation.

The host MUST NOT hold a GitHub token capable of mutating EC repositories. It signs an attestation with a service-specific HMAC key and sends it to a receiver. Every observation contains a signed `observation_id`; receiver-side deduplication is mandatory so transport retries cannot increment incident thresholds twice. The receiver owns the GitHub integration and can publish Deployment/Check status or open incidents.

## Initial status

`0.1` is a pre-adoption protocol. The first integration target is `id.exergism.org`. The service release workflow must publish `DEPLOYMENT_MANIFEST.json` before the updater is enabled. The receiver/GitHub-App implementation is intentionally a separate step from the host agent so its credentials and failure modes remain isolated.

See `docs/ARCHITECTURE.md` before deploying the agent.
