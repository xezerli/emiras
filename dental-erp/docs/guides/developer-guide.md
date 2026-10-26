# Developer guide

Bu bələdçi yeni developer-in repo-nu qaldırıb, test edib, dəyişiklik göndərməsi üçündür. Əmrlər bu sessiyada işlədilib (ayrıca qeyd olunmayıbsa); işlədilməyənlər "yoxlanmayıb" ilə işarələnib.

## 1. Tələblər

.NET 9 SDK (`backend/global.json`), Node 22 + pnpm, PostgreSQL 16, RabbitMQ 3; könüllü: Redis, Elasticsearch 8.11 (olmasa axtarış SQL-ə düşür). E2E üçün Chromium (Playwright). Docker **tələb olunmur**: `infra/dev/services.sh` və `infra/dev/README.md` Docker-siz qurulumu göstərir.

## 2. Repo xəritəsi

| Qovluq | Nə var |
|---|---|
| `api/` | OpenAPI 3.1 kontrakt (`openapi.yaml`): UI tipləri buradan yaranır |
| `backend/src/Modules/<Ad>/{Domain,Application,Infrastructure}` | Biznes modulları; `Hosts/` ayrıca proseslər (Gateway, Identity, ClinicCore, Billing, Notify, Workers) |
| `backend/src/BuildingBlocks` | Result/Error, `ISender` pipeline-ı, outbox, PII, tenancy |
| `backend/tests` | Unit, inteqrasiya və **Architecture** testləri (qatlar və modullar arası asılılıq) |
| `db/` | Miqrasiyalar (`migrations/platform`, `migrations/tenant`), `migrate.sh`, `tests.sql` |
| `frontend/` | pnpm workspace: `apps/web` (Next.js), `packages/{ui,api-client,config}` |
| `infra/` | `docker`, `helm`, `observability`, `backup`, `dev` |
| `tests/` | `coverage.sh`, `load`, `ci`, `docs` (bələdçi nümunələrinin yoxlanışı) |
| `docs/` | Mərhələ sənədləri 00–10, `adr/`, `guides/` |

## 3. Lokal qurulum

```bash
# 1) PostgreSQL, RabbitMQ, Redis. Bu skript sandbox yollarına (/tmp/pgdata54329, soket 54329) bağlıdır;
#    Docker olan mühitdə infra/docker/compose.yaml istifadə et (bu bələdçidə yoxlanmayıb)
infra/dev/services.sh

# 2) bütün backend servisləri + gateway (:5100), miqrasiya, "demo" tenant və seed
infra/dev/run-stack.sh start          # giriş məlumatları: /tmp/dentacore-stack/seed.txt
infra/dev/run-stack.sh stop

# 3) veb
cd frontend && pnpm install
pnpm --filter @dentacore/web e2e:build            # API_ORIGIN və NEXT_PUBLIC_DEV_TENANT build zamanı "bişir"
API_ORIGIN=http://127.0.0.1:5100 DEV_TENANT=demo pnpm --filter @dentacore/web exec next start -p 3100
```

Seed istifadəçiləri `admin@demo.test`, `reception@demo.test`, `doctor@demo.test` və s.; parol `seed.txt` faylındadır (`SEED_PASSWORD` ilə təyin edilə bilər). Açarlar (JWT RSA, PII) `STACK_DIR`-də yaranır və repoya düşmür.

**`next dev` haqqında.** `API_ORIGIN`, `DEV_TENANT`, `NEXT_PUBLIC_DEV_TENANT` mühit dəyişənləri ilə işləyir; `/v1/*` sorğuları Next rewrite ilə gateway-ə gedir. Brauzer kodu tenantı `X-Tenant` başlığı ilə özü göndərir. **Çılpaq `curl` ilə** `/v1/...` çağıranda `-H 'X-Tenant: demo'` lazımdır, yoxsa gateway `404` (tenant tapılmadı) qaytarır: bu gateway-in davranışıdır (birbaşa `:5100`-ə də eyni nəticə yoxlanıb). `X-Tenant` yalnız `Tenancy:AllowTenantHeader=true` olanda (dev/test) qəbul olunur; istehsalda tenant subdomen-dən götürülür, lokal serverdə `Tenancy:FixedTenant` ilə. Qeyd: `next dev` ilə tam brauzer axını bu sessiyada ayrıca yoxlanmayıb; yoxlanmış yol `next build` + `next start` + Playwright-dır.

