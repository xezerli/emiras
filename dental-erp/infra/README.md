# Infrastruktur
- `docker/`: Dockerfile-lar və `compose.yaml` (local server və developer mühiti)
- `k8s/base` + `overlays/{dev,staging,prod}`: Kustomize
- `helm/dentacore`: Helm chart
- `terraform/`: cloud infra
- `observability/`: Prometheus, Grafana, OpenTelemetry konfiqurasiyaları
- `backup/`: PG PITR, S3 replikasiyası, restore drill skriptləri
Hamısı Mərhələ 9-da (Deployment) doldurulur.
