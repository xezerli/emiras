# Mərhələ 9: Deployment (Dental Clinic ERP "DentaCore")

> Status: ✅ Artefaktlar yazılıb və yoxlanılıb; nəyin real icra olunduğu, nəyin olunmadığı §3-dədir. Bu mühitdə Docker daemon yoxdur, ona görə
> **heç bir image qurulmayıb** (`release.yml` işləməyib). `ci.yml` və `codeql.yml` isə PR #1-də **real GitHub runner-lərdə işləyib və yaşıldır** (§3).
> Növbəti: Mərhələ 10 (Documentation).

## 1. Nə çatdırılır

```
                    ┌────────────── Cloud (K8s) ──────────────┐        ┌──── Local Server (tək klinika) ────┐
internet ─TLS─▶ Ingress (*.dentacore.app)                      │        │ nginx :8080 (compose.yaml)         │
                  ├─ /v1, /hubs ─▶ gateway ─▶ identity-api     │        │   ├─ /v1, /hubs ─▶ gateway ─▶ …    │
                  │                         ├─ clinic-core-api │        │   └─ /  ─▶ web (Next.js, BFF)      │
                  │                         ├─ billing-api     │        │ postgres, rabbitmq, redis, ES      │
                  │                         └─ notify-api      │        │ Tenancy__FixedTenant=<klinika>     │
                  └─ /  ─▶ web (Next.js + BFF)                 │        └────────────────────────────────────┘
                  workers (outbox publisher, consumers, indexer, maintenance)  ·  migrate (hook)  ·  backup/restore-drill (CronJob)
```

Xarici asılılıqlar (PostgreSQL 16, RabbitMQ 3.13, Redis 7, Elasticsearch 8.11) cloud-da **chart-a daxil deyil**: idarə olunan xidmət və ya operator (CloudNativePG, RabbitMQ Cluster Operator) istifadə edilir. Compose bunları konteyner kimi qaldırır (sınaq/Local Server).

## 2. Qərarlar

| Qərar | Səbəb |
|---|---|
| **Helm** əsas, Kustomize/Terraform **yox** | İki paralel manifest sistemi sinxrondan çıxır. Cloud provayderi və region müştəriyə/şirkətə xasdır: yoxlana bilməyən Terraform yazmaq əvəzinə boş qovluq silindi. |
| Bir `backend.Dockerfile`, `HOST` arqumenti | Altı host eyni build qaydalarına tabedir; kopyalanmış altı Dockerfile fərqlənərdi. |
| Metrikalar **itələnir** (OTLP), `/metrics` endpoint-i **yoxdur** | Scrape endpoint-i əlavə şəbəkə səthidir. Collector PHI-yə yol aça biləcək atributları silməli olur (`url.query`, `db.statement` …; konfiqurasiya validasiya olunub, silmə davranışı ayrıca sınanmayıb). Telemetriya `OTEL_EXPORTER_OTLP_ENDPOINT` təyin olunmasa tam söndürülüdür. |
| Liveness (`/health`) ≠ readiness (`/health/ready`) | DB düşəndə pod-u yenidən başlatmaq kömək etmir (restart fırtınası); trafik isə kəsilməlidir. Readiness cavabı təfərrüat vermir. |
| Chart **Secret yaratmır** | Sirlər Git-də/Helm tarixçəsində qalmasın; `existingSecret` (External Secrets/Vault/Sealed Secrets). Test bunu yoxlayır. |
| İrəli-yönlü miqrasiya, hook-da, **yalnız-additiv** qayda | Bax §6. |
| Logical backup (age ilə şifrəli) **+** PITR ayrıca | Logical backup portativdir (Local Server də) və tək klinikanı bərpa edir; saatlıq RPO üçün WAL arxivi (pgBackRest/CloudNativePG) lazımdır: **konfiqurasiya edilməyib** (§10). |

## 3. Yoxlama statusu (dürüst cədvəl)

