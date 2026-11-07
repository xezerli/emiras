# syntax=docker/dockerfile:1.7
# docker build -f infra/docker/backup.Dockerfile dental-erp   → backup.sh / restore-drill.sh (pg_dump, age, python3, aws cli)
FROM postgres:16
# apt paketləri bilərəkdən sancılmır: baza image digest ilə sancılır və təhlükəsizlik yeniləmələri istənilir
# hadolint ignore=DL3008
RUN apt-get update \
 && apt-get install -y --no-install-recommends age python3-minimal awscli ca-certificates \
 && rm -rf /var/lib/apt/lists/*
COPY infra/backup/backup.sh infra/backup/restore-drill.sh /opt/backup/
USER 999
ENTRYPOINT []
CMD ["/opt/backup/backup.sh"]
