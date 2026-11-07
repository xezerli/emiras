#!/usr/bin/env bash
# compose üçün .env yaradır (bir dəfə; mövcud faylı ƏZMİR). Sirlər yalnız bu fayldadır (chmod 600), repoya düşmür (.gitignore).
set -euo pipefail
cd "$(dirname "$0")"
[[ -e .env ]] && { echo ".env artıq var: silmədən əzilmir." >&2; exit 1; }
umask 077
rand() { head -c "$1" /dev/urandom | base64 | tr -d '\n=' | tr '+/' '-_'; }
key="$(mktemp)"; trap 'rm -f "$key" "$key.pub"' EXIT
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$key" 2>/dev/null
openssl rsa -in "$key" -pubout -out "$key.pub" 2>/dev/null
pem() { awk 'BEGIN{ORS="\\n"} {print}' "$1"; }    # çoxsətirli PEM → compose-un "..." dəyərində \n
cat > .env <<ENV
POSTGRES_PASSWORD=$(rand 24)
RABBITMQ_PASSWORD=$(rand 24)
GRAFANA_PASSWORD=$(rand 18)
PII_ENCRYPTION_KEY=$(head -c 32 /dev/urandom | base64)
PII_HASH_KEY=$(head -c 32 /dev/urandom | base64)
JWT_SIGNING_KEY_PEM="$(pem "$key")"
JWT_PUBLIC_KEY_PEM="$(pem "$key.pub")"
# Local Server: tək klinikanın slug-ı (docker compose run --rm migrate create-tenant <slug>)
DENTACORE_TENANT=
HTTP_PORT=8080
ENV
echo ".env yaradıldı. PII açarlarını ITIRMƏYİN: şifrələnmiş məlumat onsuz oxunmur (docs/09-deployment.md §8 açar idarəsi)."
