#!/usr/bin/env bash
# Yerli / e2e üçün bütün backend servislərini qaldırır: Identity, ClinicCore, Billing, Notify, Workers, Gateway.
# İstifadə:  infra/dev/run-stack.sh start | stop | env
# Tələb: PostgreSQL və RabbitMQ əvvəlcədən işləyir (Redis və Elasticsearch könüllüdür).
#   DENTACORE_PG    PostgreSQL superuser bağlantısı (db adı OLMADAN), default: Host=/tmp;Port=54329;Username=postgres
#   DENTACORE_AMQP  default: amqp://guest:guest@localhost:5672/
#   DENTACORE_REDIS (könüllü) məs. 127.0.0.1:6390     DENTACORE_ES (könüllü) məs. http://127.0.0.1:9200
#   STACK_DIR       log/pid/açar qovluğu, default /tmp/dentacore-stack     STACK_DB  default dentacore_e2e
#   GATEWAY_PORT    default 5100 (Next.js `API_ORIGIN` buna yönəlir)
# Sirlər (RSA, PII açarları) hər qovluq üçün bir dəfə yaradılır və yalnız STACK_DIR-də saxlanır; repoya düşmür.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="${STACK_DIR:-/tmp/dentacore-stack}"
DB="${STACK_DB:-dentacore_e2e}"
PG="${DENTACORE_PG:-Host=/tmp;Port=54329;Username=postgres}"
AMQP="${DENTACORE_AMQP:-amqp://guest:guest@localhost:5672/}"
GW="${GATEWAY_PORT:-5100}"
mkdir -p "$DIR"

secrets() {
  [[ -f "$DIR/jwt.key" ]] || { openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$DIR/jwt.key" 2>/dev/null; openssl rsa -in "$DIR/jwt.key" -pubout -out "$DIR/jwt.pub" 2>/dev/null; }
  [[ -f "$DIR/pii.enc" ]] || head -c 32 /dev/urandom | base64 > "$DIR/pii.enc"
  [[ -f "$DIR/pii.hash" ]] || head -c 32 /dev/urandom | base64 > "$DIR/pii.hash"
}

common_env() {
  export ConnectionStrings__Default="$PG;Database=$DB"
  export Security__PiiEncryptionKey="$(cat "$DIR/pii.enc")" Security__PiiHashKey="$(cat "$DIR/pii.hash")"
  export Tenancy__AllowTenantHeader=true RateLimiting__AuthPerMinute=10000
  export RabbitMq__Uri="$AMQP"
  [[ -n "${DENTACORE_REDIS:-}" ]] && export ConnectionStrings__Redis="$DENTACORE_REDIS"
  [[ -n "${DENTACORE_ES:-}" ]] && export Elasticsearch__Uri="$DENTACORE_ES"
  return 0
}

launch() { # name project port [extra env...]
  local name="$1" proj="$2" port="$3"; shift 3
  ( common_env
    export ASPNETCORE_URLS="http://127.0.0.1:$port" ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
    cd "$ROOT/backend/src/Hosts/$proj"
    # `env` (export yox): YARP klaster adlarındakı '-' bash identifikatorunda yolverilməzdir
    nohup env "$@" dotnet "bin/Debug/net9.0/$proj.dll" > "$DIR/$name.log" 2>&1 & echo $! > "$DIR/$name.pid" )
}

wait_health() { # name port
  for _ in $(seq 1 60); do curl -fs "http://127.0.0.1:$2/health" >/dev/null 2>&1 && { echo "  $1 :$2 ok"; return; }; sleep 1; done
  echo "  $1 BAŞLAMADI, bax: $DIR/$1.log" >&2; tail -20 "$DIR/$1.log" >&2; exit 1
}

case "${1:-}" in
  start)
    secrets
    psql "postgresql:///postgres?host=/tmp&port=54329&user=postgres" -tc "SELECT 1 FROM pg_database WHERE datname='$DB'" 2>/dev/null | grep -q 1 \
      || psql "postgresql:///postgres?host=/tmp&port=54329&user=postgres" -qc "CREATE DATABASE $DB"
    for proj in src/Hosts/DentaCore.Identity.Api src/Hosts/DentaCore.ClinicCore.Api src/Hosts/DentaCore.Billing.Api \
                src/Hosts/DentaCore.Notify.Api src/Hosts/DentaCore.Workers src/Hosts/DentaCore.Gateway tools/DentaCore.DevSeed; do
      ( cd "$ROOT/backend" && dotnet build -v q --nologo "$proj" 2>&1 | grep -E "error|Build FAILED" || true )
    done
    # Sxem versiyalı miqrasiya icraçısı ilə qurulur (db/migrate.sh), DevSeed yalnız başlanğıc məlumatı yazır
    ( export DATABASE_URL="postgresql:///$DB?host=/tmp&port=54329&user=postgres"; "$ROOT/db/migrate.sh" create-tenant "${SEED_SLUG:-demo}" >/dev/null )
    ( common_env; export DENTACORE_DB="$ConnectionStrings__Default"; cd "$ROOT/backend" && dotnet tools/DentaCore.DevSeed/bin/Debug/net9.0/DentaCore.DevSeed.dll "${SEED_SLUG:-demo}" ${SEED_PASSWORD:+"$SEED_PASSWORD"} | tee "$DIR/seed.txt" )
    PUB="Jwt__PublicKeyPem=$(cat "$DIR/jwt.pub")"
    launch identity DentaCore.Identity.Api 5101 "Jwt__SigningKeyPem=$(cat "$DIR/jwt.key")" "Jwt__PublicKeyPem=$(cat "$DIR/jwt.pub")"
    launch clinic DentaCore.ClinicCore.Api 5102 "$PUB"
    launch billing DentaCore.Billing.Api 5103 "$PUB"
    launch notify DentaCore.Notify.Api 5104 "$PUB"
    launch workers DentaCore.Workers 5105 "X=1"
    launch gateway DentaCore.Gateway "$GW" "$PUB" Gateway__ForwardTenantHeader=true Gateway__AuthRequestsPerMinute=100000 Gateway__RequestsPerMinute=100000 \
      ReverseProxy__Clusters__identity__Destinations__d1__Address=http://127.0.0.1:5101/ \
      ReverseProxy__Clusters__clinic-core__Destinations__d1__Address=http://127.0.0.1:5102/ \
      ReverseProxy__Clusters__billing__Destinations__d1__Address=http://127.0.0.1:5103/ \
      ReverseProxy__Clusters__notify__Destinations__d1__Address=http://127.0.0.1:5104/
    for p in "identity 5101" "clinic 5102" "billing 5103" "notify 5104" "workers 5105" "gateway $GW"; do wait_health $p; done
    echo "Hazırdır. Gateway: http://127.0.0.1:$GW   (giriş məlumatları: $DIR/seed.txt)"
    ;;
  stop)
    for f in "$DIR"/*.pid; do [[ -f "$f" ]] && kill "$(cat "$f")" 2>/dev/null || true; rm -f "$f"; done
    ;;
  env) secrets; echo "DENTACORE_DB=$PG;Database=$DB"; ;;
  *) echo "start | stop | env" >&2; exit 2 ;;
esac