| Artefakt | Yoxlama | Nəticə |
|---|---|---|
| 6 host `dotnet publish -c Release` (Dockerfile ilə eyni əmr, `TreatWarningsAsErrors`) | İcra olundu | ✅ hamısı; `Billing.Api` publish çıxışı mənbə kodu olmadan işə salındı: `/health/ready` 200, JSON log |
| Next.js `standalone` çıxışı | İcra olundu | ✅ `node apps/web/server.js` açılır, `/login` və statik CSS 200, təhlükəsizlik başlıqları var |
| `docker build` (4 Dockerfile) | **İcra OLUNMAYIB** (daemon yox) | ❌ hadolint təmiz (`--failure-threshold warning`), amma image qurulmayıb |
| `compose.yaml` | `docker compose config` (interpolyasiya + PEM çoxsətirli dəyərlər) | ✅ sintaksis; **konteynerlər qaldırılmayıb** |
| Helm chart | `helm lint`, `helm template`, **kubeconform --strict (K8s 1.30), 36/36 etibarlı**, `test-chart.py` (10 yoxlama, mutasiya ilə sınanıb) | ✅ klasterə qoyulmayıb |
| Prometheus qaydaları | `promtool check rules` + `promtool test rules` (6 ssenari; qaydanı pozunca test düşür) | ✅ |
| OTel Collector konfiqurasiyası | `otelcol-contrib validate` + **real Workers/Billing host-ları ilə OTLP axını**: Prometheus-da `dentacore_outbox_*`, `http_server_request_duration_seconds_*{service_name}` görünür | ✅ qaydalardakı metrik adları real çıxışdan təsdiqlənib (sayğacların sıfır nöqtəsi başlanğıcdan ixrac olunur: bu yoxlamada tapılan boşluq düzəldildi) |
| `db/migrate.sh` | `db/test-migrate.sh` (real PG, 6 ssenari) | ✅ |
| Backup / restore | `infra/backup/test-backup.sh` (real PG, 7 ssenari: tam və tək-tenant bərpa, yanlış açar, zədələnmə, saxta say) | ✅ |
| `ci.yml` + `codeql.yml` | **PR #1-də real runner-lərdə icra olundu**, son commit-də 9/9 yaşıl: API kontraktı, Backend (real PG/RabbitMQ/Redis/**Elasticsearch 8.11**: Search 26/26, Notify 14/14 daxil), Frontend, E2E, Təhlükəsizlik, Miqrasiya/ehtiyat nüsxə/Helm/Prometheus/Dockerfile lint/compose, CodeQL (C# + JS/TS) | ✅ |
| `release.yml` | `actionlint` (+ shellcheck) | ⚠ **heç vaxt icra olunmayıb** (yalnız `main`/teqdə işləyir); action-lar teq ilə (SHA ilə deyil) |
| Prometheus + Grafana + Collector compose profili | Yalnız konfiqurasiya sintaksisi | ⚠ qaldırılmayıb; dashboard JSON-u Grafana-da açılmayıb |
| Trivy / CodeQL / cosign | CI ilk işə düşəndə `aquasecurity/trivy-action@0.28.0` teqi tapılmadı (job başlamadı): action əvəzinə birbaşa `aquasec/trivy:0.57.1` konteyneri çağırılır; konteyner teqi özü hələ təsdiqlənməyib | ⚠ |

**CI-nin ilk real işə salınması 4 problem tapdı** (hamısı düzəldildi, lokal yoxlamalar bunları görə bilməzdi):
1. `aquasecurity/trivy-action@0.28.0` teqi mövcud deyildi: Trivy birbaşa `aquasec/trivy` konteyneri ilə çağırılır.
2. Runner-də `shellcheck` var, lokal `actionlint`-də yox idi (SC2015, SC2155): düzəldildi və lokal `shellcheck` quraşdırıldı.
3. RabbitMQ `guest` istifadəçisi konteynerə port xəritəsi ilə qoşulanda rədd olunur (24 test düşdü; `loopback_users` ERL arqumenti kömək etmədi): `RABBITMQ_DEFAULT_USER/PASS` ilə adi istifadəçi.
4. `DentaCore.TestSupport` `xunit` istinadı səbəbindən test layihəsi sayılırdı və boş testhost `dotnet test`-i exit 1 ilə bitirirdi (lokalda mən nəticəni `grep` ilə süzdüyümdən görünməmişdi): `IsTestProject=false`.

