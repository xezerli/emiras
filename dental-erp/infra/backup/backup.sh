#!/usr/bin/env bash
# Məntiqi ehtiyat nüsxə: bütün baza (bir ardıcıl snapshot) + hər tenant üçün ayrıca dump (tək klinikanı bərpa etmək üçün).
# Hər fayl age ilə ŞİFRƏLƏNİR (açıq açar: yalnız şifrələyə bilər; gizli açar bu işdə YOXDUR). Yoxlama üçün SHA-256 manifest + meta.json.
#
#   DATABASE_URL     (məcburi)   BACKUP_DIR (default /backups)   AGE_RECIPIENTS (məcburi, vergüllə: age1...,age1...)
#   BACKUP_KEEP_DAYS (default 14; 0 = silmə)   BACKUP_S3_URI (könüllü, aws CLI ilə kopyalanır)
#
# Bu, PITR-i əvəz ETMİR: WAL arxivləşdirmə (pgBackRest/CloudNativePG) saatlıq RPO üçündür; bu skript portativ əsas xəttdir (local server də).
set -euo pipefail
: "${DATABASE_URL:?}"; : "${AGE_RECIPIENTS:?AGE_RECIPIENTS təyin olunmalıdır (age açıq açarları)}"
DIR="${BACKUP_DIR:-/backups}"; STAMP="$(date -u +%Y%m%dT%H%M%SZ)"; OUT="$DIR/$STAMP"
umask 077; mkdir -p "$OUT"
recipients=(); IFS=',' read -ra R <<< "$AGE_RECIPIENTS"; for r in "${R[@]}"; do recipients+=(-r "$r"); done
q() { psql "$DATABASE_URL" -X -At -v ON_ERROR_STOP=1 -c "$1"; }

encrypt() { age "${recipients[@]}" -o "$2" "$1" && rm -f "$1"; }

echo "tam dump"
pg_dump "$DATABASE_URL" -Fc --no-owner --no-privileges -f "$OUT/full.dump"
encrypt "$OUT/full.dump" "$OUT/full.dump.age"

tenants=""
while IFS='|' read -r slug schema; do
  [[ -n "$slug" ]] || continue
  [[ "$schema" =~ ^t_[a-z0-9_]{1,40}$ ]] || { echo "etibarsız sxem adı atlandı: $schema" >&2; continue; }
  echo "tenant $slug"
  pg_dump "$DATABASE_URL" -Fc --no-owner --no-privileges -n "$schema" -f "$OUT/tenant-$slug.dump"
  encrypt "$OUT/tenant-$slug.dump" "$OUT/tenant-$slug.dump.age"
  # Bərpa sınağı bu say-ları dump-dan çıxan nəticə ilə müqayisə edir (tranzaksiyaya yaxın, canlı bazadan oxunur)
  counts="$(q "SELECT json_build_object('patients',(SELECT count(*) FROM $schema.patients),'appointments',(SELECT count(*) FROM $schema.appointments),'invoices',(SELECT count(*) FROM $schema.invoices),'payments',(SELECT count(*) FROM $schema.payments),'audit_log',(SELECT count(*) FROM $schema.audit_log),'migrations',(SELECT count(*) FROM platform.schema_migrations WHERE scope='$slug'))")"
  tenants="${tenants:+$tenants,}\"$slug\":$counts"
done < <(q "SELECT slug||'|'||schema_name FROM platform.tenants WHERE deleted_at IS NULL ORDER BY slug")

# Uzantılar (btree_gist, pg_trgm...) tenant sxeminin DEYİL, bazanın tərkibidir: tək-tenant dump-u boş bazaya qaytaranda əvvəlcədən qurulmalıdır
extensions="$(q "SELECT coalesce(string_agg('\"'||extname||'\"', ','), '') FROM pg_extension WHERE extname <> 'plpgsql'")"
printf '{"createdAt":"%s","pgVersion":"%s","extensions":[%s],"tenants":{%s}}\n' "$STAMP" "$(q 'SHOW server_version')" "$extensions" "$tenants" > "$OUT/meta.json"
( cd "$OUT" && sha256sum -- *.age meta.json > MANIFEST.sha256 )
echo "hazırdır: $OUT"

if [[ -n "${BACKUP_S3_URI:-}" ]]; then aws s3 cp --recursive --only-show-errors "$OUT" "${BACKUP_S3_URI%/}/$STAMP/"; fi
if [[ "${BACKUP_KEEP_DAYS:-14}" != "0" ]]; then
  find "$DIR" -mindepth 1 -maxdepth 1 -type d -mtime "+${BACKUP_KEEP_DAYS:-14}" -exec rm -rf {} +
fi
