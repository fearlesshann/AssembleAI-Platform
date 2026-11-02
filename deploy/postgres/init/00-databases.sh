#!/bin/sh
set -eu
: "${PLATFORM_DB_PASSWORD:?PLATFORM_DB_PASSWORD required}"
: "${KEYCLOAK_DB_PASSWORD:?KEYCLOAK_DB_PASSWORD required}"
psql --username "$POSTGRES_USER" --dbname postgres --set ON_ERROR_STOP=1 \
  --set platform_password="$PLATFORM_DB_PASSWORD" \
  --set keycloak_password="$KEYCLOAK_DB_PASSWORD" <<'SQL'
CREATE ROLE assembleai_platform LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'platform_password';
CREATE DATABASE assembleai_platform OWNER assembleai_platform;
CREATE ROLE keycloak LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS PASSWORD :'keycloak_password';
CREATE DATABASE keycloak OWNER keycloak;
REVOKE CONNECT ON DATABASE assembleai_platform FROM PUBLIC;
REVOKE CONNECT ON DATABASE keycloak FROM PUBLIC;
GRANT CONNECT ON DATABASE assembleai_platform TO assembleai_platform;
GRANT CONNECT ON DATABASE keycloak TO keycloak;
SQL
psql --username "$POSTGRES_USER" --dbname assembleai_platform --set ON_ERROR_STOP=1 --file /migrations/apply.sql
