# Mərhələ 7 — Source Code

> Bu mərhələ böyükdür və dilimlərlə gedir. Hər dilim kompilyasiya olunur, testlərdən keçir və ayrıca commit olunur.

| # | Dilim | Status |
|---|---|---|
| 1 | BuildingBlocks + Identity (login, JWT, refresh rotasiyası, tenant həlli, outbox) | ✅ Tamamlandı |
| 2 | Patient (qeydiyyat, axtarış, tibbi profil) + RBAC scope + hash-chain audit | ✅ Tamamlandı |
| 3 | Scheduling (qəbul, double-booking, boş slotlar, növbə, no-show, xatırlatma planı) | ✅ Tamamlandı |
| 4 | Clinical (vizit, odontoqram tarixçəsi, müalicə planı, resept allergiya yoxlaması ilə, imzalanan qeyd) | ✅ Tamamlandı |
| 5a | Mesajlaşma: Outbox publisher + inbox + consumer host (RabbitMQ), no-show consumer, tenant maintenance | ✅ Tamamlandı |
| 5b | Billing (qiymət siyahısı, faktura, ödəniş, kassa smeni, refund) + prosedur→qaralama faktura consumer-i | ✅ Tamamlandı |
| 6 | Audit publisher, Notify (SignalR), Gateway (YARP) | |
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

---

## Dilim 3: Scheduling

### Nə yazıldı

**Domen (`Scheduling.Domain`):**
- `Appointment` aggregate: `Book`, `Reschedule`, `CheckIn`, `Cancel`, `MarkNoShow` və status maşını. Qaydalar: müddət 5 dəq–8 saat, keçmişdə başlaya bilməz (5 dəq tolerans: gələn pasiyent), 2 ildən uzaq olmaz, check-in başlanğıcdan 2 saat əvvəldən bitməyə qədər, no-show yalnız başlanğıcdan sonra.
- `TimeSlot`: yarı açıq `[Start, End)`. 10:00–10:30 və 10:30–11:00 üst-üstə düşmür.
- `SchedulePlanner` (təmiz, I/O-suz): iş qrafiki yoxlaması, boş slot hesablanması, xatırlatma planı. Yerli (Bakı) vaxt ↔ UTC çevrilməsi bir yerdə.
- `QueueTicket`, `AppointmentReminder`, `WaitlistEntry`.

**Application / Infrastructure:**
- Əmrlər: `BookAppointment`, `RescheduleAppointment` (merge-patch: yalnız `start` verilsə müddət qorunur, sürüklə-burax üçün), `CancelAppointment`, `MarkNoShow`, `CheckIn`, `CallTicket`, `AddToWaitlist`. Sorğular: `GetAppointment`, `ListAppointments` (aralıq ≤ 62 gün), `GetAvailability`, `GetQueue`.
- Endpoint-lər (`ClinicCore.Api`): `/v1/appointments` (CRUD, `/availability`, `/check-in`, `/cancel`, `/no-show`), `/v1/queue`, `/v1/queue/{id}/call`, `/v1/waitlist`.
- **Modul kontraktları:** `Patient.Contracts.IPatientDirectory` (Scheduling pasiyentin adını/filialını bu kontraktla öyrənir) və `Scheduling.Contracts.ICareRelationships` ("baxan həkim"). Modullar bir-birinin daxilini görmür, arxitektura testi bunu yoxlayır.
- `ConstraintViolationException`: infrastruktur PostgreSQL constraint xətalarını (exclusion, unique, FK) Application qatının başa düşdüyü tipə çevirir.

### Əsas qərarlar

| Qərar | Səbəb |
|---|---|
| Double-booking-in **son hakimi DB-dəki `EXCLUDE` constraint-dir**. Tətbiq "əvvəlcə yoxla, sonra yaz" etmir | Yoxla-yaz yarışa açıqdır. 10 paralel sorğu testində tam 1 qəbul yaranır |
| Overlap-da 409 + **3 alternativ boş slot** (`alternativeSlots`) | UX sənədindəki söz: rədd etmək yox, çıxış yolu göstərmək |
| İş qrafiki heç təyin olunmayıbsa məhdudiyyət yoxdur | Yeni klinika qrafiki doldurana qədər işləyə bilsin. Qrafik var, həmin gün yoxdursa: iş günü deyil |
| Check-in bilet nömrəsi **advisory lock** altında verilir | 8 paralel check-in testində 1..8 təkrarsız və boşluqsuz |
| Ləğv/no-show/yenidən planlama zamanı gözləyən xatırlatmalar eyni tranzaksiyada ləğv olunur | Ləğv olunmuş qəbula SMS getməsin |
| Xatırlatma planı kanal olaraq pasiyentin `preferredChannel`-ını işlədir, `none` isə heç nə yaratmır | Pasiyentin seçimi |
| Scope xaricindəki qəbul 404 qaytarır, növbə yalnız filial/tenant scope ilə görünür | Mövcudluq sızmasın. Lobbi növbəsi filial səviyyəsindədir |
| Audit: `appointment.create/read/list/update/cancel/check_in/no_show`, `queue.read/call`, `waitlist.add`. Ad və səbəb auditə yazılmır | PHI audit-ə sızmasın |

