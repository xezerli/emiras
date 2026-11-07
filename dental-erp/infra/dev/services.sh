#!/usr/bin/env bash
# Docker daemon olmayan mühit üçün: yerli PostgreSQL, RabbitMQ, Redis-i (yenidən) qaldırır. İdempotentdir.
# (Docker olan mühitdə infra/docker/compose.yaml istifadə edin.)
set -euo pipefail
PGDATA="${PGDATA:-/tmp/pgdata54329}"
if ! psql "postgresql:///postgres?host=/tmp&port=54329&user=postgres" -qtc "select 1" >/dev/null 2>&1; then
  rm -f "$PGDATA/postmaster.pid"
  su postgres -c "/usr/lib/postgresql/16/bin/pg_ctl -D $PGDATA -o '-p 54329 -k /tmp -c listen_addresses=' -l $PGDATA/log -w start" >/dev/null
fi
if ! rabbitmqctl -n rabbit@localhost status >/dev/null 2>&1; then
  su rabbitmq -s /bin/bash -c "HOME=/var/lib/rabbitmq RABBITMQ_NODENAME=rabbit@localhost RABBITMQ_NODE_IP_ADDRESS=127.0.0.1 /usr/sbin/rabbitmq-server -detached" >/dev/null 2>&1 || true
  for _ in $(seq 1 30); do rabbitmqctl -n rabbit@localhost await_startup >/dev/null 2>&1 && break; sleep 2; done
fi
redis-cli -p 6390 ping >/dev/null 2>&1 || redis-server --port 6390 --save "" --appendonly no --daemonize yes >/dev/null
echo "postgres:54329 rabbitmq:5672 redis:6390 hazırdır"
