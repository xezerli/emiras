# Mərhələ 7 — Source Code

> Bu mərhələ böyükdür və dilimlərlə gedir. Hər dilim kompilyasiya olunur, testlərdən keçir və ayrıca commit olunur.

| # | Dilim | Status |
|---|---|---|
| 1 | BuildingBlocks + Identity (login, JWT, refresh rotasiyası, tenant həlli, outbox) | ✅ Tamamlandı |
| 2 | Patient (qeydiyyat, axtarış, tibbi profil) + RBAC scope + hash-chain audit | ✅ Tamamlandı |
| 3 | Scheduling (qəbul, double-booking, növbə, xatırlatma) | Növbəti |
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

---

## Dilim 2: Patient, RBAC scope və audit zənciri

### Nə yazıldı

**Ümumi infrastruktur (BuildingBlocks):**
- `CurrentUser`: JWT `perm` (`code@scope`) və `branch` claim-lərindən icazə modeli. `tenant` hamısını, `branch` istifadəçinin filiallarını, `own` yalnız özünün yaratdığını görür. DB-dən və HTTP-dən asılı deyil, tam unit-test olunub.
- Pipeline genişləndi: `Logging → Authorization → Validation → Audit → UnitOfWork`. Sorğu `IRequiresAccess` daşıyırsa icazə handler-dən əvvəl yoxlanılır, `IAuditable` daşıyırsa uğurlu nəticədən sonra audit zəncirinə yazılır.
- `Optional<T>`: JSON Merge Patch-də "sahə göndərilməyib" ilə "null göndərilib" fərqi.
- `412 Precondition Failed` (`If-Match`), `ConcurrencyExceptionMiddleware` (EF konkurrensi toqquşması → 412), `Error.Details` (məs. dublikat pasiyentin id-si).
- `AddJwtValidation`: Identity-dən başqa host-lar JWT-ni yalnız public açarla (`Jwt__PublicKeyPem`) yoxlayır.

**Audit modulu:** `audit_log` zənciri. Hər qeyd əvvəlkinin heş-ini daşıyır. Heş **DB-də** hesablanır (jsonb/inet saxlanarkən normallaşdığı üçün tətbiqdə hesablanan heş sonradan yoxlananda uyğun gəlməzdi). Yazılar tenant üzrə advisory lock ilə ardıcıllaşdırılır. `IAuditChainVerifier` bütün zənciri yenidən hesablayır və pozulan ilk `id`-ni qaytarır.

**Patient modulu:**
- `Patient` aggregate (qeydiyyat, merge-patch ilə yeniləmə: "ya hamısı, ya heç nə", soft delete), `PatientAllergy`.
- Telefon/email/FİN şifrələnir (AES-256-GCM) + blind index. Telefon normallaşdırılır (`+994 50 123-45-67`, `0501234567`, `00994...` eyni heş verir).
- Əmrlər/sorğular: `RegisterPatient`, `GetPatient`, `SearchPatients`, `UpdatePatient`, `DeletePatient`, `AddAllergy`, `GetMedicalProfile`.
- Endpoint-lər (`ClinicCore.Api`): `GET/POST /v1/patients`, `GET/PATCH/DELETE /v1/patients/{id}`, `GET /v1/patients/{id}/medical-profile`, `POST /v1/patients/{id}/allergies`.
- Miqrasiya `T007_patient_permissions.sql`: `patient:read_sensitive` icazəsi.

### Təhlükəsizlik və konfidensiallıq qərarları

| Qərar | Səbəb |
|---|---|
| Telefon/email/FİN cavabda **default maskalı** (`+994*******67`). Açıq dəyər `?reveal=true` ilə, ayrıca `patient:read_sensitive` icazəsi ilə və `patient.read.sensitive` kimi audit olunur | Minimum lazımi giriş (HIPAA/GDPR prinsipi) |
| Scope xaricindəki pasiyent **404** qaytarır (403 yox) | Başqa filialdakı pasiyentin mövcudluğu sızmasın |
| Dublikat xətasında mövcud pasiyentin id-si yalnız onu görməyə haqqı olana göstərilir | Filiallararası məlumat sızması |
| Audit-ə PHI yazılmır: axtarış mətni yox, yalnız nəticə sayı; yeniləmədə dəyər yox, yalnız sahə adları | Audit log-un özü PHI anbarına çevrilməsin |
| İcazəsiz cəhd də auditə yazılır (`permission.denied`) | "Kim nəyə cəhd etdi" izi |
| Audit yazıla bilmirsə sorğu uğursuz olur (fail-closed) | Audit olunmamış PHI girişi qəbul edilmir |
| `Cache-Control: no-store` pasiyent cavablarında | PHI brauzer/proxy keşinə düşməsin |
| Tanınmayan axtarış mətni (`%`, `_`) boş nəticə qaytarır, LIKE joker simvolları literal sayılır | Süzgəcsiz bütün siyahının qaytarılmasının qarşısı |
| `PATCH` üçün `If-Match` məcburidir (428), köhnə versiya 412 | İtirilmiş yeniləmə (lost update) |
| Eyni telefon ailə üzvləri üçün normaldır: 409 xəbərdarlıq, `confirmDuplicate=true` ilə keçilir | Real klinika ssenarisi |