Həmçinin: ardıcıl push-lar `cancel-in-progress` ilə köhnə run-ları ləğv edir; yaşıl nəticə yalnız ən son commit üçün sayılır.

### Bu mərhələdə əlavə olunan avtomatik testlər

| Test | Say | Nə sübut edir |
|---|---|---|
| `TenantSlugResolverTests` | 15 | Cloud: host şəkilçisi; çılpaq domen və `evil.example.dentacore.app.evil` rədd; `X-Tenant` yalnız icazə ilə və tək dəyərlə; `FixedTenant` host/başlıqla əzilmir |
| `HostConfigurationTests` | 2 | Log səviyyəsi (PHI), commit olunan konfiqurasiyada sirr yoxdur |
| `HealthTests` (ClinicCore) | 2 | DB əlçatmaz → liveness 200 / readiness 503 təfərrüatsız; real host hər ikisini anonim açır |
| `OutboxMetricsTests` (Workers) | 2 | Göndərilən/uğursuz sayğaclar, gecikmə nümunəsi; sampler gözləyən ≠ ölü ayrımı, ən köhnə yaş |
| `EndpointSecurityTests` (4 host) | yenilənib | `GET /health/ready` anonim marşrut kimi siyahıya əlavə edildi (qəsdən) |
| `db/test-migrate.sh` | 6 ssenari | Bax §6 |
| `infra/backup/test-backup.sh` | 7 ssenari | Bax §3 |
| `infra/helm/test-chart.py` | 10 yoxlama | Hardening, sirr paylanması (imzalama açarı yalnız identity-da), probe-lar, NetworkPolicy, ingress, hook, CronJob adları |
| `promtool test rules` | 6 qrup | Hər xəbərdarlıq vaxt/etiket/annotasiya ilə |

Mərhələ 9 sonunda tam yoxlama: backend **674** (646 keçir, 28 Elasticsearch tələb edir və atlanıb, **0 uğursuz**), e2e **13/13** (yeni `migrate.sh` provisioning-i, OTel paketləri, `/health/ready` ilə).

## 4. Konfiqurasiya

| Açar (env) | Harada | Qeyd |
|---|---|---|
| `ConnectionStrings__Default` | bütün host-lar | Sir. PostgreSQL |
| `ConnectionStrings__Redis` | clinic-core, workers | Könüllü. Yoxdursa dashboard keşi prosess-daxilidir |
| `RabbitMq__Uri` | workers, notify | Sir. `QueuePrefix` izolyasiya üçün |
| `Elasticsearch__Uri` | clinic-core, workers | Könüllü. Yoxdursa axtarış 503 → UI SQL axtarışına keçir |
| `Security__PiiEncryptionKey`, `Security__PiiHashKey` | PII yazan/oxuyan host-lar | **Sir. İtirilməməlidir** (§8) |
| `Jwt__SigningKeyPem` | **yalnız identity-api** | Sir. Chart testi bunu yoxlayır |
| `Jwt__PublicKeyPem` | token yoxlayan host-lar | Açıq açar |
| `Tenancy__BaseDomain` / `Tenancy__FixedTenant` | hamısı | Cloud: domen. Local Server: sabit slug (host/`X-Tenant` nəzərə alınmır; 15 vahid test) |
| `Tenancy__AllowTenantHeader` | **yalnız dev/test** | İstehsalda `false` (chart və compose sabitləyir) |
| `Logging__Format=json` | hamısı | Bir sətir = bir JSON |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | bütün .NET host-lar (Gateway istisna, §10) | Boşdursa telemetriya söndürülüdür |

Commit olunan `appsettings*.json`-da sirr olmaması və `Microsoft.AspNetCore` log səviyyəsinin ≥ Warning olması (sorğu sətri = PHI loglara düşməsin) **Architecture testi ilə** yoxlanılır (log səviyyəsi qaydası mutasiya ilə sınanıb: `Information`-a çevirəndə test düşdü; sirr qaydası mutasiya ilə sınanmayıb).

## 5. Buraxılış və deploy

