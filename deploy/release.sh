#!/usr/bin/env bash
set -euo pipefail
cd /opt/swiftride
[[ "$1" =~ ^[a-f0-9]{40}$ ]] || { echo 'Expected a full commit SHA'; exit 1; }
export DEPLOY_TAG="$1"
export PUBLIC_HOST="${2:?Public IP required}"
export REGISTRY_PREFIX="ghcr.io/aangtn/switftride"
bash deploy/initialize-demo.sh
# Shell environment takes precedence over .env for Compose interpolation.
# The REST key is provisioned over SSH stdin and is never put in frontend builds.
if [[ -f .secrets/goong-api-key ]]; then
    export GOONG_API_KEY="$(cat .secrets/goong-api-key)"
    if [[ -z "$GOONG_API_KEY" ]]; then
        echo 'Matching requires a non-empty Goong REST key.' >&2
        exit 1
    fi
fi
compose=(docker compose -f compose.yml -f compose.deploy.yml)
"${compose[@]}" pull
"${compose[@]}" up -d --no-build --wait --wait-timeout 360
curl --fail --retry 6 --retry-delay 5 "https://${PUBLIC_HOST}/health/ready"
printf 'DEPLOY_TAG=%s\nPUBLIC_HOST=%s\nREGISTRY_PREFIX=%s\n' "$DEPLOY_TAG" "$PUBLIC_HOST" "$REGISTRY_PREFIX" > .release.env
