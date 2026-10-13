#!/usr/bin/env bash
# Köhnə giriş nöqtəsi (geriyə uyğunluq): yeni tenant yaradır. İzlənən versiya: migrate.sh create-tenant.
# İstifadə: ./provision.sh <t_slug> [DATABASE_URL]
set -euo pipefail
SCHEMA="${1:?tenant schema, məs. t_demo}"; export DATABASE_URL="${2:-${DATABASE_URL:-}}"
[[ "$SCHEMA" =~ ^t_[a-z0-9]{1,30}$ ]] || { echo "yanlış schema adı" >&2; exit 1; }
exec "$(cd "$(dirname "$0")" && pwd)/migrate.sh" create-tenant "${SCHEMA#t_}"