1. PR → `ci.yml`: kontrakt, backend (real PG/RabbitMQ/Redis/ES, **atlanma = uğursuzluq**, əhatə ≥ 90%), frontend, e2e, miqrasiya/backup/Helm/Prometheus/Dockerfile/compose/workflow lint, təhlükəsizlik.
2. `main`-ə birləşmə → `release.yml`: 9 image qurulur → Trivy (düzəldilə bilən HIGH/CRITICAL varsa dayanır) → GHCR (`sha-<12 simvol>`, SBOM, provenance) → cosign keyless imza.
3. Deploy (CI-dən kənar, müstəqil qərar): `helm upgrade --install dentacore infra/helm/dentacore -f values-prod.yaml --set global.imageTag=sha-<commit> --atomic --timeout 10m`.
   `pre-upgrade` hook `migrate` Job-u əvvəl işləyir; uğursuz olsa buraxılış dayanır, köhnə pod-lar işləməyə davam edir. `--atomic` readiness keçməsə geri qaytarır. Rolling update `maxUnavailable: 0`.
4. Geri qaytarma: `helm rollback` **kodu** qaytarır, **sxemi yox** (§6).

Image tag-ları dəyişməzdir (`sha-…`); `:latest` chart testi ilə qadağandır. Əsas image-lərin digest ilə sancılması Dependabot/Renovate PR-ları ilə edilir (`.github/dependabot.yml`).

## 6. Verilənlər bazası miqrasiyaları

`db/migrate.sh up`: platform + **bütün aktiv tenant sxemlərinə** gözləyən `P*`/`T*` faylları tətbiq edir.

- **Dəyişməzlik:** tətbiq olunmuş faylın SHA-256-sı saxlanır; fayl dəyişsə icra dayanır (exit 3).
- **Atomiklik:** hər fayl öz tranzaksiyasında izləmə sətri ilə birlikdə; yarımçıq miqrasiya qalmır; iki icraçı yarışsa uduzan PK xətası ilə geri qaytarılır.
- **İzlənməyən sxem** (DevSeed/əl ilə yaranıb): səssiz təkrar tətbiq YOX, aydın xəta (exit 4) və `baseline` əmri.
- **Yalnız irəli.** Geri qaytarma skripti yoxdur. Çünki hook köhnə pod-lar hələ işləyərkən işləyir, hər miqrasiya **əvvəlki versiya ilə uyğun (additiv)** olmalıdır:
  1. *Expand* (buraxılış N): yeni sütun/cədvəl, nullable və ya default ilə; kod hələ yazmır/oxumur.
  2. Kod yeni sütundan istifadə edir (N+1); köhnə sütun hələ qalır.
  3. *Contract* (N+2): köhnə sütunu sil.
  Sütunun adını dəyişmək, `NOT NULL` default-suz əlavə, tip daraltmaq tək buraxılışda **qadağandır**.
- **Çox tenant:** hər miqrasiya N sxemdə işləyir; böyük cədvəl dəyişikliyi N dəfə kilid deməkdir. 10³ tenant üçün paralel icra/pəncərə planı **yoxdur** (§10).
- **Bu yoxlanmayıb:** uzun miqrasiya zamanı canlı trafikin davranışı.

## 7. Müşahidə

| Siqnal | Mənbə |
|---|---|
| HTTP gecikmə/xəta | ASP.NET Core instrumentasiyası → OTLP → Collector → Prometheus (`http_server_request_duration_seconds_*{service_name,http_route,…}`) |
| Outbox | `dentacore.outbox.{pending,dead,oldest_age}` (hər 15 s nümunələnir), `published`, `publish_failures`, `publish_delay` |
| Tranzaksiya | Npgsql metrikaları/izləri; **`pg_long_running_tx_*`** `postgres_exporter` + `postgres-exporter-queries.yaml` ilə (yazı tranzaksiyası: `backend_xid IS NOT NULL`). SQL real PostgreSQL-də açıq yazı tranzaksiyasını tutdu; `postgres_exporter` özü qaldırılmayıb |
| Broker | RabbitMQ prometheus plugini: DLQ dərinliyi |
| Cron | `kube_cronjob_status_last_successful_time` (kube-state-metrics): `dentacore-backup`, `dentacore-restore-drill` |
| Log | JSON; sorğu sətri yazılmır; tenant adı metrik etiketi deyil |

