# Receiver / GitHub bridge

The receiver is the trust boundary between production hosts and GitHub.

It is intentionally not part of the initial host-agent implementation. The host can already auto-update and emit locally signed payloads, but production GitHub projection should not be enabled until this receiver exists.

## Required receiver behavior

1. Accept HTTPS POST requests containing an Attestation v0.1 body.
2. Validate `X-EC-Timestamp` within a bounded replay window (recommended 300 seconds).
3. Select the expected service-specific HMAC key from trusted receiver configuration, never from request-controlled identity alone.
4. Verify `X-EC-Signature` over `<timestamp>.<raw-body>` using constant-time comparison.
5. Validate the JSON payload against `spec/attestation-v0.1.schema.json`.
6. Require `Idempotency-Key` to equal the signed body field `observation_id`.
7. Persist a deduplication record keyed by trusted service identity plus `observation_id` **before** incrementing failure counters or mutating GitHub. A duplicate submission MUST return the same successful acknowledgement and MUST NOT increment health/failure thresholds, create another Check/Deployment mutation, or create another incident. Retain deduplication state for at least the larger of the replay window and the incident-evaluation window.
8. Independently verify that `expected_commit`, `expected_runtime_sha256`, and `release_manifest_sha256` correspond to the service's current `DEPLOYMENT_MANIFEST.json` release snapshot.
9. Project accepted, first-seen observations into GitHub.

The agent may retry an identical POST when a response is lost. Idempotency is therefore a protocol requirement, not an optional receiver optimization.

## Preferred GitHub surfaces

Healthy heartbeat traffic should not mutate Git history.

Preferred projection:

- Deployment status for the exact deployed commit;
- Check Run with per-check details;
- one incident Issue only after a failure threshold is reached;
- update/close the existing incident rather than creating one issue per health poll.

## Authentication to GitHub

Production should use a dedicated GitHub App with minimum repository permissions. Do not put a PAT or GitHub App private key on the Droplet.

Likely permissions:

- Checks: read/write;
- Deployments: read/write;
- Issues: read/write only if automatic incidents are enabled;
- Metadata: read.

Repository contents write permission is not required for health projection and should not be granted.

## Deployment target

A small DigitalOcean Function is a suitable receiver because it keeps GitHub credentials outside the service host and can scale independently. The protocol intentionally does not depend on DigitalOcean; any HTTPS service implementing the receiver contract can be used.
