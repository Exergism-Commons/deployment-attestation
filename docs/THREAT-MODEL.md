# Threat Model

## Protected properties

Deployment Attestation is designed to protect these properties:

- the runtime activated on a host is bound to an explicit release and source commit;
- a host cannot silently claim a different deployed commit without detection by receiver-side cross-checks;
- a mutable default branch cannot directly trigger unverified production code;
- a compromised production host does not automatically gain GitHub repository write access;
- immutable release checksums are verified before binary activation;
- failed activation can be rolled back to a previously known working revision;
- health observations are authenticated and replay-bounded.

## Trust assumptions

The design currently assumes:

- GitHub release metadata and HTTPS transport are available and authentic;
- the service repository release workflow is trusted to publish correct `SOURCE_COMMIT` and checksum metadata;
- the host root account and systemd configuration are trusted until compromise;
- the service-specific HMAC secret is readable only by root / the attestation agent;
- the receiver protects GitHub App credentials independently of the host.

## Explicit non-goals

The initial protocol does not attempt to prove host integrity against a root-level attacker. A root attacker can replace the agent, read its HMAC key and forge observations. Future work may add TPM/TEE-backed device identity or signed measurements, but v0.1 is an operational deployment-attestation protocol, not remote hardware attestation.

The protocol also does not make the receiver a semantic authority. It reports whether deployed artifacts match owner-authored releases; it does not decide what those artifacts mean.

## Replay resistance

Every request carries a Unix timestamp in `X-EC-Timestamp`; the receiver must reject observations outside its accepted clock window. Receivers should also retain a short-lived digest cache if duplicate suppression is required.

## Secret rotation

HMAC keys are service-specific and must be independently rotatable. A receiver should support a short overlap window between old and new keys. Secrets must never be committed to this repository or stored in service repository Actions logs.

## Failure amplification

Health reporting must not create one GitHub issue per failed poll. Incident creation belongs to receiver policy: for example, create one issue after three consecutive failures and update/close that same incident as state changes.

## Update safety

The updater must fail closed when any of these checks fail:

- malformed release source commit;
- missing expected asset checksum;
- checksum mismatch;
- checked-out repository revision does not equal release `SOURCE_COMMIT`;
- post-restart mandatory local health failure.

A public health failure after successful local activation should be reported as unhealthy/degraded according to service policy, but should not necessarily trigger automatic rollback if the failure can be external to the host (DNS, upstream network, certificate propagation).
