# Mərhələ 8: Testing (Dental Clinic ERP "DentaCore")

> Status: ✅ Tamamlandı. Növbəti: Mərhələ 9 (Deployment: CI/CD, Docker, K8s/Helm, monitorinq): **hazırdır**, bax `09-deployment.md`.
> Qeyd: bu sənədin rəqəmləri Mərhələ 8-in sonundakı vəziyyətdir. Mərhələ 9 sonra əlavə etdi: backend **674** test (646 keçir, 28 ES tələb edir), +15 `TenantSlugResolver`, +2 Architecture (host konfiqurasiyası), +2 Workers (outbox metrikaları), +2 ClinicCore (health), həmçinin shell/chart/promtool testləri (`09 §3`).
> Bütün rəqəmlər bu sessiyada real PostgreSQL 16, RabbitMQ 3.12, Redis ilə ölçülüb (Elasticsearch olmadan: bax §7).

## 1. Prinsiplər

1. **Mock yalnız sərhəddə.** Verilənlər bazası, broker, keş və HTTP real servislərlə sınanır. Tarixçə: mock-lu testlərin tutmadığı real səhvləri inteqrasiya və e2e tapdı (başlanğıc yarışı, `pg_snapshot_xmin` horizontu, `/auth/me` filialları, `ApiError` kodu).
2. **Testin dişi olmalıdır.** Kritik testlər üçün kodu qəsdən pozub testin düşdüyünü yoxlayırıq (mutasiya yoxlaması, §6). Düşməyən test sayılmır.
3. **Atlanmış ≠ keçmiş.** Mühit dəyişəni yoxdursa inteqrasiya testi **atlanır** (uğursuz sayılmır), amma hesabatda "Skipped" görünür. CI (Mərhələ 9) hamısını servis konteynerləri ilə təyin edib atlanmanı **uğursuzluq** sayacaq.
4. **Xəbərdarlıq = xəta.** Backend-də `TreatWarningsAsErrors` + CA analizatorları testlərə də şamildir.
5. **Təhlükəsizlik defoltla qapalıdır** (default-deny) və bu avtomatik yoxlanır (§4).

## 2. Test piramidası və inventar

| Səviyyə | Alət | Say | Nə sübut edir |
|---|---|---|---|
| Arxitektura | xUnit (csproj istinad qrafı) | 7 | Modullar yalnız `.Contracts` ilə danışır; host-lar yalnız modul `.Infrastructure`-a istinad edir; Domain heç nədən asılı deyil |
| Backend unit | xUnit | 389 | Domen qaydaları, handler-lər, validatorlar, marşrutlaşdırma, tarix/pul hesabı |
| Backend inteqrasiya | xUnit + WebApplicationFactory + **real** PG/AMQP/Redis(/ES) | 257 | HTTP → handler → DB → outbox → broker → consumer → SignalR/indeks zənciri, RBAC, tenant izolyasiyası, idempotentlik, paralel yarışlar |
| Frontend unit/komponent | Vitest + Testing Library + axe | 88 (api-client 13, ui 12, web 63) | Klient (refresh, ApiError), komponentlər (a11y), BFF (cookie, Origin), ekran məntiqi (təkrar ödəniş açarı, dublikat axını, maska) |
| **E2E** | Playwright (Chromium) + **real backend** (6 servis) | 13 | Brauzerdən verilənlər bazasına tam yol, cookie atributları, rola görə naviqasiya, ödəniş axını, **axe WCAG 2.2 AA (real kontrast, açıq+tünd)** |
| Yük/davamlılıq | `tests/load/load.mjs` | 7 ssenari | Gecikmə persentilləri, xəta nisbəti, yük altında outbox boşalması |
| Asılılıq təhlükəsizliyi | `pnpm audit`, `dotnet list package --vulnerable` | - | Məlum zəiflik yoxdur (bu sessiyada: 0) |

Cəmi: backend **653** test (7 + 389 + 257) (625 keçir, 28 Elasticsearch tələb edir və ES olmadan atlanır), frontend **88** unit + **13** e2e.

Backend inteqrasiya testlərinin bölgüsü: ClinicCore 62, Gateway 40, Billing 38, Sync 32, Search 26 (ES), Identity 17, Workers 17 (2 ES), Notify 14, Dashboard 11.

## 3. Necə işə salmaq

```bash
# Backend: hamısı (servislər: PostgreSQL, RabbitMQ, Redis; ES könüllü)
export DENTACORE_TEST_PG='Host=/tmp;Port=54329;Username=postgres;Database=postgres'
export DENTACORE_TEST_AMQP='amqp://guest:guest@localhost:5672/'
export DENTACORE_TEST_REDIS='127.0.0.1:6390'
export DENTACORE_TEST_ES='http://127.0.0.1:9200'      # varsa
dotnet test backend/DentaCore.sln

# Backend əhatəsi (təbəqə üzrə hesabat; --min-lines ilə hədd)
tests/coverage.sh --min-lines 90

# Frontend: unit + əhatə həddləri
cd frontend && pnpm test:coverage

# E2E (real stack)
infra/dev/run-stack.sh start
pnpm --filter @dentacore/web e2e:build
PLAYWRIGHT_CHROMIUM=/opt/pw-browsers/chromium pnpm --filter @dentacore/web e2e

# Yük testi (stack işləyərkən)
node tests/load/load.mjs --seconds 10 --concurrency 16 --p95 750
```

