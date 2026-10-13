#!/usr/bin/env bash
# Versiyalı, irəli-yönlü miqrasiya icraçısı (platform + hər tenant sxemi).
#
#   migrate.sh up                     gözləyən P*/T* miqrasiyalarını platformaya və BÜTÜN aktiv tenant sxemlərinə tətbiq edir
#   migrate.sh create-tenant <slug> [ad] [plan]   yeni tenant: platform.tenants sətri + sxem + bütün T* miqrasiyaları
#   migrate.sh status                 hər sxem üçün tətbiq olunmuş / gözləyən miqrasiyalar
#   migrate.sh baseline <scope> <son_versiya>     İZLƏNMƏMİŞ (əl ilə yaradılmış) sxemi "bu versiyaya qədər tətbiq olunub" kimi qeyd edir
#                                                 (scope: platform | tenant slug). Yalnız mövcud sxem ƏL İLƏ yoxlandıqdan sonra!
#
# Əlaqə: DATABASE_URL (psql URI/keyword). İzləmə: platform.schema_migrations (scope, version, checksum) və tenant-da <schema>.schema_migrations.
# Qaydalar: (1) fayl dəyişdirilməz: yadda saxlanmış SHA-256 fərqlənirsə dayanır; (2) hər fayl öz tranzaksiyasında və izləmə sətri ilə
# BİRLİKDƏ tətbiq olunur, ona görə yarımçıq miqrasiya qalmır və iki icraçı yarışsa uduzan PK xətası ilə geri qaytarılır;
# (3) yalnız irəli: geri qaytarma yoxdur, "expand/contract" üsulu ilə (docs/09-deployment.md §6) yazılır.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DB="${DATABASE_URL:?DATABASE_URL təyin olunmalıdır}"
export PGOPTIONS="${PGOPTIONS:-} -c client_min_messages=warning"
PSQL=(psql "$DB" -X -q -v ON_ERROR_STOP=1)
SCHEMA_RE='^t_[a-z0-9_]{1,40}$'

q() { "${PSQL[@]}" -At -c "$1"; }
sum() { sha256sum "$1" | cut -d' ' -f1; }

ensure_platform_tracking() {
  "${PSQL[@]}" -c "CREATE SCHEMA IF NOT EXISTS platform" -c "CREATE TABLE IF NOT EXISTS platform.schema_migrations (
    scope text NOT NULL, version text NOT NULL, checksum text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY (scope, version))"
}

# apply <scope> <schema> <file>   (scope: "platform" | tenant slug; schema: "platform" | t_xxx)
apply_file() {
  local scope="$1" schema="$2" file="$3" version checksum recorded
  version="$(basename "$file" .sql)"; checksum="$(sum "$file")"
  recorded="$(q "SELECT checksum FROM platform.schema_migrations WHERE scope='$scope' AND version='$version'")"
  if [[ -n "$recorded" ]]; then
    [[ "$recorded" == "$checksum" ]] || { echo "XƏTA: $scope/$version dəyişdirilib (yaddaş $recorded ≠ fayl $checksum). Miqrasiya fayllarını dəyişməyin, yenisini əlavə edin." >&2; exit 3; }
    return 0
  fi
  echo "  tətbiq: $scope/$version"
  # Platform: search_path=public (uzantılar, məs. pg_trgm, public sxemində qurulmalıdır; tenant miqrasiyaları onları public-dən oxuyur)
  local path="$schema,public"; [[ "$schema" == "platform" ]] && path="public"
  PGOPTIONS="-c search_path=$path -c client_min_messages=warning" "${PSQL[@]}" --single-transaction -f "$file" \
    -c "INSERT INTO platform.schema_migrations(scope, version, checksum) VALUES ('$scope', '$version', '$checksum')"
}

