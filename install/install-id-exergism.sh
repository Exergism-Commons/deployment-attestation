#!/usr/bin/env bash
set -Eeuo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run as root (or with sudo)." >&2
  exit 1
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SERVICE="id.exergism.org"

install -d -m 0755 /usr/local/libexec
install -d -m 0755 /etc/ec-deployment-attestation
install -d -m 0700 /etc/ec-deployment-attestation/secrets

install -o root -g root -m 0755 "$ROOT/agent/ec-deployment-agent.sh" \
  /usr/local/libexec/ec-deployment-agent
install -o root -g root -m 0755 "$ROOT/examples/id.exergism.org-smoke.sh" \
  /usr/local/libexec/id.exergism.org-smoke.sh
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.service" \
  /etc/systemd/system/ec-deployment-attestation@.service
install -o root -g root -m 0644 "$ROOT/packaging/ec-deployment-attestation@.timer" \
  /etc/systemd/system/ec-deployment-attestation@.timer

if [[ ! -e "/etc/ec-deployment-attestation/${SERVICE}.env" ]]; then
  install -o root -g root -m 0640 "$ROOT/examples/id.exergism.org.env.example" \
    "/etc/ec-deployment-attestation/${SERVICE}.env"
fi

systemctl daemon-reload
systemctl enable --now "ec-deployment-attestation@${SERVICE}.timer"

printf '\nInstalled Deployment Attestation agent for %s.\n' "$SERVICE"
printf 'Config: /etc/ec-deployment-attestation/%s.env\n' "$SERVICE"
printf 'Manual run: systemctl start ec-deployment-attestation@%s.service\n' "$SERVICE"
printf 'Logs: journalctl -u ec-deployment-attestation@%s.service -n 100 --no-pager\n' "$SERVICE"
printf 'Timer: systemctl status ec-deployment-attestation@%s.timer\n' "$SERVICE"
printf '\nThe updater is active. Attestations remain local journal output until EC_ATTESTATION_ENDPOINT and EC_HMAC_SECRET_FILE are configured.\n'
