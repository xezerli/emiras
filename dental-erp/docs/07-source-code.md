# Mərhələ 7 — Source Code

> Bu mərhələ böyükdür və dilimlərlə gedir. Hər dilim kompilyasiya olunur, testlərdən keçir və ayrıca commit olunur.

| # | Dilim | Status |
|---|---|---|
| 1 | BuildingBlocks + Identity (login, JWT, refresh rotasiyası, tenant həlli, outbox) | ✅ Tamamlandı |
| 2 | Patient (qeydiyyat, axtarış, tibbi profil, audit) | Növbəti |
| 3 | Scheduling (qəbul, double-booking, növbə, xatırlatma) | |
| 4 | Clinical (vizit, odontoqram, plan, resept, qeyd) | |
| 5 | Billing (faktura, ödəniş, kassa smeni, refund) + vizit→faktura saga | |
| 6 | Audit, Outbox publisher (RabbitMQ), Notify (SignalR), Gateway (YARP) | |
| 7 | Dashboard, Sync, Elasticsearch indexer | |
| 8 | Frontend (Next.js): Design System, auth, əsas ekranlar | |

---

## Dilim 1: BuildingBlocks və Identity

### Necə işlətmək

```bash
cd dental-erp/backend
dotnet build DentaCore.sln                                 # 0 xəta, 0 xəbərdarlıq (xəbərdarlıqlar xəta sayılır)
dotnet test tests/DentaCore.Identity.UnitTests             # 44 test, DB lazım deyil
dotnet test tests/DentaCore.Architecture.Tests             # 7 qayda

# İnteqrasiya testləri real PostgreSQL tələb edir (superuser; hər sinif üçün müvəqqəti DB yaradır və silir):
export DENTACORE_TEST_PG="Host=localhost;Port=5432;Username=postgres;Password=...;Database=postgres"
dotnet test tests/DentaCore.Identity.IntegrationTests      # 16 test; env yoxdursa Skip olunur
```
İnteqrasiya testləri `db/migrations/*.sql` faylını **istehsaldakı kimi** tətbiq edir (trigger, constraint, RLS daxil), sonra `WebApplicationFactory<Program>` ilə Identity.Api-ni real host kimi qaldırır.

### Nə yazıldı

**BuildingBlocks.Domain**: `Entity`, `AggregateRoot` (domen hadisələri + `RowVersion`), `DomainEvent`, `Result<T>`/`Error` (gözlənilən xətalar exception ilə yox, nəticə ilə qaytarılır).

**BuildingBlocks.Application**: CQRS (`ICommand`, `IQuery`, `IRequestHandler`), `ISender` dispatcher və pipeline: `Logging → Validation (FluentValidation) → UnitOfWork`. MediatR **bilərəkdən işlədilmir**: v13-dən kommersiya lisenziyası tələb edir. Əvəzində ~80 sətirlik öz dispatcher-imiz var.

**BuildingBlocks.Infrastructure**:
- *Outbox:* `DomainEventsToOutboxInterceptor` aggregate-lərin hadisələrini eyni tranzaksiyada `outbox_messages`-ə yazır ("DB yazıldı, mesaj itdi" mümkün deyil).
- *Tenancy:* `TenantResolutionMiddleware` (domen → slug → `platform.tenants`), hər tenant üçün ayrıca `NpgsqlDataSource` (`search_path=t_xxx,public`), `TenantClaimGuardMiddleware` (JWT `tid` ≠ sorğunun tenant-ı → 403).
- *Security:* `AesGcmPiiProtector` (AES-256-GCM + HMAC blind index, açar rotasiyası üçün versiya baytı).
- *Http:* `Result` → RFC 9457 `problem+json` xəritələməsi.

**Identity** (Domain / Application / Infrastructure / Host):
- `User` aggregate: bloklama siyasəti (5 uğursuz cəhd → 15 dəq), `UserLoggedIn`/`UserLockedOut` hadisələri.
- `RefreshToken` aggregate: yalnız SHA-256 heş saxlanır, `family_id` ilə rotasiya.
- Əmrlər: `LoginCommand`, `RefreshTokenCommand`, `LogoutCommand`, sorğu `GetCurrentUserQuery`.
- Argon2id (OWASP parametrləri, parametrlər heş-in içindədir), RS256 JWT (açar PEM ilə), JwtBearer konfiqurasiyası.
- Endpoint-lər: `POST /v1/auth/login|refresh|logout`, `GET /v1/auth/me`, `GET /health`, auth üçün IP başına rate limit.

### Təhlükəsizlik qərarları və niyə