**Xəbərdarlıqlar** (`rules/dentacore.yml`, 15 qayda): `OutboxDeadLetters`, `OutboxBacklogAge(Critical)`, `OutboxPublisherStalled`, `OutboxPublishFailures`, `RabbitMqDeadLetterQueueNotEmpty`, `PostgresLongRunningWriteTransaction(Critical)`, `PostgresIdleInTransactionWriters`, `HighServerErrorRatio`, `HighLatencyP95`, `AuthFailureSpike`, `ServiceTelemetryMissing`, `BackupTooOld`, `RestoreDrillOverdue`.

**Niyə uzun yazı tranzaksiyası xəbərdarlığı var:** sync `pull` və Elasticsearch indexer `pg_snapshot_xmin` horizontundan oxuyur; açıq yazı tranzaksiyası horizontu saxlayır (klaster miqyasında!), hər ikisi geridə qalır və VACUUM təmizləyə bilmir (`docs/07` Dilim 7b).

## 8. Sirlər və açar idarəsi

- **PII açarları (`PiiEncryptionKey`, `PiiHashKey`).** Telefon/FİN/e-poçt AES-GCM ilə şifrəlidir, e-poçt və telefon axtarışı HMAC blind index ilədir. **`PiiHashKey` itərsə giriş (e-poçt blind index) və dublikat yoxlaması işləmir; `PiiEncryptionKey` itərsə məlumat bərpa olunmur.** Bu açarlar ehtiyat nüsxədən **ayrı** və ən azı iki yerdə (məs. Vault + offline sənədləşmiş zərf) saxlanmalıdır. **Açar rotasiyası həyata keçirilməyib** (tək açar, versiya sahəsi yoxdur): ciddi boşluq (§10).
- **JWT.** Yalnız Identity imzalama açarını görür. JWKS və açar rotasiyası **yoxdur** (`kid` sabitdir): açarı dəyişmək bütün sessiyaları ləğv edir və hər host-un public key-ini eyni anda yeniləməyi tələb edir.
- **Ehtiyat nüsxə açarları (age).** Backup Job-u yalnız **açıq** açarı (`age-recipients`) bilir: cluster ələ keçirilsə köhnə ehtiyat nüsxələr açılmır. **Gizli açar offline saxlanır.** Restore-drill Job-u deşifrə üçün gizli açar tələb edir: bunun üçün **ayrı drill açar cütü** yaradın, backup-u hər iki açıq açara (vergüllə) şifrələyin, drill gizli açarını yalnız `dentacore-restore-drill-key` Secret-ində saxlayın. Bu açar cluster-də olduğuna görə **drill açarı ilə şifrələnmiş nüsxələr cluster girişi olan şəxs üçün oxunaqlıdır**; maksimum təhlükəsizlik tələb olunursa drill-i etibarlı xarici mühitdə işlədin.
- `init-secrets.sh` (compose) `.env` yaradır (`chmod 600`, `.gitignore`-dadır, mövcud faylı əzmir).

## 9. Runbook-lar

**Ölü outbox mesajları (`OutboxDeadLetters`).** Səbəbi tapın: `SELECT id, routing_key, attempts, last_error FROM <t_slug>.outbox_messages WHERE processed_at IS NULL AND attempts >= 10;`. Səbəb (broker, şəkil uyğunsuzluğu) düzəldilibsə: `UPDATE <t_slug>.outbox_messages SET attempts = 0, next_attempt_at = now() WHERE processed_at IS NULL AND attempts >= 10;`. Mesajlar at-least-once göndərilir, consumer-lər inbox ilə dublikatı atır.

**DLQ-da mesaj (`RabbitMqDeadLetterQueueNotEmpty`).** `dental.<consumer>.dlq` növbəsini yoxlayın (poison və ya davamlı handler xətası). Kodu düzəldin, mesajları `dental.events` exchange-ə orijinal routing key ilə geri atın (shovel/`rabbitmqadmin publish`); inbox təkrarı zərərsizləşdirir.