### "Baxan həkim" əlaqəsi (Dilim 2 məhdudiyyətinin həlli)

Həkimin pasiyentlə (ləğv olunmamış) qəbulu varsa, pasiyent onun `own` scope-una daxildir: `GET /patients/{id}`, tibbi profil, allergiya əlavəsi və axtarış. Qəbul ləğv olunanda əlaqə yox olur. Bu, inteqrasiya testində başdan sona yoxlanılır (qəbul yox: 404, qəbul var: 200, ləğv: yenə 404).

### Testlər (253 test, hamısı keçir)

| Layer | Say |
|---|---|
| Architecture | 7 |
| BuildingBlocks unit | 16 |
| Identity unit / integration | 44 / 16 |
| Patient unit | 64 |
| Scheduling unit | 65 |
| ClinicCore integration (Patient + Scheduling, real PostgreSQL və real HTTP host) | 41 |

Scheduling unit testləri: domen status maşını və pəncərə sərhədləri, slot hesablanması (məşğul/məzuniyyət/keçmiş/bir neçə pəncərə, Bakı UTC+4), xatırlatma planı, handler-lər (scope, konflikt xəritələməsi, alternativlər, tranzaksiya sərhədləri). İnteqrasiya testləri: 10 paralel eyni slot, ardıcıl slotlar, otaq konflikti, iş saatı/məzuniyyət, availability ilə real rezerv uyğunluğu, reschedule + ETag + 412/428, 8 paralel check-in, no-show, list filtrləri və scope, baxan həkim, audit zənciri, tenant izolyasiyası. İnteqrasiya testləri 3 dəfə ardıcıl işlədildi, hamısı yaşıl.

### Testlərin tapdığı real bug-lar (düzəldilib)

1. **Ardıcıl qəbullar üst-üstə düşürdü:** Npgsql-in `NpgsqlRange(lower, upper)` konstruktoru **hər iki sərhədi daxil edir** (`[a,b]`). 10:00–10:30 qəbulundan sonra 10:30–11:00 DB-də konflikt verirdi. Üst sərhəd açıq göstərilir, testdə saxlanan aralığın `[...)` formatı yoxlanılır. Bu xəta kod oxuyarkən görünmürdü, yalnız real DB testi tutdu.
2. **EF xatırlatmaları qəbuldan əvvəl yazırdı** (FK pozuntusu, 500): reminder ilə appointment arasında əlaqə modeldə bəyan olunmamışdı və EF insert sırasını bilmirdi. FK modeldə bəyan olundu.
3. **Paralel test sinifləri bir-birinin JWT açarını əzirdi:** host konfiqurasiyası process-global mühit dəyişənləri ilə verilir. İndi bütün host testləri tək collection fixture-da ardıcıl işləyir.

### Məlum məhdudiyyətlər (dürüst qeyd)

- **Təkrarlanan qəbul (`recurrenceRule`) yoxdur** (OpenAPI-də qeyd olunub, Faza 2).
- **Xatırlatmalar yalnız planlanır** (`appointment_reminders`), göndərən worker və SMS/WhatsApp provayderləri yoxdur (Dilim 6 və Communication modulu).
- **Wait list yalnız əlavə olunur.** Ləğv olunan slot üçün avtomatik təklif və siyahıya baxış yoxdur.
- **no_show_count / risk skoru yenilənmir:** `AppointmentMissed` hadisəsi outbox-a düşür, amma onu Patient modulunda işləyən consumer (RabbitMQ publisher, Dilim 6) hələ yoxdur.
- **Provayder yoxlaması yalnız mövcudluqdur** (FK). Provayderin həkim rolunda olması, aktiv olması yoxlanılmır (Identity Contracts ilə gələcək).
- **İş qrafiki yalnız oxunur:** `provider_schedules` və `time_off` üçün idarəetmə endpoint-ləri (HR/Admin dilimi) yoxdur, testlər onları SQL ilə yaradır.
- **Otaq və filial məlumatı** (`rooms`) üçün ayrıca Organization modulu hələ yoxdur: Scheduling bu cədvəli oxu modeli ilə birbaşa sorğulayır.
- **`Scheduling:TimeZone`** klinika üçün tək IANA qurşağıdır (default `Asia/Baku`). Filial üzrə fərqli qurşaq dəstəklənmir. Tenant-ın `timezone` sahəsi ilə avtomatik uyğunlaşdırma yoxdur.
- **Real-time yeniləmə (SignalR) və Google/Outlook sinxronu yoxdur.** Təqvim indi yalnız sorğu ilə yenilənir.
- **`start`/`complete` statusları** Clinical dilimində vizitlə birgə gələcək.
- Əvvəlki dilimlərin məhdudiyyətləri (2FA, JWKS, outbox publisher, audit partition job-u, Idempotency-Key, Testcontainers) qüvvədə qalır.