| Qərar | Səbəb |
|---|---|
| Yanlış parol və mövcud olmayan email eyni 401 + `BurnTime` | Email enumerasiyası və timing hücumu |
| JWT alqoritmi sabit RS256 (`ValidAlgorithms`) | `alg: none` və HS256/RS256 qarışdırma hücumu. Testlə yoxlanılıb |
| Refresh token **atomik** istifadə: `UPDATE ... WHERE used_at IS NULL` | Paralel iki sorğudan yalnız biri qalib. Testlə yoxlanılıb (8 paralel sorğu) |
| İstifadə olunmuş refresh token təkrar gəlsə, bütün family ləğv olunur və hadisə yazılır | Oğurlanmış token aşkarlanması |
| Tokendə yalnız `permission@scope`, məbləğ limiti yox | Token şişməsin. Limitlər (`max_amount`) tələb olunduqda DB-dən yoxlanılacaq |
| `X-Tenant` header-i yalnız konfiqurasiya ilə (`AllowTenantHeader`), default söndürülü | İstehsalda tenant yalnız domendən gəlir |
| Açar yoxdursa host istehsalda **qalxmır** (fail-fast) | Səssiz zəif konfiqurasiya olmasın |
| Uğursuz giriş sayğacı handler-də açıq `SaveChanges` ilə saxlanır | Pipeline yalnız uğurlu nəticədə saxlayır, bloklama isə uğursuz nəticənin yan təsiridir |

### Testlər (67 test)

| Layer | Say | Nəyi yoxlayır |
|---|---|---|
| Unit (Domain, Handler, Security, Pipeline) | 44 | Bloklama və vaxt bitməsi, IP məhdudiyyəti (IPv4-mapped IPv6 daxil), 2FA-da token verilməməsi, refresh rotasiyası və reuse, AES-GCM saxtakarlıq aşkarı, Argon2 səhv formatlar, validasiya pipeline |
| Integration (real PostgreSQL + real HTTP host) | 16 | Tam login/refresh/logout axını, JWT claim-ləri, outbox atomikliyi, email-in açıq saxlanmaması, cross-tenant bloku, saxta/`alg:none` token, 8 paralel refresh |
| Architecture | 7 | Qat və modul sərhədləri |

### Test zamanı tapılan və düzəldilən real bug

Unit testlər (fake ilə) keçirdi, real DB testi isə iki problemi tapdı:
1. Refresh race-ni uduzan sorğu family ləğvini **tranzaksiya daxilində** edirdi, tranzaksiya commit olunmadan bağlandığı üçün ləğv geri qaytarılırdı. İndi tranzaksiya yalnız qalib yolunda yaşayır.
2. Ləğv olunmuş family-nin hər təkrar cəhdi yeni `RefreshTokenReuseDetected` yazırdı. İndi hadisə yalnız həqiqətən sətir ləğv olunanda yazılır.

### Məlum məhdudiyyətlər (dürüst qeyd)

- **2FA tamamlanmayıb.** 2FA aktiv istifadəçiyə 202 + challenge qaytarılır və token **verilmir** (təhlükəsiz default), amma `POST /auth/2fa/verify` hələ yoxdur (TOTP növbəti Identity dilimində).
- **İstifadəçi yaratma/dəvət, parol sıfırlama, rol idarəsi** yoxdur (Admin dilimi). İnteqrasiya testləri istifadəçini SQL ilə yaradır.
- **JWKS endpoint-i** (`/.well-known/jwks.json`) yoxdur: digər servislər hələ public açarı almır. Gateway dilimində əlavə olunacaq.
- **Forwarded headers / proxy etibarı** konfiqurasiya olunmayıb: rate limit və IP məhdudiyyəti reverse proxy arxasında düzgün IP-ni görməsi üçün Gateway/K8s mərhələsində `KnownProxies` təyin olunmalıdır. Bu olmadan proxy arxasında hamı eyni IP-dən görünər.
- **Outbox publisher** (RabbitMQ) yoxdur: hadisələr `outbox_messages`-də yığılır (Dilim 6).
- **Audit yazısı** hələ yoxdur: `audit_log` cədvəli var, yazan modul Dilim 6-dadır.
- **İnteqrasiya testləri Testcontainers əvəzinə mühit dəyişəni** ilə işləyir (bu mühitdə Docker yoxdur). CI-də (Mərhələ 9) Postgres service container ilə qoşulacaq.
- Testlər .NET 10 runtime üzərində `DOTNET_ROLL_FORWARD=Major` ilə işləyir (Mərhələ 6 §8).
- `appsettings` içində sirr yoxdur. Açarlar mühit dəyişəni ilə verilməlidir: `Security__PiiEncryptionKey`, `Security__PiiHashKey` (`openssl rand -base64 32`, fərqli olmalıdırlar), `Jwt__SigningKeyPem` (RSA-2048 PKCS#8), `ConnectionStrings__Default`.

> Növbəti dilim: **Patient** (qeydiyyat + dublikat yoxlaması, axtarış, tibbi profil, hər oxumanın auditi, şifrələnmiş telefon/email/FİN, RBAC icazə yoxlaması). Davam etmək üçün **"Davam et"** yazın.
