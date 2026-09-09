# Architecture

## Roles

Deployment Attestation separates four roles:

1. **Service owner repository** — authors code, semantic/application artifacts and release workflow.
2. **Production host agent** — verifies and activates a release, then reports what is actually running.
3. **Attestation receiver** — validates host signatures, replay windows and observation idempotency; it does not trust host claims blindly.
4. **GitHub projection** — converts validated observations into Deployment Status / Check Runs and incidents.

The host is deliberately not a GitHub authority. Compromising the host must not give an attacker repository mutation credentials.

## Release flow

```text
service main
   |
   v
CI / release workflow
   |
   +-- DEPLOYMENT_MANIFEST.json
   |      +-- source_commit
   |      +-- release_tag/channel
   |      +-- amd64 asset name + sha256
   |      +-- arm64 asset name + sha256
   |
   +-- architecture-specific runtime assets
   |
   v
production agent
   |
   +-- snapshot one complete manifest response
   +-- verify manifest repository/channel/commit/digests
   +-- download runtime and verify against manifest digest
   +-- fetch exact source commit named by manifest
   +-- verify current durable baseline
   +-- fsync rollback binary
   +-- persist transaction.json atomically
   +-- stop service
   +-- switch source + runtime
   +-- restart service
   +-- local health + service-specific smoke checks
   +-- commit current-state.json atomically
   +-- remove transaction journal durably
   +-- public health check
   |
   v
signed deployment attestation
```

A service must never be upgraded merely because its default branch moved.

### Why the release manifest is one object

A rolling GitHub release may replace assets through separate HTTP requests. Fetching `SOURCE_COMMIT`, checksum metadata and the runtime independently allows a reader to observe a cross-generation mixture while publication is in progress. `DEPLOYMENT_MANIFEST.json` is the snapshot boundary: one response binds the source commit and all runtime digests. The binary may still be fetched separately, but it is accepted only if its SHA-256 equals the digest captured in that manifest response. Any old/new race therefore fails closed rather than being interpreted as a valid pair.

## Durable activation state

Each service has root-owned state under `/var/lib/ec-deployment-attestation/<service>/`:

- `current-state.json` — committed source commit, runtime SHA-256 and release-manifest SHA-256;
- `transaction.json` — durable pre-activation rollback journal;
- `backups/` — verified rollback binaries;
- `agent.lock` — process exclusion lock.

Before creating a transaction, the agent requires repository `HEAD` and the installed binary digest to match `current-state.json`. It then writes and fsyncs the rollback material before atomically persisting `transaction.json`.

If the process is killed or the host reboots at any later point, the next invocation sees the transaction journal and restores the old source/runtime pair before doing anything else. It never derives a rollback baseline from the partially switched checkout.

Rollback restoration is explicit fail-closed logic: every source, binary and state restoration step is checked individually. The service is not started unless the complete old pair has been restored and recorded. A failure to atomically record the new `current-state.json` is treated as activation failure and also triggers rollback.

## Attestation flow

The host serializes the v0.1 JSON payload using compact, sorted JSON. It sends:

- `Content-Type: application/json`
- `X-EC-Timestamp: <unix-seconds>`
- `X-EC-Signature: sha256=<hex-hmac>`
- `Idempotency-Key: <observation_id>`

The signature input is exactly:

```text
<timestamp>.<raw-request-body>
```

using HMAC-SHA256 with a service-specific secret.

The signed body includes:

- deployed and expected source commits;
- SHA-256 of the captured release manifest;
- deployed and expected runtime SHA-256;
- health checks;
- `observation_id`, derived from the canonical observation before signing.

The receiver must:

1. reject missing/invalid signatures;
2. use constant-time signature comparison;
3. reject timestamps outside a small replay window (recommended: 300 seconds);
4. reject malformed payloads against the published schema;
5. bind the HMAC key to the expected `service`/`repository` identity;
6. require `Idempotency-Key == observation_id`;
7. deduplicate the trusted service + observation ID before updating counters or GitHub;
8. independently compare expected commit/runtime/manifest digest with current release metadata;
9. project only first-seen accepted observations to GitHub using credentials that never reach the production host.

Transport retries intentionally reuse the same signed body. A duplicate must return the same successful acknowledgement and must not increment failure thresholds or create duplicate GitHub mutations.

## GitHub projection

Healthy periodic observations should not create commits or issues. Preferred surfaces are:

- Deployment + Deployment Status for the deployed commit;
- Check Run on the deployed commit for detailed health;
- issue creation only after a policy-defined number/duration of consecutive failures.

A receiver maintains incident and deduplication state outside Git history to prevent issue spam.

## Status semantics

- `healthy` — deployed source and runtime equal the captured release snapshot and all mandatory checks pass.
- `degraded` — the deployed release is internally valid/available but a non-critical external observation fails, such as public HTTPS while loopback remains healthy.
- `unhealthy` — service availability, source/runtime/state binding or mandatory service-specific checks fail.

## Rollback

A rollback transaction contains the pre-activation source commit, runtime digest, prior manifest digest and path to a verified durable backup binary. Recovery restores source, runtime and current-state metadata before the service is restarted. The transaction journal is removed only after the restored service passes mandatory local health/smoke checks.

Rollback does not make a bad release good: the GitHub projection should still mark the attempted release as failed/deployment-error.

## Service-specific checks

The generic agent validates process state, local HTTP reachability, public HTTPS reachability, source-tree identity, durable-state identity and runtime digest. A service may provide an executable smoke script via `EC_SMOKE_SCRIPT` for domain-specific invariants.

For `id.exergism.org`, the reference smoke script verifies Commons/Governance semantic version routes, the fail-closed authority profile, catalogs and Turtle content negotiation.

## Receiver boundary

The receiver is intentionally not implemented inside the initial host-agent commit. Its production implementation should use a GitHub App with minimum permissions rather than a long-lived personal token. This repository defines that integration independently so host rollout does not wait on GitHub credential design.
