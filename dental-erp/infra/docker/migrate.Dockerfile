# syntax=docker/dockerfile:1.7
# docker build -f infra/docker/migrate.Dockerfile dental-erp   → db/migrate.sh icraçısı (psql + bash). DATABASE_URL mühit dəyişəni ilə.
FROM postgres:16
COPY db/migrate.sh /db/migrate.sh
COPY db/migrations /db/migrations
# postgres image-ində UID 999 `postgres`; yazılan heç nə yoxdur (yalnız /tmp)
USER 999
ENTRYPOINT ["/db/migrate.sh"]
CMD ["up"]
