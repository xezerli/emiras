#!/usr/bin/env bash
# İstifadə: ./provision.sh <schema_name> [DATABASE_URL]
# Platform sxemini (bir dəfə) və tenant sxemini yaradır. Hər fayl bir tranzaksiyada işləyir.
set -euo pipefail
SCHEMA="${1:?tenant schema, məs. t_demo}"; DB="${2:-${DATABASE_URL:-}}"
[[ "$SCHEMA" =~ ^t_[a-z0-9_]{1,40}$ ]] || { echo "yanlış schema adı" >&2; exit 1; }
HERE="$(cd "$(dirname "$0")" && pwd)"
PSQL=(psql ${DB:+"$DB"} -v ON_ERROR_STOP=1 -q)
"${PSQL[@]}" -c "SELECT to_regclass('platform.tenants')" -t | grep -q tenants || \
  for f in "$HERE"/migrations/platform/P*.sql; do "${PSQL[@]}" --single-transaction -f "$f"; done
"${PSQL[@]}" -c "CREATE SCHEMA IF NOT EXISTS $SCHEMA"
for f in "$HERE"/migrations/tenant/T*.sql; do
  PGOPTIONS="-c search_path=$SCHEMA,public" "${PSQL[@]}" --single-transaction -f "$f"
done
echo "OK: $SCHEMA"
