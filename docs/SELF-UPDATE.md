# Opt-in agent package updates

Self-updates are disabled by default. Installing or reinstalling a package does
not enable the self-update timer and preserves an existing policy.

After a reviewed one-time installation of a release that includes this feature,
explicitly opt in as root:

```sh
printf '{ "enabled": true }\n' > /etc/ec-deployment-attestation/self-update.json
chmod 0600 /etc/ec-deployment-attestation/self-update.json
systemctl enable --now ec-deployment-agent-self-update.timer
```

The timer checks the latest published stable GitHub release daily, with up to an
hour of jitter. An explicit check uses
`systemctl start ec-deployment-agent-self-update.service`.
`ec-deployment-agent package-version` reports the installed package version.

To opt out:

```sh
printf '{ "enabled": false }\n' > /etc/ec-deployment-attestation/self-update.json
chmod 0600 /etc/ec-deployment-attestation/self-update.json
systemctl disable --now ec-deployment-agent-self-update.timer
```

A disabled or absent policy performs no network request. The policy is read again
immediately before invoking the installer, so withdrawal during a download
discards the stage. Disabling the timer prevents future runs; an installer that
has already entered its transaction finishes or rolls back that transaction.

The installed agent invokes the installed Python helper. That running helper
snapshots GitHub release metadata, verifies the manifest against its API asset
digest, checks every agent/installer/helper digest against the same manifest,
and fetches source at the exact recorded commit. The reviewed installer binds
all executable and configuration inputs to its embedded digest table. Source
archives reject escaping paths, duplicate entries, links and oversized content.

The currently running generation finishes the installation; the new generation
handles the next check. Agent, helper, self-update unit/timer and initial policy
are covered by the installer journal. Existing schema-2 journals remain readable;
schema 3 includes package updater artifacts. Atomic replacement permits the old
agent/helper to finish while new bytes are published.

An unfinished application or installer transaction blocks a package update.
The existing installer owns stop/quiescence locks, candidate self-test, config
checks, semantic resolver checks, fsync, rollback and boot recovery. No updater
journal is deleted to force a new baseline. Installation failures retain
actionable recovery state if restoration cannot be verified.

Scope: the current host installer targets `id.exergism.org` on Linux x64. The
same service updates its complete agent package, including this helper, but
does not run a second independent deployment/attestation stack. The opt-in
grants the repository's release workflow authority to publish root-executed
updates. HTTPS and GitHub API asset digests bind one generation; this does not
claim protection against a compromised trusted publisher or root attacker.