### Testlər (166 test, hamısı keçir)

| Layer | Say | Əhatə |
|---|---|---|
| Architecture | 7 | Qat və modul sərhədləri |
| BuildingBlocks unit | 16 | Scope modeli (tenant/branch/own, saxta claim-lər), Authorization və Audit behavior |
| Identity unit | 44 | (Dilim 1) |
| Patient unit | 62 | Domen invariantları, telefon normallaşması, maskalama, handler-lər, patch parser, axtarış kriteriyaları |
| Identity integration | 16 | (Dilim 1), filial claim-i əlavə olunandan sonra yenidən yoxlanıldı |
| Patient integration | 21 | Real PostgreSQL və real HTTP host: şifrələmə, outbox, audit, filial/own scope, reveal, axtarış (sıra, format, səhifələmə, joker), PATCH konkurrensi (6 paralel yazan), soft delete, allergiya, tenant izolyasiyası, audit zəncirinin pozulması aşkarı |

Patient inteqrasiya testləri 3 dəfə ardıcıl işlədildi, hamısı yaşıl.

### Testlərin tapdığı real bug-lar (düzəldilib)

1. **PATCH cavabı köhnə `rowVersion`/ETag qaytarırdı:** DTO `SaveChanges`-dən əvvəl qurulurdu. İndi əvvəl saxlanır.
2. **`q=%` bütün pasiyentləri qaytarırdı:** tanınmayan mətn heç bir süzgəc yaratmırdı. İndi boş nəticə.
3. **Raw SQL alias-ları:** EF-nin snake_case naming convention-ı raw SQL sütun adlarına da tətbiq olunur (`ChartNo` → `chart_no`, `Icd10Code` → `icd10code`). Bu xəta Identity-də də gizlənmişdi (yeni `BranchRow`), inteqrasiya testi tutdu.

### Məlum məhdudiyyətlər (dürüst qeyd)

- **`own` scope yalnız "özünün qeydiyyata aldığı" deməkdir.** Həkimin "öz pasiyentləri" (qəbulu/vizitləri olan) anlayışı Scheduling və Clinical dilimlərində gələcək. Hələlik seed rolunda həkimin `patient:read` icazəsi `own`-dur, yəni qeydiyyatı özü etməyibsə pasiyenti görmür. Dilim 3-4-də "baxan həkim" əlaqəsi əlavə olunacaq.
- **Axtarış PostgreSQL-dədir** (trigram index + dəqiq heş). Elasticsearch (fuzzy, orfoqrafik səhv) Dilim 7-də. Azərbaycan hərflərinin (Ə, İ/ı) böyük/kiçik hərf fərqsizliyi DB-nin collation-undan asılıdır: istehsal bazası UTF-8 locale (`az_AZ.UTF-8` və ya ICU) ilə yaradılmalıdır. Test bazası default locale ilə işləyir.
- **`Idempotency-Key` hələ yoxdur** (OpenAPI-də var). Dublikat telefon yoxlaması qismən qoruyur, amma telefonsuz iki eyni sorğu iki pasiyent yaradır.
- **Anamnez, xəstəlik, dərman yazma endpoint-ləri yoxdur** (yalnız oxuma və allergiya əlavəsi). Sənəd/görüntü yükləmə (S3), razılıq/e-imza, ailə əlaqələri, sığorta, pasiyent birləşdirmə (merge) yoxdur.
- **`balance` sahəsi** Billing hazır olana qədər qaytarılmır.
- **Audit partition-ları** yalnız cari və növbəti ay üçün T001 ilə yaranır. Aylıq partition yaradan job (Workers) yazılmayıb, o olmadan ay dəyişəndə audit yazısı uğursuz olar. Bu, Dilim 6-da mütləq bağlanmalıdır.
- **Audit throughput-u** tenant başına advisory lock ilə ardıcıllaşdırıldığı üçün məhduddur (klinika miqyası üçün kifayət, böyük şəbəkələr üçün ölçülməlidir).
- **Rate limit və forwarded headers** hələ yalnız Identity-dədir (Gateway dilimində).
- Əvvəlki dilimlərin məhdudiyyətləri (2FA, JWKS, outbox publisher, Testcontainers) qüvvədə qalır.

> Növbəti dilim: **Scheduling** (qəbul yaratma, DB səviyyəsində double-booking qorunması ilə uyğunlaşdırma, boş slotlar, check-in/növbə, no-show, xatırlatma planlaması). Davam etmək üçün **"Davam et"** yazın.
