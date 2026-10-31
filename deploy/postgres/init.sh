#!/bin/sh
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  -v auth_password="$AUTH_DB_PASSWORD" -v payment_password="$PAYMENT_DB_PASSWORD" <<'SQL'
CREATE ROLE auth_app LOGIN PASSWORD :'auth_password';
CREATE ROLE payment_app LOGIN PASSWORD :'payment_password';
CREATE DATABASE "AuthDB" OWNER auth_app;
CREATE DATABASE "PaymentDB" OWNER payment_app;
REVOKE CONNECT ON DATABASE "AuthDB" FROM PUBLIC;
REVOKE CONNECT ON DATABASE "PaymentDB" FROM PUBLIC;
GRANT CONNECT ON DATABASE "AuthDB" TO auth_app;
GRANT CONNECT ON DATABASE "PaymentDB" TO payment_app;
SQL
