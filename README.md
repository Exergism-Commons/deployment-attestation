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
- `agent/ec-deployment-agent.sh` — reference release updater + health/attestation agent.
- `packaging/` — systemd service/timer templates.
- `examples/id.exergism.org.env.example` — configuration for the current PID resolver deployment.
- `examples/id.exergism.org-smoke.sh` — semantic smoke checks for the resolver.
- `docs/ARCHITECTURE.md` — trust and data-flow model.
- `docs/THREAT-MODEL.md` — explicit security assumptions and non-goals.

## Core trust invariant

The production host MUST NOT deploy `git pull main` directly and MUST NOT independently combine mutable `SOURCE_COMMIT`, checksum and binary assets. A release channel publishes one `DEPLOYMENT_MANIFEST.json` response that binds the exact source commit and SHA-256 digest of every architecture-specific runtime asset. The agent first snapshots that single manifest, then accepts only a runtime matching the digest in that snapshot and checks out the exact commit named by it. If a rolling release is being republished concurrently, mismatched old/new artifacts fail closed rather than producing a mixed deployment.

Activation is a durable transaction. Before mutating source or runtime, the agent verifies the currently recorded source/runtime pair, fsyncs a rollback binary, and atomically persists `transaction.json`. Any later run that finds this file restores the old pair before establishing a new baseline. `current-state.json` is atomically committed only after the new runtime passes mandatory health checks; failure to record that state is itself an activation failure and triggers rollback.

The host MUST NOT hold a GitHub token capable of mutating EC repositories. It signs an attestation with a service-specific HMAC key and sends it to a receiver. Every observation contains a signed `observation_id`; receiver-side deduplication is mandatory so transport retries cannot increment incident thresholds twice. The receiver owns the GitHub integration and can publish Deployment/Check status or open incidents.

## Initial status

`0.1` is a pre-adoption protocol. The first integration target is `id.exergism.org`. The service release workflow must publish `DEPLOYMENT_MANIFEST.json` before the updater is enabled. The receiver/GitHub-App implementation is intentionally a separate step from the host agent so its credentials and failure modes remain isolated.

See `docs/ARCHITECTURE.md` before deploying the agent.