---

## Dilim 4: Clinical

### Nə yazıldı

**Domen (`Clinical.Domain`):**
- `Visit`: açıq/bağlı, `VisitStarted` və `VisitClosed` hadisələri.
- `ToothRecord` (odontoqram): FDI validasiyası (11–48, 51–85), səth, vəziyyət. Yeni qeyd köhnəni əvəz edir (`superseded_at`), heç nə silinmir.
- `TreatmentPlan` + `PlanItem`: `draft → proposed → accepted → in_progress → completed`, həmçinin `rejected`/`cancelled`. Sətir cəmi: say × qiymət × (1 − endirim%), bank yuvarlaqlaşdırması.
- `Prescription` (dəyişməz) və `AllergyChecker`.
- `ClinicalNote` (SOAP): qaralama → imza → dəyişməz, düzəliş addendum ilə.

**Endpoint-lər (`ClinicCore.Api`):** `/v1/visits` (+`/close`), `/v1/patients/{id}/odontogram` (GET, POST, `/{fdi}/history`, `?at=` ilə time travel), `/v1/patients/{id}/treatment-plans`, `/v1/treatment-plans/{id}` (+`/propose|accept|reject|cancel`, `/items/{id}/perform`), `/v1/clinical-notes` (+`PUT`, `/sign`), `/v1/prescriptions`, `/v1/procedure-codes`, plus pasiyent üzrə siyahılar.

**Miqrasiya `T008_clinical.sql`:** 18 prosedur kodlu kataloq, `prescriptions.override_reason`, DB qaydaları (aşağıda), plan bəndlərinə `row_version`.

**Başqa modullarla əlaqə (yalnız kontraktlarla):**
- `Scheduling.Contracts.IAppointmentLifecycle`: vizit başlayanda qəbul `in_progress`, bağlananda `completed` olur. Qəbul həmin pasiyentə və həkimə aid olmalıdır.
- `Patient.Contracts.IPatientDirectory.GetActiveAllergiesAsync`: resept yoxlaması üçün.
- Həkimin `own` scope-u "baxan həkim" əlaqəsi ilə həll olunur (Dilim 3).

### Əsas qərarlar

| Qərar | Səbəb |
|---|---|
| **Allergiya yoxlaması serverdədir.** Konflikt 422 `prescription.allergy_conflict` və konflikt siyahısı qaytarır. Keçmək üçün `overrideAllergyWarning=true` və ≥ 10 simvollu səbəb lazımdır, nəticə `prescription.allergy_override` kimi audit olunur | UI-ı keçib API-yə birbaşa yazmaq xəbərdarlığı atlamasın |
| Resept dəyişməzdir, `allergy_check_passed = false` olarsa səbəb DB `CHECK` ilə məcburidir | Səbəbsiz override mümkün deyil, hətta birbaşa SQL ilə də |
| Eyni diş+səth üçün yalnız **bir aktual qeyd** (DB unikal indeksi `ux_tooth_current_surface`). Bütün dişi (səthsiz) qeyd etmək səthlərin hamısını əvəz edir. Qeyd pasiyent üzrə advisory kilid altında tranzaksiyada yazılır | Paralel iki həkim eyni dişi dəyişəndə iki "aktual" qeyd qalmasın |
| Bir həkim–pasiyent üçün yalnız **bir açıq vizit** (`ux_visits_one_open`) | Yarışa qarşı DB qoruması, 409 cavabı mövcud vizitin id-sini qaytarır |
| Plan bəndi üçün **optimistic concurrency** (`row_version`). Prosedur yalnız həkimin öz açıq vizitində icra olunur | Eyni bəndi iki dəfə "icra olundu" etmək ikiqat faktura deməkdir |
| `ProcedurePerformed` hadisəsi **qiyməti, endirimi və sayı daşıyır** | Billing icra anındakı qiymətlə faktura yazır, plan sonradan dəyişsə də |
| Qaralama qeydlər yalnız müəllifə görünür və yalnız o dəyişə/imzalaya bilər. İmzadan sonra həm domen, həm DB trigger-i dəyişməyə icazə vermir | Klinik-hüquqi tələb: imzalanmış qeyd sübutdur |
| Audit-ə dərman adı, qeyd mətni və diş şərhi yazılmır, yalnız fakt və sayı | Audit log PHI anbarına çevrilməsin |
| Bütün klinik məlumat `Cache-Control: no-store` (odontoqram, reseptlər) | PHI brauzer/proxy keşinə düşməsin |
| Plan yalnız qaralamada redaktə olunur (`version` hələlik həmişə 1) | Təklif olunmuş planın sənəd kimi dəyişməməsi. Dəyişiklik yeni plan kimi yaradılır |

