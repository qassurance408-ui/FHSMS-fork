#!/usr/bin/env bash
# Dumps the PostgreSQL database to backups/fhsms-YYYYmmdd-HHMMSS.sql.gz
# Run it by hand, or daily from cron:
#   0 2 * * *  cd /opt/fhsms && bash deploy/backup.sh >> backups/backup.log 2>&1
# Keeps the newest 14 dumps. Copy backups OFF the server as well
# (another machine, object storage, ...) - a backup on the same disk is not enough.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p backups
DOCKER="docker"; docker info >/dev/null 2>&1 || DOCKER="sudo docker"
OUT="backups/fhsms-$(date +%Y%m%d-%H%M%S).sql.gz"

$DOCKER compose -f docker-compose.prod.yml exec -T postgres \
  sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" --no-owner' | gzip > "$OUT"

[[ -s "$OUT" ]] || { echo "Backup failed (empty file)"; rm -f "$OUT"; exit 1; }
echo "Backup written: $OUT ($(du -h "$OUT" | cut -f1))"
ls -1t backups/fhsms-*.sql.gz 2>/dev/null | tail -n +15 | xargs -r rm --
