# DentaCore: stomatoloji klinika ERP

Multi-tenant (SaaS və lokal server) stomatoloji klinika sistemi: pasiyentlər, qəbul təqvimi, klinik qeydlər, faktura/kassa, audit, offline sinxronizasiya serveri. **Pilot mərhələsi**: tam vəziyyət və boşluqlar üçün [docs/STATUS.md](docs/STATUS.md).

| Qat | Texnologiya |
|---|---|
| Backend | .NET 9, modullu quruluş, 6 host (Gateway/YARP, Identity, ClinicCore, Billing, Notify/SignalR, Workers) |
| Məlumat | PostgreSQL 16 (schema-per-tenant), RabbitMQ (transactional outbox), Redis (keş, könüllü), Elasticsearch (axtarış, könüllü) |
| Veb | Next.js 16, React 19, öz Design System-i, BFF auth |
| Kontrakt | OpenAPI 3.1 (`api/openapi.yaml`) → tipli klient |
| Deploy | Docker, Helm, GitHub Actions, OpenTelemetry/Prometheus |

## Sürətli başlanğıc

```bash
infra/dev/services.sh                                  # PostgreSQL + RabbitMQ (sandbox; Docker olanda infra/docker/compose.yaml)
SEED_PASSWORD='Passw0rd!e2e' infra/dev/run-stack.sh start   # backend + gateway :5100 + "demo" tenant
cd frontend && pnpm install
pnpm --filter @dentacore/web e2e:build
API_ORIGIN=http://127.0.0.1:5100 DEV_TENANT=demo pnpm --filter @dentacore/web exec next start -p 3100
# http://127.0.0.1:3100  →  admin@demo.test / Passw0rd!e2e
```

Ətraflı: [developer-guide](docs/guides/developer-guide.md).

## Sənədlər

[docs/README.md](docs/README.md) bütün mərhələ sənədlərini və bələdçiləri sıralayır. Əsas giriş nöqtələri:

- İstifadəçi üçün: [user-guide](docs/guides/user-guide.md)
- İnteqrator üçün: [api-guide](docs/guides/api-guide.md), [OpenAPI](api/openapi.yaml)
- Developer üçün: [developer-guide](docs/guides/developer-guide.md), [ADR-lər](docs/adr/README.md)
- Təhlükəsizlik/məxfilik: [security-and-privacy](docs/guides/security-and-privacy.md)
- Deploy: [09-deployment](docs/09-deployment.md)

Töhfə: [CONTRIBUTING.md](CONTRIBUTING.md) · Zəiflik: [SECURITY.md](SECURITY.md) · Dəyişikliklər: [CHANGELOG.md](CHANGELOG.md)

Lisenziya faylı repoda yoxdur; istifadə şərtləri layihə sahibi tərəfindən təyin olunmalıdır.