### Testlər (365 test, hamısı keçir)

| Layer | Say |
|---|---|
| Architecture | 7 |
| BuildingBlocks unit | 16 |
| Identity unit / integration | 44 / 16 |
| Patient unit | 64 |
| Scheduling unit | 67 |
| Clinical unit | 90 |
| ClinicCore integration (Patient + Scheduling + Clinical, real PostgreSQL və real HTTP host) | 61 |

Clinical inteqrasiya testləri (20): qəbuldan vizit və status keçidləri, 6 paralel "vizitə başla" (tam 1), səth üzrə əvəzlənmə və tarixçə, time travel, 6 paralel eyni dişə qeyd (tam 1 aktual), plan cəmləri və tam həyat dövrü və Billing-ə hazır hadisə yükü, 6 paralel eyni bəndin icrası (tam 1 faktura hadisəsi), ləğv olunmuş planda icra olunmuş bəndlərin qalması, allergiya konflikti/override/audit, DB `CHECK`, qaralama/imza/addendum və DB trigger-i, həkim `own` scope-u və baxan həkim əlaqəsi, tenant izolyasiyası, audit zənciri.

**Testlərin dişi olduğunu yoxladım (mutasiya):** bənd üçün optimistic concurrency-ni, sonra odontoqram advisory kilidini qəsdən söndürdüm. Hər dəfə uyğun paralel test qırmızı oldu, bərpadan sonra yenə yaşıl.

### Testlərin tapdığı real bug-lar (düzəldilib)

1. **Allergiya yoxlaması böyük "İ" ilə yayınırdı.** `AMOKSİSİLLİN` (nöqtəli İ) `ToLowerInvariant` ilə `i̇` (i + birləşən nöqtə) olur və `penisillin` allergiyası ilə uyğun gəlmirdi. Azərbaycan/türk klinikaları üçün real təhlükə. İndi diakritiklər Unicode FormD ilə silinir.
2. Test zamanı mənasız bir assertion də tapılıb düzəldildi (testin özündə səhv).

### Məlum məhdudiyyətlər (dürüst qeyd)

- **Allergiya yoxlaması sadə ad və kiçik dərman sinfi lüğətidir** (penisillin, sefalosporin, NSAID, lokal anesteziklər, lateks, makrolid və s.). Tam dərman məlumat bazası DEYİL, qarşılıqlı təsir (interaction) yoxlaması yoxdur. Bu klinik qərar dəstəyi vasitəsidir, həkimin mühakiməsini əvəz etmir. Faza 3-də dərman bazası və AI ilə genişlənəcək.
- **Perio chart, implant və ortodontik planlama, şəkil annotasiyası, before/after yoxdur** (spesifikasiyadakı Doctor modulunun bir hissəsi). Cədvəl (`perio_charts`) hazırdır, endpoint-lər yoxdur.
- **Voice-to-text və AI qaralama yoxdur.** `source=voice|ai_draft` yalnız saxlanılır, AI funksiyası Faza 3-dədir.
- **Plan bəndinin qiyməti əl ilə daxil edilir.** Billing modulunun qiymət siyahısı (`services`) ilə uyğunlaşma Dilim 5-də. Plan "estimate" faktura yaratmır (qəbul zamanı yalnız hadisə yazılır).
- **Plan redaktəsi yoxdur** (yalnız yarat, təklif et, qəbul/rədd, ləğv, icra). Qaralama bəndlərini dəyişmək üçün endpoint əlavə olunmalıdır.
- **Qəbul statusu vizitlə iki ayrı tranzaksiyadadır** (iki modulun iki DbContext-i). Vizit saxlanandan sonra qəbul `completed` edilir və uğursuz olarsa vizit yenə də bağlı sayılır. Tam atomiklik RabbitMQ saga-sı (Dilim 6) ilə gələcək.
- **Prosedur icrası odontoqramı avtomatik yeniləmir** (məs. plomb icrasından sonra dişin vəziyyəti). Həkim ayrıca qeyd edir.
- **Prosedurun materialları (stok)** hələ yoxdur, Inventory modulu Faza 2-dədir.
- Əvvəlki dilimlərin məhdudiyyətləri (2FA, JWKS, outbox publisher, audit partition job-u, Idempotency-Key, Testcontainers) qüvvədə qalır.

> Növbəti dilim: **Dilim 5a** (aşağıda, RabbitMQ), sonra **Billing**.

---

## Dilim 5a: Mesajlaşma (RabbitMQ publisher, inbox, consumer)

### Nə yazıldı

