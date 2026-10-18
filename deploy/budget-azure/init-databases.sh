#!/usr/bin/env bash
set -euo pipefail
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname postgres \
  --set=identity_password="$IDENTITY_DB_PASSWORD" \
  --set=catalog_password="$CATALOG_DB_PASSWORD" \
  --set=order_password="$ORDER_DB_PASSWORD" \
  --set=fulfilment_password="$FULFILMENT_DB_PASSWORD" <<'SQL'
CREATE ROLE identity_app LOGIN PASSWORD :'identity_password';
CREATE ROLE catalog_app LOGIN PASSWORD :'catalog_password';
CREATE ROLE order_app LOGIN PASSWORD :'order_password';
CREATE ROLE fulfilment_app LOGIN PASSWORD :'fulfilment_password';
CREATE DATABASE identity_db OWNER identity_app;
CREATE DATABASE catalog_db OWNER catalog_app;
CREATE DATABASE order_db OWNER order_app;
CREATE DATABASE fulfilment_db OWNER fulfilment_app;
REVOKE ALL ON DATABASE identity_db, catalog_db, order_db, fulfilment_db FROM PUBLIC;
GRANT CONNECT ON DATABASE identity_db TO identity_app;
GRANT CONNECT ON DATABASE catalog_db TO catalog_app;
GRANT CONNECT ON DATABASE order_db TO order_app;
GRANT CONNECT ON DATABASE fulfilment_db TO fulfilment_app;
SQL
