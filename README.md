# Exergism Commons Deployment Attestation

`deployment-attestation` defines a reusable protocol and reference host agent for proving what Exergism Commons services are actually running in production.

The repository deliberately separates **deployment authority** from **semantic/application authority**. A service repository produces a release; the host verifies and activates it; the host then emits a signed observation describing what is actually running. A receiver may project that observation into GitHub Deployment/Check status without giving the production host GitHub write credentials.

## Goals

- deploy only release-bound, content-verified revisions;
- record the exact source commit and release observed on a host;
- perform local, public and service-specific health checks;
- roll back automatically when activation fails;
- emit HMAC-authenticated attestations to an external receiver;
- make production state actionable in GitHub without committing heartbeat noise to repositories;
- reuse the same protocol for `id.exergism.org`, governance, funding and future EC services.

## Repository layout

- `spec/attestation-v0.1.schema.json` — wire contract for host observations.
- `agent/ec-deployment-agent.sh` — reference release updater + health/attestation agent.
- `packaging/` — systemd service/timer templates.
- `examples/id.exergism.org.env.example` — configuration for the current PID resolver deployment.
- `examples/id.exergism.org-smoke.sh` — semantic smoke checks for the resolver.
- `docs/ARCHITECTURE.md` — trust and data-flow model.
- `docs/THREAT-MODEL.md` — explicit security assumptions and non-goals.

## Core trust invariant

The production host MUST NOT deploy `git pull main` directly. It deploys only a release that publishes a valid `SOURCE_COMMIT` plus checksum metadata and verifies that the checked-out repository state and installed binary correspond to that release.

The host MUST NOT hold a GitHub token capable of mutating EC repositories. It signs an attestation with a service-specific HMAC key and sends it to a receiver. The receiver owns the GitHub integration and can publish Deployment/Check status or open incidents.

## Initial status

`0.1` is a pre-adoption protocol. The first integration target is `id.exergism.org`. The receiver/GitHub-App implementation is intentionally a separate step from the host agent so its credentials and failure modes remain isolated.

See `docs/ARCHITECTURE.md` before deploying the agent.