| Hissə | Fayl (`BuildingBlocks.Infrastructure/Messaging`) | Rolu |
|---|---|---|
| Zərf və müqavilələr | `Messaging.cs` | `IntegrationEnvelope` (id, type, version, tenantId, tenantSlug, routingKey, payload), `[Consumes]`, `IIntegrationConsumer`, `RabbitMqOptions` |
| Publisher | `RabbitMqPublisher.cs` | Bir kanal, publisher confirms, kəsilmədə təkrar qoşulma, persistent mesaj, `MessageId = outbox id` |
| Outbox processor | `OutboxProcessor.cs` | Hər tenant üçün `FOR UPDATE SKIP LOCKED` ilə paket götürür, göndərir, `processed_at` yazır; xətada `attempts+1` və exponential `next_attempt_at` (5 san · 2^cəhd, max 1 saat) |
| Inbox | `Inbox.cs` | `ExecuteOnceAsync`: inbox sətri + consumer təsiri EYNİ tranzaksiyada |
| Consumer host | `ConsumerHost.cs` | Topologiya (topic exchange `dental.events`, `dental.dead`, növbə `<prefix>.<ad>` + `.dlq`), prefetch, yerli təkrar cəhdlər, sonra DLQ |
| Maintenance | `Maintenance.cs` | Saatlıq: audit partition-ları (cari + 2 ay), işlənmiş outbox (7 gün), köhnə refresh token və change_log təmizliyi |
| Miqrasiya | `db/migrations/tenant/T009_outbox_retry.sql` | `outbox_messages.next_attempt_at` + indekslər |
| İlk consumer | `Patient.Infrastructure/NoShowConsumer.cs` | `scheduling.appointment-missed` → `patients.no_show_count + 1` (atomik SQL) |
| Müqavilə | `Scheduling.Contracts/IntegrationEvents.cs` | `AppointmentMissedV1`: Patient modulu Scheduling.Domain-ə istinad etmir |
| Host | `DentaCore.Workers/Program.cs` | Patient modulu, messaging, consumer-lər, publisher, maintenance |

### Əsas qərarlar

- **At-least-once + inbox = effektiv bir dəfə.** Broker və outbox təkrar çatdıra bilər; inbox `(message_id, consumer)` PK-sı dublikatı atır. Inbox və təsir bir tranzaksiyadadır: proses ortada ölərsə hər ikisi geri qaytarılır.
- **Göndərmə outbox tranzaksiyası daxilindədir.** Commit-dən əvvəl proses ölərsə mesaj təkrar göndərilir (dublikat, itki yox). Əksinə sıra (əvvəl commit, sonra göndər) mesajı itirərdi.
- **`SKIP LOCKED`** bir neçə Workers nüsxəsinin eyni sətri iki dəfə götürməməsini təmin edir və horizontal miqyaslanmanı açır.
- **Routing key tenant-sızdır, tenant zərfdədir.** Bir növbə bütün tenant-lara xidmət edir (10 000 tenant üçün 10 000 növbə olmur). Consumer tenant-ı `platform.tenants`-dan slug ilə tapır və scope-u həmin tenant-a bağlayır.
- **Topologiyanı consumer host `StartAsync`-də elan edir və publisher-dən ƏVVƏL qalxır.** Subscriber-i olmayan routing key-ə göndərilən mesaj broker-də itir; növbə əvvəlcədən bağlı olmalıdır. Hosted servislər ardıcıl başlayır və `StartAsync` bitmədən növbəti başlamır. (İlk variantda topologiya `ExecuteAsync`-də qurulurdu, yəni publisher ilə yarışırdı: Dilim 5b testləri bunu üzə çıxardı, aşağıya baxın.)
- **Zəhərli mesajlar itmir.** Oxunmayan JSON dərhal, tənzimlənən sayda uğursuz cəhddən sonra isə işlənə bilməyən mesaj DLQ-ya düşür (`dental.dead`), orada araşdırılır.
- **No-show artırması atomik SQL-dir** (`no_show_count = no_show_count + 1`), oxu-dəyiş-yaz deyil: pasiyent kartının paralel redaktəsi ilə konkurensiya istisnası yaranmır. `NoShowCount` EF mapping-indən `ValueGeneratedOnAdd` çıxarıldı ki, domen dəyəri DB default-u ilə qarışmasın.
- **Müqavilə testi:** Scheduling domen hadisəsinin real JSON-u `AppointmentMissedV1`-ə oxunur; sahə adı dəyişərsə test qırılır.

### Testlər (373 test, hamısı keçir; Dilim 5b sonrası 14 Workers testi)

Yeni `DentaCore.Workers.IntegrationTests` (8 test) **real PostgreSQL və real RabbitMQ** ilə işləyir (`DENTACORE_TEST_PG` və `DENTACORE_TEST_AMQP` təyin olunmayıbsa atlanır). Hər işə salma unikal exchange/növbə prefiksi alır və sonda təmizləyir.

