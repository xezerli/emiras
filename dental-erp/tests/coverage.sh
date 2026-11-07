#!/usr/bin/env bash
# Backend əhatə hesabatı (bütün testlər, real PostgreSQL/RabbitMQ/Redis; Elasticsearch yoxdursa Search inteqrasiya testləri atlanır).
# İstifadə: tests/coverage.sh [--min-lines 90]     Tələb: dotnet tool install -g dotnet-coverage
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="${COVERAGE_DIR:-/tmp/dentacore-coverage}"
mkdir -p "$OUT"
export PATH="$PATH:$HOME/.dotnet/tools"
export DENTACORE_TEST_PG="${DENTACORE_TEST_PG:-Host=/tmp;Port=54329;Username=postgres;Database=postgres}"
export DENTACORE_TEST_AMQP="${DENTACORE_TEST_AMQP:-amqp://guest:guest@localhost:5672/}"
export DENTACORE_TEST_REDIS="${DENTACORE_TEST_REDIS:-127.0.0.1:6390}"
cd "$ROOT/backend"
dotnet build DentaCore.sln -v q --nologo
dotnet-coverage collect -s "$ROOT/tests/coverage/settings.xml" -f cobertura -o "$OUT/backend.cobertura.xml" "dotnet test DentaCore.sln --no-build -v q"
python3 "$ROOT/tests/coverage/summarize.py" "$OUT/backend.cobertura.xml" "$@"
