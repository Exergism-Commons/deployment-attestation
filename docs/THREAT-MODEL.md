# Threat Model

## Protected properties

Deployment Attestation is designed to protect these properties:

- the runtime activated on a host is bound to an explicit release snapshot and source commit;
- source commit and architecture-specific runtime digest come from one `DEPLOYMENT_MANIFEST.json` response rather than independently mutable assets;
- a host cannot silently claim a different deployed commit/runtime digest without detection by receiver-side cross-checks;
- a mutable default branch cannot directly trigger unverified production code;
- a rolling release publication race fails closed on digest mismatch rather than creating a mixed source/runtime deployment;
- a process crash or host reboot during activation does not silently rebase rollback onto partially switched state;
- a compromised production host does not automatically gain GitHub repository write access;
- failed activation can be rolled back to a previously recorded source/runtime pair;
- the long-lived service is fenced from writing the deployed source tree or runtime while those artifacts are being attested;
- health observations are authenticated, replay-bounded and idempotently processed.

## Trust assumptions

The design currently assumes:

- GitHub release metadata and HTTPS transport are available and authentic;
- the service repository release workflow is trusted to publish a correct atomic `DEPLOYMENT_MANIFEST.json` binding source commit to runtime digests;
- the host root account, local filesystem and systemd configuration are trusted until compromise;
- the production service runs inside the systemd mount namespace installed by Deployment Attestation, with the deployment source and runtime mounted read-only;
- the service-specific HMAC secret is readable only by root / the attestation agent;
- the receiver protects GitHub App credentials independently of the host;
- receiver-side durable storage can enforce observation deduplication and incident state.

## Explicit non-goals

The initial protocol does not attempt to prove host integrity against a root-level attacker. A root attacker can replace the agent, read its HMAC key and forge observations. Future work may add TPM/TEE-backed device identity or signed measurements, but v0.1 is an operational deployment-attestation protocol, not remote hardware attestation.

The protocol also does not make the receiver a semantic authority. It reports whether deployed artifacts match owner-authored releases; it does not decide what those artifacts mean.

## Rolling-release race resistance

A mutable rolling release can expose different generations of separately replaced assets. The updater therefore treats only `DEPLOYMENT_MANIFEST.json` as the release snapshot boundary. The manifest names the exact source commit and SHA-256 for each architecture runtime. A binary fetched before/after a channel update is rejected unless it matches the digest captured in that one manifest response.

The protocol does not claim that the manifest protects against a malicious trusted release workflow. It prevents accidental/transport-observable cross-generation mixtures under the stated trust model.

## Crash consistency and interrupted activation

Before source/runtime mutation, the agent:

1. verifies the installed checkout and binary against durable `current-state.json`;
2. copies and verifies the rollback binary;
3. fsyncs rollback material;
4. atomically writes/fsyncs `transaction.json`.

Any later invocation that sees `transaction.json` must recover it before accepting a new baseline. Recovery explicitly checks every restoration operation and does not start the service unless the old source/runtime pair has been completely restored and recorded.

`current-state.json` is committed only after the new release passes mandatory local checks. Failure to write/rename/fsync the committed state is an activation failure and triggers rollback. A crash after successful state commit but before journal removal may conservatively roll back on the next run; consistency is preferred to silently assuming completion.

## Replay resistance and idempotency

Every request carries a Unix timestamp in `X-EC-Timestamp`; the receiver must reject observations outside its accepted clock window.

Every signed body also contains `observation_id`, and the sender sends the same value in `Idempotency-Key`. Receiver-side deduplication is mandatory: a transport retry of an accepted observation must return the same successful acknowledgement without incrementing failure counters, changing incident thresholds or repeating GitHub mutations. Deduplication state must be retained for at least the larger of the replay window and incident-evaluation window.

## Secret rotation

HMAC keys are service-specific and must be independently rotatable. A receiver should support a short overlap window between old and new keys. Secrets must never be committed to this repository or stored in service repository Actions logs.

## Failure amplification

Health reporting must not create one GitHub issue per failed poll. Incident creation belongs to receiver policy: for example, create one issue after three distinct consecutive failed observations and update/close that same incident as state changes. Retries carrying an already-seen observation ID are not new observations.

## Update safety

The updater must fail closed when any of these checks fail:

- malformed deployment manifest;
- repository/channel mismatch in the manifest;
- malformed source commit or runtime digest;
- runtime asset digest mismatch against the captured manifest;
- existing checkout/runtime mismatch against durable current state;
- failure to durably prepare rollback state;
- checked-out repository revision does not equal manifest `source_commit`;
- failure to atomically record the newly activated state;
- post-restart mandatory local health failure.

A public health failure after successful local activation should be reported as degraded according to service policy, but should not necessarily trigger automatic rollback if the failure can be external to the host (DNS, upstream network, certificate propagation).

## Service artifact write fence

A running service is not considered a stable measurement source merely because the agent hashes an artifact twice. The id deployment installer places the resolver in a systemd mount namespace where both `/srv/id.exergism.org` and `/usr/local/bin/idresolver` are `ReadOnlyPaths`. The agent verifies the *live service namespace* through `MainPID`, `nsenter` and `findmnt` before accepting a running baseline, before post-start finalization, and before declaring an attestation healthy. The root-owned updater remains outside that namespace and can mutate those paths only while carrying out its journaled transaction.

If the live read-only fence is absent, the observation is unhealthy and an active service cannot be accepted as a new rollback baseline. Root-level mutation remains outside the v0.1 threat model.
