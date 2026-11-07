#!/usr/bin/env bash
# backup.sh + restore-drill.sh davranış testi (real PostgreSQL): uğurlu dövrə + 3 mənfi hal (zədələnmə, yanlış açar, saxta say).
# İstifadə: ADMIN_URL=postgresql:///postgres?host=/tmp&port=54329&user=postgres ./test-backup.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"; ROOT="$HERE/../.."
ADMIN="${ADMIN_URL:?}"; DB="bk_src_$$"
BASE="${ADMIN%%\?*}"; QUERY=""; [[ "$ADMIN" == *\?* ]] && QUERY="?${ADMIN#*\?}"
export DATABASE_URL="${BASE%/*}/$DB$QUERY"
TMP="$(mktemp -d)"; export BACKUP_DIR="$TMP/backups"
trap 'psql "$ADMIN" -qc "DROP DATABASE IF EXISTS $DB WITH (FORCE)" >/dev/null 2>&1 || true; rm -rf "$TMP"' EXIT
ok() { echo "ok: $*"; }; fail() { echo "FAIL: $*" >&2; exit 1; }
psql "$ADMIN" -qc "CREATE DATABASE $DB"
"$ROOT/db/migrate.sh" create-tenant acme >/dev/null; "$ROOT/db/migrate.sh" create-tenant beta >/dev/null
psql "$DATABASE_URL" -qc "SET search_path=t_acme,public" -c "INSERT INTO branches(code,name) VALUES ('B1','Mərkəz')" \
  -c "INSERT INTO patients(branch_id,first_name,last_name) SELECT id,'Əli'||g,'Test' FROM branches, generate_series(1,25) g"
age-keygen -o "$TMP/key.txt" 2>"$TMP/pub.txt"; PUB="$(grep -o 'age1[a-z0-9]*' "$TMP/pub.txt" | head -1)"
age-keygen -o "$TMP/other.txt" >/dev/null 2>&1

export AGE_RECIPIENTS="$PUB"; "$HERE/backup.sh" >/dev/null
SET="$(find "$BACKUP_DIR" -mindepth 1 -maxdepth 1 -type d | head -1)"
[[ -f "$SET/full.dump.age" && -f "$SET/tenant-acme.dump.age" && ! -e "$SET/full.dump" ]] || fail "encrypted files only, no plaintext dump left"
python3 -c "import json,sys;m=json.load(open(sys.argv[1]));sys.exit(0 if m['tenants']['acme']['patients']==25 else 1)" "$SET/meta.json" || fail "meta has counts"
ok "backup written encrypted, plaintext removed, counts recorded"

export AGE_IDENTITY_FILE="$TMP/key.txt"
"$HERE/restore-drill.sh" | tail -1 | grep -q "UĞURLU" || fail "drill must pass on a good backup"
ok "full restore drill passes"
DRILL_TENANT=acme "$HERE/restore-drill.sh" | tail -1 | grep -q "UĞURLU" || fail "single-tenant drill must pass"
ok "single-tenant restore passes"
[[ -z "$(psql "$ADMIN" -Atc "select datname from pg_database where datname like 'drill_%'")" ]] || fail "scratch databases must be dropped"
ok "scratch databases cleaned up"

AGE_IDENTITY_FILE="$TMP/other.txt" "$HERE/restore-drill.sh" >/dev/null 2>&1 && fail "wrong key must fail" || ok "wrong private key rejected"

cp "$SET/tenant-beta.dump.age" "$TMP/beta.bak"; printf 'X' | dd of="$SET/tenant-beta.dump.age" bs=1 seek=100 conv=notrunc 2>/dev/null
"$HERE/restore-drill.sh" >/dev/null 2>&1 && fail "corrupted file must fail the manifest check" || ok "corruption detected by manifest"
cp "$TMP/beta.bak" "$SET/tenant-beta.dump.age"

python3 - "$SET/meta.json" <<'PY'
import json, sys
p = sys.argv[1]
m = json.load(open(p))
m["tenants"]["acme"]["patients"] += 1
json.dump(m, open(p, "w"))
PY
( cd "$SET" && sha256sum -- *.age meta.json > MANIFEST.sha256 )
"$HERE/restore-drill.sh" >/dev/null 2>&1 && fail "count mismatch must fail" || ok "count mismatch detected (restored data ≠ recorded)"
echo "ALL OK"