API nümunələrini real stack-ə qarşı işlətmək: `tests/docs/api-guide-examples.sh` (parol `Passw0rd!e2e` seed ilə, tenant `demo`).

## 4. Test

```bash
export DENTACORE_TEST_PG='Host=/tmp;Port=54329;Username=postgres;Database=postgres'
export DENTACORE_TEST_AMQP='amqp://guest:guest@localhost:5672/'
export DENTACORE_TEST_REDIS='127.0.0.1:6390'      # könüllü
export DENTACORE_TEST_ES='http://127.0.0.1:9200'  # könüllü, yoxdursa ES testləri atlanır
dotnet test backend/DentaCore.sln

cd frontend && pnpm lint && pnpm typecheck && pnpm test
PLAYWRIGHT_CHROMIUM=/opt/pw-browsers/chromium pnpm --filter @dentacore/web e2e   # stack və next start işləyir
```

Pul/DB davranışı üçün inteqrasiya testi yaz (real PostgreSQL), mock yox. CI atlanmış testlərə icazə vermir (`tests/ci/assert-no-skips.py`), ona görə xidmət olmadan atlanan test ya xidməti CI-də qaldırmalı, ya da silinməlidir. Tam strategiya: [08-testing](../08-testing.md). Sandbox qeydi: yalnız .NET 10 runtime varsa `DOTNET_ROLL_FORWARD=LatestMajor` lazımdır.

## 5. Resept: yeni miqrasiya

1. `db/migrations/tenant/T013_<ad>.sql` (platform üçün `P…`). Mövcud faylı **dəyişmə**: `migrate.sh` SHA-256 fərqini tutub dayanır.
2. Additiv yaz (expand/contract, bax [09-deployment](../09-deployment.md) §6); geri qaytarma yoxdur.
3. `db/test-migrate.sh` və `db/tests.sql` işlət; gözləyənləri `DATABASE_URL=… db/migrate.sh status` göstərir, tətbiq üçün `up`.

## 6. Resept: yeni endpoint

1. **Kontrakt:** `api/openapi.yaml` (xəta kodları, başlıqlar daxil). `pnpm gen:api` UI tiplərini yeniləyir.
2. **Domain/Application:** əmr və ya sorğu + handler; icazə lazımdırsa `IRequiresAccess` (permission və scope), səhvlər `Result`/`Error` ilə.
3. **Host:** `MapGet/MapPost(...).RequireAuthorization()`. Avtorizasiyasız marşrut siyahısını sabitləyən test (`EndpointSecurity`) yeni açıq endpoint-də düşür: bu istənilən davranışdır.
4. **Audit/PII:** PHI oxuyan/yazan əməliyyat auditdə olmalıdır; PII `IPiiProtector` ilə şifrələnir, cavabda maskalanır ([security-and-privacy](security-and-privacy.md) §10).
5. **Test:** unit (domain qaydası) + inteqrasiya (HTTP, real DB, icazə rədd halı).
6. Kontraktla real davranış ayrılırsa, [api-guide §9](api-guide.md#9-openapi-ilə-real-davranış-arasındakı-fərqlər) cədvəlinə yaz.

## 7. Resept: yeni UI səhifəsi

`apps/web/src/app/(app)/<bölmə>/page.tsx` + `features/<bölmə>/`. Server tərəfi sirr bilmir; sorğular `@dentacore/api-client` və React Query ilə gedir. Rənglər/komponentlər `packages/ui` tokenlərindən; yeni vizual elementin axe (WCAG 2.2 AA) testindən keçdiyini e2e ilə yoxla. Next page faylından `page`, `layout` və s. xaricində heç nə export etmə (bunun üçün `features/*/` faylı aç).

## 8. Gözlənilən tələlər

- **Servislər sandbox-da turlar arasında ölür:** `infra/dev/services.sh`.
- `pkill -f`/`pgrep -f` əmr sətrində olan nümunə ilə öz shell-ini öldürə bilər (exit 144).
- `next build` `API_ORIGIN`-i və `NEXT_PUBLIC_*`-i **build vaxtı** bişirir: dəyişəndə yenidən build et.
- Refresh cookie `Path=/api/session`; `localStorage`-da token axtarma, orada yoxdur.
- Idempotency-Key **yalnız** ödəniş və refund-da tələb olunur.

## 9. PR prosesi

Qısa qollar, hər PR-da lint + typecheck + test yaşıl olmalıdır (CI 9 yoxlama işlədir). Commit mesajı: nə və niyə. Bax [CONTRIBUTING.md](../../CONTRIBUTING.md).
