#!/bin/bash
set -euo pipefail
if [[ ! "$TRIP_DB_PASSWORD" =~ ^[A-Za-z0-9_-]+$ ]]; then
  echo 'TRIP_DB_PASSWORD must contain only letters, numbers, underscore or hyphen.' >&2
  exit 1
fi
export SQLCMDPASSWORD="$SQLSERVER_SA_PASSWORD"
/opt/mssql-tools18/bin/sqlcmd -S sqlserver -U sa -C -b -v TripPassword="$TRIP_DB_PASSWORD" -i /init/init.sql