| Test | Nəyi sübut edir |
|---|---|
| Outbox → broker | Zərfin bütün sahələri (id, type, tenant, routing key, payload), `MessageId`, persistent; `processed_at` yazılır |
| Uğursuz göndərmə | `attempts`, `last_error`, `next_attempt_at` gələcəkdə; backoff müddətində cəhd yoxdur; vaxt çatanda göndərilir və xəta təmizlənir |
| Max cəhd | Limitə çatmış sətir toxunulmaz qalır |
| Paralel processor-lar | 30 mesaj, iki processor: hər mesaj tam bir dəfə, hər iki processor iş görür |
| No-show consumer | Outbox-dan gələn mesaj sayı 1 edir; eyni mesaj broker-də iki dəfə təkrar çatdırılanda say artmır; fərqli mesaj sayılır |
| DLQ | JSON olmayan mesaj və tanınmayan tenant-lı mesaj `patient.no-show.dlq`-ya düşür |
| Müqavilə uyğunluğu | Domen hadisəsi JSON-u `AppointmentMissedV1`-ə düzgün oxunur |
| Maintenance | Audit partition-ları 2 ay irəli yaranır və təkrar işə salma idempotentdir |

**Mutasiya yoxlaması:** `NoShowConsumer`-dən inbox çıxarılanda duplicate testi qırılır (say 2-dən çox olur); kod bərpa olundu.

### Testin tapdığı səhv (test tərəfində)

- Partition yoxlaması `pg_class` ilə tenant sxemlərini qarışdırırdı (`t_demo` və `t_other` eyni ad). `to_regclass` ilə (search_path-ə uyğun) əvəz olundu.

### Məlum məhdudiyyətlər (dürüst qeyd)

- **Yalnız bir consumer var** (no-show). Billing, Notify, Search indexer növbəti dilimlərdə eyni mexanizmi istifadə edəcək.
- **Risk skoru (AI) yenilənmir**, yalnız `no_show_count`.
- **Broker əlçatmaz olarkən Workers açılışı** consumer host-da istisna atır və host dayanır (orkestrator yenidən başladır). Daha yumşaq yenidən qoşulma gələcək işdir. Publisher isə mesajı outbox-da saxlayıb təkrar cəhd edir.
- **Maksimum cəhdi aşmış outbox sətirləri** avtomatik xəbərdarlıq yaratmır; monitorinq qaydası (Mərhələ 9, Prometheus) lazımdır.
- **Outbox sorğusu hər tenant üçün ardıcıl işləyir.** Minlərlə tenant üçün tenant-ları paralelləşdirmək və ya LISTEN/NOTIFY ilə oyatmaq optimallaşdırmasıdır.
- Əvvəlki dilimlərin qalan məhdudiyyətləri (2FA, JWKS, Idempotency-Key, Testcontainers) qüvvədə qalır; **audit partition job-u və no-show consumer məhdudiyyətləri bu dilimlə bağlandı.**

> Dilim 5b-də tapılan və düzəldilən 5a xətası: **başlanğıc yarışı**. Consumer host topologiyanı `ExecuteAsync`-də (arxa fonda) qururdu, outbox publisher isə dərhal başlayırdı; yeni növbə hələ bağlanmamış exchange-ə göndərilən ilk mesaj broker-də itirdi və outbox onu "göndərilib" saymışdı. 5a-nın tək consumer-i yarışı təsadüfən qazanırdı. Düzəliş: topologiya `StartAsync`-də qurulur; regression testi (`Queues_are_declared_before_the_host_finishes_starting...`) host başlayan kimi növbələrin və dinləyicilərin mövcudluğunu yoxlayır.

---

## Dilim 5b: Billing

### Nə yazıldı

| Qat | Məzmun |
|---|---|
| Domen | `Invoice` (aggregate: sətirlər, yekunlar, issue/void, ödəniş, geri qaytarma, hadisələr), `InvoiceItem`, `Payment` (append-only), `CashShift`, `Service` (qiymət siyahısı), `Money` |
| Tətbiq | Qiymət siyahısı (siyahı, yarat, yenilə), faktura (yarat, oxu, siyahı, issue, void), ödəniş qəbulu, geri qaytarma, kassa smeni (aç, bağla, cari), `BillingAccess`, `IRefundLimits` |
| İnfrastruktur | `BillingDbContext`, `BillingRepository` (FOR UPDATE/SHARE kilidləri), `RefundLimits` (rolun `max_amount`), `ProcedurePerformedConsumer` |
| Host | `DentaCore.Billing.Api` (14 endpoint, `Idempotency-Key`, `If-Match`), Workers-də Billing consumer-i |
| Miqrasiya | `T010_billing.sql`: DB səviyyəsində pul qaydaları (aşağıda) |
| Müqavilə | `Clinical.Contracts.ProcedurePerformedV1` |

