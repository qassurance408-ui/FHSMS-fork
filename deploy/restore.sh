#!/usr/bin/env bash
# Restores a dump made by backup.sh. This REPLACES all current data.
#   bash deploy/restore.sh backups/fhsms-20260101-020000.sql.gz
set -euo pipefail
cd "$(dirname "$0")/.."
FILE="${1:-}"
[[ -f "$FILE" ]] || { echo "Usage: bash deploy/restore.sh <backup.sql.gz>"; exit 1; }
read -r -p "This will ERASE the current database and load $FILE. Type YES to continue: " ans
[[ "$ans" == "YES" ]] || { echo "Cancelled."; exit 1; }

DOCKER="docker"; docker info >/dev/null 2>&1 || DOCKER="sudo docker"
C="$DOCKER compose -f docker-compose.prod.yml"

$C stop api web bot 2>/dev/null || true
$C exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d postgres -c "DROP DATABASE IF EXISTS \"$POSTGRES_DB\" WITH (FORCE);" -c "CREATE DATABASE \"$POSTGRES_DB\" OWNER \"$POSTGRES_USER\";"'
gunzip -c "$FILE" | $C exec -T postgres sh -c 'psql -q -U "$POSTGRES_USER" -d "$POSTGRES_DB"'
$C up -d
echo "Restore complete."
