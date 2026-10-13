# Infrastruktur

| Qovluq | Nə |
|---|---|
| `docker/` | `backend.Dockerfile` (6 host üçün bir fayl), `frontend.Dockerfile`, `migrate.Dockerfile`, `backup.Dockerfile`, `compose.yaml` (Local Server), `nginx.conf`, `init-secrets.sh` |
| `helm/dentacore` | Cloud/K8s üçün əsas çatdırılma vasitəsi (Kustomize/Terraform YOXDUR: bax `docs/09-deployment.md` §2) |
| `observability/` | OTel Collector, Prometheus qaydaları (+ `promtool` testləri), Grafana dashboard və provisioning |
| `backup/` | `backup.sh`, `restore-drill.sh`, `test-backup.sh` |
| `dev/` | Docker olmayan sandbox üçün: `services.sh` (PG/RabbitMQ/Redis), `run-stack.sh` (6 host), `pull_image.py` (ES) |

Hamısının izahı, işə salma və yoxlama statusu: [`docs/09-deployment.md`](../docs/09-deployment.md).
