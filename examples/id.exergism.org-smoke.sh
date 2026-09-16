#!/usr/bin/env bash
set -Eeuo pipefail

LOCAL="${EC_LOCAL_URL%/}"
tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT

fetch_turtle() {
  local path="$1" output="$2"
  curl -fsS --max-time 10 -H 'Accept: text/turtle' "${LOCAL}${path}" -o "$output"
}

fetch_turtle /ontology/commons/0.1-PRE2 "$tmpdir/commons.ttl"
grep -Fq 'owl:versionIRI <https://id.exergism.org/ontology/commons/0.1-PRE2>' "$tmpdir/commons.ttl"

fetch_turtle /ontology/governance/0.1-PRE2 "$tmpdir/governance.ttl"
grep -Fq 'owl:versionIRI <https://id.exergism.org/ontology/governance/0.1-PRE2>' "$tmpdir/governance.ttl"

curl -fsS --max-time 10 "${LOCAL}/governance/profile/0.1-DRAFT" \
  | jq -e '.operative == false' >/dev/null

curl -fsS --max-time 10 "${LOCAL}/catalog/namespaces" | jq -e . >/dev/null
curl -fsS --max-time 10 "${LOCAL}/catalog/terms" | jq -e . >/dev/null

fetch_turtle /commons "$tmpdir/commons-base.ttl"
grep -Fq 'https://id.exergism.org/ontology/commons' "$tmpdir/commons-base.ttl"

fetch_turtle /governance "$tmpdir/governance-base.ttl"
grep -Fq 'https://id.exergism.org/ontology/governance' "$tmpdir/governance-base.ttl"