platform_up() {
  ensure_platform_tracking
  # Köhnə (izlənməyən) quraşdırma: platform.tenants var, qeyd yoxdur → səssizcə təkrar tətbiq etmək fəlakətdir
  if [[ -n "$(q "SELECT to_regclass('platform.tenants')")" && "$(q "SELECT count(*) FROM platform.schema_migrations WHERE scope='platform'")" == "0" ]]; then
    echo "XƏTA: platform sxemi mövcuddur, amma izlənmir. Əvvəlcə: migrate.sh baseline platform P001" >&2; exit 4
  fi
  for f in "$HERE"/migrations/platform/P*.sql; do apply_file platform platform "$f"; done
}

tenant_up() { # slug schema
  local slug="$1" schema="$2"
  [[ "$schema" =~ $SCHEMA_RE ]] || { echo "XƏTA: yanlış sxem adı '$schema'" >&2; exit 1; }
  if [[ -n "$(q "SELECT 1 FROM information_schema.schemata WHERE schema_name='$schema'")" && "$(q "SELECT count(*) FROM platform.schema_migrations WHERE scope='$slug'")" == "0" ]]; then
    if [[ -n "$(q "SELECT to_regclass('$schema.users')")" ]]; then
      echo "XƏTA: $schema mövcuddur və cədvəlləri var, amma izlənmir. Əvvəlcə: migrate.sh baseline $slug T0xx" >&2; exit 4
    fi
  fi
  "${PSQL[@]}" -c "CREATE SCHEMA IF NOT EXISTS $schema"
  for f in "$HERE"/migrations/tenant/T*.sql; do apply_file "$slug" "$schema" "$f"; done
}

case "${1:-}" in
  up)
    platform_up
    while IFS='|' read -r slug schema; do
      [[ -n "$slug" ]] || continue
      echo "tenant $slug ($schema)"; tenant_up "$slug" "$schema"
    done < <(q "SELECT slug||'|'||schema_name FROM platform.tenants WHERE deleted_at IS NULL ORDER BY slug")
    echo "OK"
    ;;
  create-tenant)
    slug="${2:?slug}"; name="${3:-$slug}"; plan="${4:-starter}"
    [[ "$slug" =~ ^[a-z0-9]{1,30}$ ]] || { echo "XƏTA: slug yalnız kiçik hərf/rəqəm" >&2; exit 1; }
    platform_up
    "${PSQL[@]}" -c "INSERT INTO platform.tenants(slug, name, schema_name, plan_code) VALUES ('$slug', \$\$$name\$\$, 't_$slug', '$plan') ON CONFLICT (slug) DO NOTHING"
    tenant_up "$slug" "t_$slug"
    echo "OK: t_$slug"
    ;;
  status)
    ensure_platform_tracking
    all_t=$(ls "$HERE"/migrations/tenant/T*.sql | wc -l)
    echo "platform: $(q "SELECT count(*) FROM platform.schema_migrations WHERE scope='platform'")/$(ls "$HERE"/migrations/platform/P*.sql | wc -l)"
    q "SELECT slug||'|'||schema_name FROM platform.tenants WHERE deleted_at IS NULL ORDER BY slug" | while IFS='|' read -r slug schema; do
      echo "$slug: $(q "SELECT count(*) FROM platform.schema_migrations WHERE scope='$slug'")/$all_t"
    done
    ;;
  baseline)
    scope="${2:?scope}"; upto="${3:?son versiya, məs. T012}"
    ensure_platform_tracking
    dir="tenant"; [[ "$scope" == "platform" ]] && dir="platform"
    for f in "$HERE"/migrations/$dir/*.sql; do
      v="$(basename "$f" .sql)"
      [[ "${v%%_*}" > "$upto" ]] && continue   # "T012_xxx" → "T012" (tam ad ilə müqayisə "T012_x" > "T012" verir)
      "${PSQL[@]}" -c "INSERT INTO platform.schema_migrations(scope, version, checksum) VALUES ('$scope', '$v', '$(sum "$f")') ON CONFLICT DO NOTHING"
      echo "  qeyd: $scope/$v"
    done
    ;;
  *) sed -n 2,13p "$0" >&2; exit 2 ;;
esac