## 4. Təhlükəsizlik testləri

| Mövzu | Necə yoxlanır |
|---|---|
| **Default-deny marşrutlar** (yeni) | Hər host üçün `EndpointSecurity.AnonymousRoutes`: avtorizasiyasız marşrutların siyahısı **dəqiq** gözlənilən siyahıya bərabər olmalıdır (ClinicCore/Billing/Notify: yalnız `GET /health`; Identity: `health`, `login`, `refresh`). `RequireAuthorization()` unudulsa test düşür. **Mutasiya yoxlaması:** Billing-dən `RequireAuthorization()` silindi → test `GET /v1/invoices`, `/services`, ... marşrutlarını açıq tapıb düşdü; bərpa edildi. |
| Autentifikasiya | Saxta/imzasız (`alg:none`) token, başqa tenant-ın tokeni (`tenant.mismatch`), brute-force kilidi, refresh token **təkrar istifadəsi bütün ailəni ləğv edir**, paralel refresh yarışı |
| RBAC və scope | `own < branch < tenant` hər modulda: başqa filial 403/404 (varlığı sızdırmır), geri qaytarma limiti `max_amount` |
| Tenant izolyasiyası | Başqa tenant-ın id-si ilə sorğu 404; `X-Tenant` yalnız dev-də; Gateway xaricdən gələn `X-Tenant`/`X-Forwarded-*` başlıqlarını silir |
| PHI qorunması | Telefon/FİN/e-poçt DB-də şifrəli, API-də default maskalı; `reveal` ayrıca icazə + audit; `Cache-Control: no-store`; sorğu sətri log-lanmır |
| Dəyişməzlik | `audit_log`/`payments` append-only (DB trigger), audit zənciri bütövlük yoxlaması |
| Brauzer | Refresh token yalnız `HttpOnly; SameSite=Strict; Path=/api/session` cookie-dədir; access token yalnız yaddaşdadır (storage-da JWT yoxdur: e2e yoxlayır); BFF **Origin yoxlaması** (cross-origin → 403, upstream çağırılmır); təhlükəsizlik başlıqları |
| Asılılıqlar | `pnpm audit --prod`: 0; `dotnet list package --vulnerable --include-transitive`: 0 |

## 5. Əhatə

**Backend** (`dotnet-coverage`, yalnız `DentaCore.*`; üçüncü tərəf kitabxanalar hesabdan kənar):

| Təbəqə | Sətir | Branch |
|---|---|---|
| Domain | 97.4% | 94.5% |
| Application | 96.7% | 81.4% |
| Infrastructure | 92.1% | 76.9% |
| **Cəmi** | **95.2%** (7182/7546) | **86.2%** |

Infrastructure-un aşağı hissəsi `Search.Infrastructure` (53%): onun 26 inteqrasiya testi Elasticsearch tələb edir və bu ölçmədə atlanıb. ES ilə əvvəlki işə salmada bu testlər keçmişdi. Host layihələrinin (`*.Api`) sətir rəqəmi etibarlı deyil: `Program.cs` lambdaları alətdə ayrıca sinif kimi görünmür; onların davranışı inteqrasiya testləri ilə sınanır (məs. 62 ClinicCore HTTP testi).

**Frontend** (V8, həddlər `vitest.config`-də: düşərsə `pnpm test:coverage` uğursuz olur):

| Paket | Statements | Branches | Hədd |
|---|---|---|---|
| `@dentacore/api-client` | 93.3% | 72.1% | 90 / 70 |
| `@dentacore/ui` | 95.2% | 90.4% | 90 / 85 |
| `@dentacore/web` | 72.3% | 58.3% | 70 / 55 |

Bu mərhələdə `web` əhatəsi **33.7% → 72.3%** qaldırıldı (ekran komponentləri əvvəl yalnız e2e ilə sınanırdı). Qalan boşluq: `AuthProvider`, `useRealtime` (SignalR), shell `layout`, dashboard səhifəsi: onlar e2e ilə örtülür, amma vahid səviyyədə ölçülmür.

## 6. Mutasiya yoxlamaları (testin dişi var)

Əvvəlki dilimlərdə kritik testlər (Billing kilidləri, mesajlaşma başlanğıcı, sync horizontu) əl ilə pozulub-bərpa edilərək yoxlanıb; təfərrüat həmin dilimlərin qeydlərindədir (`07-source-code.md`) və **bu mərhələdə təkrar işlədilməyib**.

Bu mərhələdə işlədilən və təsdiqlənən: Billing host-undan `RequireAuthorization()` silindi → default-deny testi `GET /v1/invoices`, `/services`, `/cash-shifts/current` və s. açıq marşrutlarını göstərərək düşdü; bərpadan sonra keçdi (§4).

