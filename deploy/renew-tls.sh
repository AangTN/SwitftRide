#!/usr/bin/env bash
set -euo pipefail
cd /opt/swiftride
docker run --rm \
    -v /opt/swiftride/.tls/letsencrypt:/etc/letsencrypt \
    -v /opt/swiftride/.tls/www:/var/www/acme \
    certbot/certbot:latest renew --webroot -w /var/www/acme --quiet
if [[ -f .release.env ]]; then
    set -a
    source .release.env
    set +a
    docker compose -f compose.yml -f compose.deploy.yml exec -T nginx nginx -t
    docker compose -f compose.yml -f compose.deploy.yml exec -T nginx nginx -s reload
fi
