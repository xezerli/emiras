#!/usr/bin/env bash
# Bərpa sınağı: ən son (və ya verilmiş) ehtiyat nüsxəni BOŞ müvəqqəti bazaya qaytarır və yoxlayır. Yoxlanmamış ehtiyat nüsxə ehtiyat nüsxə deyil.
#   ADMIN_URL   (məcburi)  bazaları yaradıb silə bilən istifadəçi, postgres bazasına URL     AGE_IDENTITY_FILE (məcburi)  age gizli açar faylı
#   BACKUP_DIR (default /backups)   BACKUP_SET (default: ən yenisi)   DRILL_TENANT (könüllü: yalnız bu klinikanı tenant dump-dan bərpa et)
# Yoxlamalar: SHA-256 manifest; bərpa xətasız; hər tenant üçün say-lar meta.json ilə EYNİ; miqrasiya sayı eyni; sxem boş deyil.
# Çıxış kodu 0 = bərpa edilə bilən. Uğur vaxtı CronJob-un son uğurlu vaxtı kimi izlənir (BackupTooOld/RestoreDrillOverdue xəbərdarlıqları).
set -euo pipefail
: "${ADMIN_URL:?}"; : "${AGE_IDENTITY_FILE:?}"
DIR="${BACKUP_DIR:-/backups}"
SET="${BACKUP_SET:-$(find "$DIR" -mindepth 1 -maxdepth 1 -type d | sort | tail -1)}"
[[ -d "$SET" ]] || { echo "ehtiyat nüsxə tapılmadı: $DIR" >&2; exit 1; }
fail() { echo "BƏRPA SINAĞI UĞURSUZ: $*" >&2; exit 1; }
DBNAME="drill_$(date -u +%Y%m%d%H%M%S)_$$"
BASE="${ADMIN_URL%%\?*}"; QUERY=""; [[ "$ADMIN_URL" == *\?* ]] && QUERY="?${ADMIN_URL#*\?}"
URL="${BASE%/*}/$DBNAME$QUERY"
WORK="$(mktemp -d)"; umask 077
cleanup() { rm -rf "$WORK"; psql "$ADMIN_URL" -qc "DROP DATABASE IF EXISTS $DBNAME WITH (FORCE)" >/dev/null 2>&1 || true; }
trap cleanup EXIT

echo "set: $SET"
( cd "$SET" && sha256sum --quiet -c MANIFEST.sha256 ) || fail "manifest uyğunsuzluğu (fayl zədələnib və ya dəyişdirilib)"
psql "$ADMIN_URL" -qc "CREATE DATABASE $DBNAME"

if [[ -n "${DRILL_TENANT:-}" ]]; then
  age -d -i "$AGE_IDENTITY_FILE" -o "$WORK/in.dump" "$SET/tenant-$DRILL_TENANT.dump.age" || fail "deşifrə alınmadı"
else
  age -d -i "$AGE_IDENTITY_FILE" -o "$WORK/in.dump" "$SET/full.dump.age" || fail "deşifrə alınmadı"
fi
if [[ -n "${DRILL_TENANT:-}" ]]; then
  # Tək-tenant dump-u uzantıları daşımır (bax backup.sh): meta.json-dakı siyahı ilə əvvəlcədən qurulur
  python3 -c 'import json,sys;print("\n".join(json.load(open(sys.argv[1])).get("extensions", [])))' "$SET/meta.json" | while read -r ext; do
    [[ -n "$ext" ]] && psql "$URL" -qc "CREATE EXTENSION IF NOT EXISTS \"$ext\""
  done
fi
pg_restore -d "$URL" --no-owner --exit-on-error "$WORK/in.dump" || fail "pg_restore xətası"

q() { psql "$URL" -X -At -v ON_ERROR_STOP=1 -c "$1"; }
python3 - "$SET/meta.json" "${DRILL_TENANT:-}" > "$WORK/expect.tsv" <<'PY'
import json, sys
meta = json.load(open(sys.argv[1]))
only = sys.argv[2]
for slug, c in meta["tenants"].items():
    if only and slug != only:
        continue
    for k, v in c.items():
        print(f"{slug}\t{k}\t{v}")
PY
[[ -s "$WORK/expect.tsv" ]] || fail "meta.json-da yoxlanacaq tenant yoxdur"
checked=0
while IFS=$'\t' read -r slug key expected; do
  schema="t_$slug"
  if [[ "$key" == "migrations" ]]; then
    [[ -n "${DRILL_TENANT:-}" ]] && continue    # tək-tenant dump platform sxemini daşımır
    actual="$(q "SELECT count(*) FROM platform.schema_migrations WHERE scope='$slug'")"
  else
    actual="$(q "SELECT count(*) FROM $schema.$key")"
  fi
  [[ "$actual" == "$expected" ]] || fail "$slug.$key: bərpa olunan $actual ≠ gözlənilən $expected"
  checked=$((checked + 1))
done < "$WORK/expect.tsv"
echo "BƏRPA SINAĞI UĞURLU: $checked yoxlama keçdi ($SET)"
