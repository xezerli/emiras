#!/usr/bin/env bash
# migrate.sh davranış testi (real PostgreSQL, atılan baza). İstifadə: ADMIN_URL=postgresql://.../postgres ./test-migrate.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ADMIN="${ADMIN_URL:?ADMIN_URL təyin olunmalıdır (postgres bazasına superuser URL)}"
DBNAME="mig_test_$$"
BASE="${ADMIN%/*}"; QUERY=""; [[ "$ADMIN" == *\?* ]] && { QUERY="?${ADMIN#*\?}"; BASE="${ADMIN%%\?*}"; BASE="${BASE%/*}"; }
export DATABASE_URL="$BASE/$DBNAME$QUERY"
fail() { echo "FAIL: $*" >&2; exit 1; }
ok() { echo "ok: $*"; }
trap 'psql "$ADMIN" -qc "DROP DATABASE IF EXISTS $DBNAME WITH (FORCE)" >/dev/null 2>&1 || true; rm -f "$HERE"/migrations/tenant/T9*_probe.sql' EXIT
psql "$ADMIN" -qc "CREATE DATABASE $DBNAME"
M="$HERE/migrate.sh"
count() { psql "$DATABASE_URL" -Atc "$1"; }

"$M" create-tenant acme >/dev/null; "$M" create-tenant beta >/dev/null
[[ "$(count "select count(*) from platform.schema_migrations where scope='acme'")" == "$(ls "$HERE"/migrations/tenant/T*.sql | wc -l)" ]] || fail "all tenant migrations recorded"
ok "fresh tenants get every migration, tracked"

[[ "$("$M" up | grep -c tətbiq || true)" == "0" ]] || fail "second run must apply nothing"
ok "up is idempotent"

echo "CREATE TABLE zz_probe(id int);" > "$HERE/migrations/tenant/T999_probe.sql"
"$M" up >/dev/null
[[ "$(count "select count(*) from pg_tables where tablename='zz_probe'")" == "2" ]] || fail "new migration must reach every tenant"
ok "new migration reaches all tenants"

echo "-- tampered" >> "$HERE/migrations/tenant/T999_probe.sql"
if "$M" up >/dev/null 2>&1; then fail "changed applied migration must be rejected"; fi
ok "checksum change rejected"
rm "$HERE/migrations/tenant/T999_probe.sql"

printf 'CREATE TABLE zz_half(id int);\nSELECT 1/0;\n' > "$HERE/migrations/tenant/T998_probe.sql"
if "$M" up >/dev/null 2>&1; then fail "failing migration must fail the run"; fi
[[ "$(count "select to_regclass('t_acme.zz_half') is null")" == "t" ]] || fail "failed migration must roll back completely"
[[ "$(count "select count(*) from platform.schema_migrations where version='T998_probe'")" == "0" ]] || fail "failed migration must not be recorded"
ok "failed migration rolls back and is not recorded"
rm "$HERE/migrations/tenant/T998_probe.sql"

# izlənməyən (əl ilə qurulmuş) sxem: səssiz təkrar tətbiq YOX, aydın xəta
count "delete from platform.schema_migrations where scope='beta'" >/dev/null
if "$M" up >/dev/null 2>&1; then fail "untracked existing schema must be refused"; fi
"$M" baseline beta T012 >/dev/null; "$M" up >/dev/null
[[ "$(count "select count(*) from platform.schema_migrations where scope='beta'")" == "12" ]] || fail "baseline then up"
ok "untracked schema refused until baselined"
echo "ALL OK"
