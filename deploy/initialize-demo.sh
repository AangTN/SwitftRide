#!/usr/bin/env bash
set -euo pipefail
cd /opt/swiftride
mkdir -p .secrets .tls/www .tls/letsencrypt
chmod 700 .secrets .tls
if [[ ! -e .env ]]; then
    umask 077
    for name in POSTGRES_ADMIN_PASSWORD AUTH_DB_PASSWORD PAYMENT_DB_PASSWORD SQLSERVER_SA_PASSWORD TRIP_DB_PASSWORD MONGO_ROOT_PASSWORD MATCHING_DB_PASSWORD RABBITMQ_PASSWORD OTP_HMAC_SECRET; do
        printf '%s=Sr1%s\n' "$name" "$(openssl rand -hex 24)" >> .env
    done
fi
if [[ ! -f .secrets/auth-private.pem ]]; then
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .secrets/auth-private.pem 2>/dev/null
    openssl pkey -in .secrets/auth-private.pem -pubout -out .secrets/auth-public.pem
fi
# .NET containers run as uid 1654; only the Auth mount receives the private key.
sudo chown 1654:1654 .secrets/auth-private.pem
sudo chmod 400 .secrets/auth-private.pem
chmod 644 .secrets/auth-public.pem