### Pul qaydaları (domenə və DB-yə eyni anda yazılıb)

- **Məbləğ ən çox 2 onluq mərtəbədir**; DB `numeric(14,2)` səssiz yuvarlaqlaşdırmasın deyə domen rədd edir.
- **Qiymətlər vergisizdir**: vergi = (sətir − endirim) × stavka, hər sətir üzrə yuvarlaqlaşdırılır (yarısı sıfırdan uzağa); yekun = cəm − endirim + vergi.
- **Qaralamada sətir əlavə olunur, issue-dan sonra sətirlər dəyişməzdir** (domen + `trg_invoice_items_draft_only` trigger-i: birbaşa SQL də rədd olunur).
- **Ödəniş qalıq borcu aşa bilməz** (`billing.overpayment`, cavabda cari qalıq) və DB-də `ck_invoices_no_overpay`.
- **Ödəniş sətirləri dəyişməzdir** (T006 trigger-i). Düzəliş = əks əməliyyat: `kind='refund'` + `refund_of` (`ck_payments_refund_link`).
- **Geri qaytarma ödənilmiş məbləği azaldır və borcu bərpa edir** (tam geri qaytarmadan sonra status yenə `issued`). Xidmət ləğv olunursa: əvvəl geri qaytar, sonra `void`. Statusu `refunded` hələlik istifadə olunmur.
- **Limit** rolun `role_permissions.max_amount` dəyərindən oxunur (NULL = limitsiz, 0 = hər məbləğ üçün rəhbər lazımdır), bir sorğu üzrə yoxlanılır. Aşılanda 403 `billing.refund_limit_exceeded` və `limit` cavabdadır.
- **Bir ödənişdən geri qaytarıla bilən = ödəniş − əvvəlki geri qaytarmalar** (`billing.refund_exceeds_payment`, cavabda `refundable`).
- **Hər vizit üçün ən çox bir qaralama faktura** (`ux_invoices_one_draft_per_visit`) və **bir plan bəndi iki dəfə fakturalanmır** (`ux_invoice_items_plan_item`).

### Konkurensiya və idempotentlik

- **Ödəniş, geri qaytarma, void**: tranzaksiya açılır, faktura sətri `SELECT … FOR UPDATE` ilə kilidlənir, sonra yenidən yüklənir. Kilid olmadan paralel iki ödəniş eyni köhnə qalığı görür (mutasiya testi sübut etdi). Qalıq/kilid üçün yoxlama izlənməyən (`AsNoTracking`) nüsxə ilə edilir ki, kilid gözləyəndən sonra köhnəlmiş izlənən obyekt işlənməsin.
- **Kassa smeni**: nağd ödəniş smen sətrini `FOR SHARE`, bağlama `FOR UPDATE` ilə kilidləyir. Bağlama ilə paralel nağd ödənişdə ya ödəniş smenə düşür və gözlənilən cəmə daxildir, ya smen bağlı olduğu üçün rədd olunur; itən pul olmur.
- **`Idempotency-Key` ödəniş və geri qaytarmada məcburidir** (başlıq yoxdursa 400). Eyni açar + eyni sorğu = eyni nəticə (ikinci pul çıxmır); eyni açar + fərqli sorğu = 409. Eyni açarla paralel sorğular `payments.idempotency_key UNIQUE` ilə ardıcıllaşır: itirən tərəf qalibin nəticəsini qaytarır.
- **Nağd ödəniş açıq smen tələb edir** (eyni filialda); kart/köçürmə tələb etmir və smen filialı fərqlidirsə kassaya yazılmır.

### Prosedur → qaralama faktura

`clinical.procedure-performed` hadisəsi vizitin qaralama fakturasına sətir əlavə edir (yoxdursa yaradır). Qiymət və endirim **icra anındakı plan bəndindən**, ƏDV və xidmət bağı `services.procedure_code` ilə qiymət siyahısından, ad isə xidmətin adından (yoxdursa prosedur adından) gəlir. Faktura avtomatik **issue olunmur**: bu insan qərarıdır (reception yoxlayıb təsdiqləyir). Faktura artıq issue olunubsa yeni hadisə yeni qaralama açır. Pasiyent tapılmasa mesaj təkrar cəhdlərdən sonra DLQ-ya düşür.

### Testlər (461 test, hamısı keçir; 0 xəbərdarlıq)