**Uzun yazı tranzaksiyası.** `SELECT pid, now()-xact_start AS age, state, left(query,80) FROM pg_stat_activity WHERE backend_xid IS NOT NULL ORDER BY xact_start;` → mənbəyi (tətbiq xətası, əl ilə sessiya) bağlayın; zəruridirsə `pg_terminate_backend(pid)`. Sync/indexer özü yetişir, itki yoxdur.

**Tenant-ı bərpa etmək.** `restore-drill.sh` ilə yoxlanılmış set-dən: `age -d -i <gizli> tenant-<slug>.dump.age > t.dump`; hədəf bazada uzantılar mövcud olmalıdır (`meta.json.extensions`); `pg_restore -d <db> --no-owner -n t_<slug> --clean --if-exists t.dump` (**sınanmayıb**: drill boş bazaya bərpanı yoxlayır, canlı sxemin üzərinə yox). Bu, yalnız həmin klinikanın sxemini əvəz edir; tenant dump-ları bir-biri ilə eyni anda alınmır (tək bir snapshot yalnız `full.dump`-dadır).

**Tam fəlakət bərpası.** Yeni PG → `full.dump.age` deşifrə → `pg_restore` → `migrate.sh status` (hamısı tətbiq olunmuş olmalıdır) → servisləri qaldır → Workers outbox-u boşaldır. Yeni açarlarla YOX: **eyni PII açarları** lazımdır.

**Geri qaytarma.** `helm rollback dentacore <rev>`; sxem additiv olduğu üçün köhnə kod yeni sxemlə işləyir (§6 qaydası pozulubsa işləməyə bilər: bu halda irəli düzəliş).

**Sirr sızması.** JWT imzalama açarı: yeni açar cütü → Identity + bütün public key-lər → bütün sessiyalar ləğv olur (planlı pəncərə). PII açarı sızması: rotasiya aləti olmadığından **hadisə səviyyəsində** məsələdir (§10).

## 10. Məlum boşluqlar (sıra ilə vacibliyə görə)

1. **Heç bir image qurulmayıb (`release.yml` işləməyib), Helm klasterə qoyulmayıb.** CI-nin ilk işə salınması 4 problem tapdı (§3); image build və klaster deploy-u da yenilərini tapacaq.
2. **PII açar rotasiyası yoxdur** və JWT üçün JWKS/rotasiya yoxdur.
3. **PITR / WAL arxivi konfiqurasiya edilməyib.** Hazırkı RPO = ən son logical backup (default 24 saat). Saatlıq/dəqiqəlik RPO üçün pgBackRest və ya CloudNativePG `barmanObjectStore` lazımdır.
4. **RTO ölçülməyib.** Bərpa sınağı kiçik bazada saniyələrlə bitdi; istehsal ölçüsündə vaxt məlum deyil.
5. **Gateway telemetriyası yoxdur** (`BuildingBlocks`-a istinad etmir); YARP metrikaları ixrac olunmur. Ingress metrikaları və `/health` ilə örtülür.
6. **Çox tenant miqrasiya miqyası** (§6) və tenant yaratma avtomatlaşdırması (provisioning API/Job) yoxdur: `migrate.sh create-tenant` əl ilə çağırılır.
7. **TLS/cert-manager, WAF, DDoS qoruması, DNS** chart-a daxil deyil (ingress annotasiyaları və wildcard sertifikat tələbi qeyd olunub).
8. **Local Server ↔ Cloud sinxronizasiyası:** backend sync API-ləri var, amma iki mühit arasında replikasiya/yenilənmə kanalı (updater) və lisenziya yoxdur.
9. **Egress NetworkPolicy** yoxdur (yalnız ingress). CIDR-lər mühitə xasdır.
10. **Çoxregion / aktiv-aktiv** yoxdur; PostgreSQL tək yazı node-udur.
11. Image-lər üçün digest sancma, action-lar üçün SHA sancma Dependabot-a həvalədir, əl ilə edilməyib.
12. Grafana dashboard-u və compose müşahidə profili real açılmayıb.
