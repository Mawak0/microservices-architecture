#!/bin/sh
set -eu
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set=ON_ERROR_STOP=1 --set=service_password="$SERVICE_DATABASE_PASSWORD" <<'SQL'
CREATE ROLE materials LOGIN PASSWORD :'service_password';
CREATE ROLE intake LOGIN PASSWORD :'service_password';
CREATE ROLE review LOGIN PASSWORD :'service_password';
CREATE DATABASE materials OWNER materials;
CREATE DATABASE intake OWNER intake;
CREATE DATABASE review OWNER review;
REVOKE CONNECT ON DATABASE materials FROM PUBLIC;
REVOKE CONNECT ON DATABASE intake FROM PUBLIC;
REVOKE CONNECT ON DATABASE review FROM PUBLIC;
GRANT CONNECT ON DATABASE materials TO materials;
GRANT CONNECT ON DATABASE intake TO intake;
GRANT CONNECT ON DATABASE review TO review;
SQL