| Layihə | Test | Nəyi sübut edir |
|---|---|---|
| `Billing.UnitTests` | 46 | Yekunlar və vergi yuvarlaqlaşdırması, validasiya sərhədləri, status maşını, ödəniş/geri qaytarma/void qaydaları, smen fərqi, qiymət siyahısı validasiyası |
| `Billing.IntegrationTests` | 36 | Real PostgreSQL üzərində API: qiymət siyahısı + `If-Match`, qiymətlərin siyahıdan götürülməsi, issue-dan sonra dondurma (trigger), filial/tenant/own scope, cursor ilə səhifələmə (boşluq və təkrar yoxdur), overdue, kassa, limitli rollar, idempotentlik, audit zənciri bütövlüyü, DB-nin tətbiqi yan keçən SQL-ə qarşı müdafiəsi |
| `Workers.IntegrationTests` (+5) | 14 | Prosedur → bir qaralama faktura (qiymət, endirim, ƏDV, xidmət bağı), təkrar çatdırılma ikiqat fakturalamır, issue-dan sonra yeni qaralama, olmayan pasiyent DLQ-da, hadisə JSON-unun müqavilə ilə uyğunluğu, başlanğıc sırası |

**Konkurensiya testləri** (hamısı real paralel HTTP sorğuları): 10 paralel 60-lıq ödənişdən 100-lük fakturaya **tam biri** keçir; 5 paralel 20-lik ödəniş itkisiz 100 edir; eyni açarla 6 paralel sorğu **bir** ödəniş yaradır; ödəniş ilə void yarışında nəticə həmişə ya "ləğv, 0 pul", ya "50 ödənilib"; 8 paralel 30-luq geri qaytarmadan **tam 3-ü** keçir; smen bağlanarkən gələn nağd ödənişlər gözlənilən cəmlə uyğun qalır.

**Mutasiya yoxlamaları** (kod bərpa olundu): faktura `FOR UPDATE` kilidi çıxarılanda üç konkurensiya testi qırılır; smen kilidi `FOR UPDATE`-dən `FOR SHARE`-ə endiriləndə smen-bağlama testi 3/3 qırılır.

### Testlərin tapdığı real xətalar (düzəldilib)

- **Başlanğıc yarışı** (yuxarıda): topologiya publisher ilə yarışırdı.
- **Raw SQL alias-ları**: `SqlQuery<T>` rekord sahələrini `snake_case` sütunlarla uyğunlaşdırır (`service_id`, `vat_rate`); PascalCase alias consumer-i sındırırdı (Patient dilimindəki eyni dərs).
- **Test hesabı**: ƏDV endirimdən sonrakı məbləğdən hesablanır; gözlənilən yekun düzəldildi.

### Məlum məhdudiyyətlər (dürüst qeyd)

- **Faktura nömrəsi PostgreSQL sequence-dir** (`INV-YYYY-000001`): ləğv olunmuş tranzaksiyalarda boşluq yaranır və nömrə qaralamada təyin olunur (issue-da yox). Boşluqsuz rəsmi nömrələmə tələb edən ölkələr üçün issue anında ayrıca nömrələmə cədvəli lazımdır.
- **Qiymətlər vergisizdir** və qiymət siyahısında valyuta çevirməsi yoxdur: faktura tək valyutalıdır (qarışıq valyuta 422). Vergi-daxil qiymət rejimi və e-qaimə inteqrasiyası ayrıca iş.
- **Promo kod, sığorta, taksit, hədiyyə kartı və depozit (prepayment) hələ yoxdur** (sxemdə hazırdır). Bu üsullar 422 qaytarır. Promo/sığorta sahələri göndərilərsə də 422.
- **`POST /invoices` üçün `Idempotency-Key` hələ tətbiq olunmayıb** (OpenAPI-də var). Pul hərəkətləri (ödəniş, geri qaytarma) qorunur; faktura yaratmanın təkrarı ikinci qaralama yaradır. Vizit üçün isə unikal indeks ikinci qaralamanı rədd edir.
- **PDF endpoint-i yoxdur** (Faza 2).
- **Geri qaytarma limiti sorğu başınadır**, günlük/kumulyativ limit yoxdur.
- **Smen bağlayan başqasının smenini yalnız `payment:write@tenant` ilə bağlaya bilər**; smen transferi və kassa orderi (giriş/çıxış) yoxdur.
- **`VisitClosed` hadisəsi Billing tərəfindən istifadə olunmur**: qaralama prosedur hadisələri ilə yaranır. Vizit bağlananda avtomatik xatırlatma (faktura issue olunmayıb) Notify dilimində olacaq.
- **Hesabatlar, gəlir/borc dashboard-u və Accounting jurnalı** hələ yoxdur; `billing.payment-received` və digər hadisələr outbox-a düşür, dinləyən yoxdur.
- Əvvəlki dilimlərin qalan məhdudiyyətləri (2FA, JWKS, Testcontainers, SignalR, Elasticsearch) qüvvədə qalır.

> Növbəti dilim: **6 Audit publisher, Notify (SignalR) və Gateway (YARP)**: bütün host-ları bir giriş nöqtəsinə bağlayır, hadisələri canlı bildirişə çevirir. Davam etmək üçün **"Davam et"** yazın.