Avtomatik mutasiya aləti (Stryker) hələ yoxdur (§9).

## 7. Yük və davamlılıq

`tests/load/load.mjs`, Gateway vasitəsilə, 16 paralel bağlantı, ssenari başına 8 s, **bir maşında** (bütün servislər + PG + RabbitMQ eyni yerdə, Elasticsearch yoxdur, Redis yoxdur → dashboard prosess-daxili keşlə):

| Ssenari | rps | p50 | p95 | p99 | Xəta |
|---|---|---|---|---|---|
| GET dashboard/summary (keşli) | 2017 | 5.9 ms | 16 ms | 23 ms | 0 |
| GET patients (siyahı) | 232 | 56 ms | 79 ms | 91 ms | 0 |
| GET patients?q= (SQL axtarış) | 336 | 47 ms | 62 ms | 71 ms | 0 |
| GET appointments (gün) | 306 | 46 ms | 75 ms | 93 ms | 0 |
| GET invoices | 162 | 70 ms | 99 ms | **2290 ms** | 0 |
| POST patients (PII şifrələmə + audit + outbox) | 212 | 69 ms | 110 ms | 145 ms | 0 |
| GET patients tokensiz (401) | 4106 | 3.3 ms | 8.5 ms | 12 ms | 0 |

**Yük altında invariantlar** (yazma ssenarisindən sonra SQL ilə): 1699 pasiyent yaradıldı, bütün `outbox_messages` (12 219 sətir) `processed_at` ilə **boşaldı, 0 gözləyən**; audit sətirləri yaradılan qeydləri əhatə edir.

Qeydlər:
- Rəqəmlər **mütləq istehsal ölçüsü deyil** (tək maşın, paylaşılan CPU). Məqsəd reqressiya: hədd (`--p95`, `--max-errors`) aşılarsa skript çıxış kodu 1 verir.
- `GET invoices` p99 = 2.3 s: sorğuların ≥1%-i (≈13 sorğu) 2.3 s-dən yavaş idi, p95 isə 99 ms. Səbəb **araşdırılmayıb** (soyuq bağlantı/JIT, pool gözləməsi, GC və ya kilid ehtimalları). Mərhələ 9-da davamlı yük (dəqiqələrlə) və `pg_stat_statements` ilə yenidən ölçülməlidir; bu hələ açıq sualdır.
- Yük testi yazma ssenarisində e2e bazasına minlərlə pasiyent əlavə edir; ayrıca baza istifadə edin.

## 8. Tapılmış səhvlər (bu mərhələ və 8b)

| Səhv | Necə tapıldı |
|---|---|
| `/auth/me` filialları qaytarmırdı (spesifikasiyada var idi) | Playwright: UI-da pasiyent yaratmaq mümkün olmadı |
| OpenAPI ≠ backend (`PatientListItem`, `Invoice.items`, `CashShift`) | Tiplə yazılmış klient tipləri uyğunsuz çıxdı |
| `ApiError` kodu itirirdi (gövdə artıq oxunub) | Vahid test: `code` gözlənilən, `http.422` gəldi |
| SignalR dev-də tenant başlığı göndərmirdi | Brauzer konsolu `tenant.not_found` |
| Gateway 30/dəq auth limiti e2e-ni 429-a salırdı | E2E: təsadüfi giriş uğursuzluqları |
| Qəbul testi `Gün` etiketini qrafikin `aria-label`-i ilə qarışdırırdı | Playwright strict locator xətası (test səhvi) |

## 9. Məlum boşluqlar və növbəti addımlar

1. **Elasticsearch testləri CI-də** (26 + 2) servis konteyneri ilə işləməlidir; 9.x uyğunluğu yoxlanmayıb.
2. **Mutasiya testi avtomatlaşdırılmayıb** (əl ilə). Stryker.NET və Stryker-JS Mərhələ 9-da gecə CI işi kimi təklif olunur.
3. **Yük testi qısadır** (8 s). Soak (saatlarla), ölçülü məlumat (10⁶ pasiyent), RabbitMQ geriləməsi və PG failover sınanmayıb. Bax §7.
4. **Fuzz/property testləri** yoxdur (validatorlar və `sync` birləşdirmə məntiqi üçün yaxşı namizədlər).
5. **DAST/SAST** (ZAP, CodeQL, Semgrep) və konteyner skanı Mərhələ 9 CI-nin tərkibindədir; bu mərhələdə yalnız asılılıq auditi işlədi.
6. **E2E** yalnız Chromium; Firefox/WebKit və mobil viewport yoxdur. Elasticsearch ilə e2e ayrıca işlədilməyib.
7. **Backup/restore və DR** sınaqları Mərhələ 9-dadır (yoxlanmamış ehtiyat nüsxə, ehtiyat nüsxə deyil).
8. 2FA təsdiqi, JWKS, `POST /invoices` idempotentliyi: funksional boşluqlar olduğu üçün test də yoxdur.
