# Architecture

## Roles

Deployment Attestation separates four roles:

1. **Service owner repository** — authors code, semantic/application artifacts and release workflow.
2. **Production host agent** — verifies and activates a release, then reports what is actually running.
3. **Attestation receiver** — validates host signatures and replay windows; it does not trust host claims blindly.
4. **GitHub projection** — converts validated observations into Deployment Status / Check Runs and incidents.

The host is deliberately not a GitHub authority. Compromising the host must not give an attacker repository mutation credentials.

## Release flow

```text
service main
   |
   v
CI / release workflow
   |
   +-- SOURCE_COMMIT (40-hex source revision)
   +-- SHA256SUMS
   +-- architecture-specific runtime asset
   |
   v
production agent
   |
   +-- verify SOURCE_COMMIT syntax
   +-- verify binary SHA-256
   +-- fetch release tag / exact repository revision
   +-- prove checkout == SOURCE_COMMIT
   +-- retain rollback state
   +-- install binary atomically
   +-- restart service
   +-- local health + service-specific smoke checks
   +-- public health check
   |
   v
signed deployment attestation
```

A service must never be upgraded merely because its default branch moved.

## Attestation flow

The host serializes the v0.1 JSON payload using compact, sorted JSON. It sends:

- `Content-Type: application/json`
- `X-EC-Timestamp: <unix-seconds>`
- `X-EC-Signature: sha256=<hex-hmac>`

The signature input is exactly:

```text
<timestamp>.<raw-request-body>
```

using HMAC-SHA256 with a service-specific secret.

The receiver must:

1. reject missing/invalid signatures;
2. use constant-time signature comparison;
3. reject timestamps outside a small replay window (recommended: 300 seconds);
4. reject malformed payloads against the published schema;
5. bind the HMAC key to the expected `service`/`repository` identity;
6. optionally compare `expected_commit` with the GitHub release metadata independently;
7. project accepted observations to GitHub using credentials that never reach the production host.

## GitHub projection

Healthy periodic observations should not create commits or issues. Preferred surfaces are:

- Deployment + Deployment Status for the deployed commit;
- Check Run on the deployed commit for detailed health;
- issue creation only after a policy-defined number/duration of consecutive failures.

A receiver may maintain incident state outside Git history to prevent issue spam.

## Status semantics

- `healthy` — deployed commit equals expected release commit and all mandatory checks pass.
- `degraded` — service remains available but one or more non-critical observations fail (for example release metadata cannot currently be refreshed).
- `unhealthy` — service, commit binding, checksum, public endpoint or mandatory service-specific checks fail.

## Rollback

Before activation the agent records the previous repository commit and copies the previous runtime binary. If the new service cannot pass mandatory local checks, it restores both, restarts the service and emits an unhealthy attestation describing the failed expected revision while reporting the actually restored deployed revision.

Rollback does not make a bad release good: the GitHub projection should still mark the attempted release as failed/deployment-error.

## Service-specific checks

The generic agent validates process state, local HTTP reachability, public HTTPS reachability and source/release binding. A service may provide an executable smoke script via `EC_SMOKE_SCRIPT` for domain-specific invariants.

For `id.exergism.org`, the reference smoke script verifies Commons/Governance semantic version routes, the fail-closed authority profile, catalogs and Turtle content negotiation.

## Receiver boundary

The receiver is intentionally not implemented inside the initial host-agent commit. Its production implementation should use a GitHub App with minimum permissions rather than a long-lived personal token. This repository will define that integration independently so host rollout does not wait on GitHub credential design.
