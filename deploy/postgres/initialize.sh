#!/bin/sh
set -eu
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set=ON_ERROR_STOP=1 \
  --set=materials_password="$MATERIALS_DATABASE_PASSWORD" --set=intake_password="$INTAKE_DATABASE_PASSWORD" \
  --set=review_password="$REVIEW_DATABASE_PASSWORD" --set=demo_password="$DEMO_DATABASE_PASSWORD" <<'SQL'
CREATE ROLE materials LOGIN PASSWORD :'materials_password';
CREATE ROLE intake LOGIN PASSWORD :'intake_password';
CREATE ROLE review LOGIN PASSWORD :'review_password';
CREATE ROLE demo LOGIN PASSWORD :'demo_password';
CREATE DATABASE materials OWNER materials;
REVOKE CONNECT ON DATABASE materials FROM PUBLIC;
GRANT CONNECT ON DATABASE materials TO materials;
CREATE DATABASE intake OWNER intake;
REVOKE CONNECT ON DATABASE intake FROM PUBLIC;
GRANT CONNECT ON DATABASE intake TO intake;
CREATE DATABASE review OWNER review;
REVOKE CONNECT ON DATABASE review FROM PUBLIC;
GRANT CONNECT ON DATABASE review TO review;
CREATE DATABASE demo OWNER demo;
REVOKE CONNECT ON DATABASE demo FROM PUBLIC;
GRANT CONNECT ON DATABASE demo TO demo;
SQL
