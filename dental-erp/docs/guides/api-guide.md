# API bələdçisi

> Bu bələdçidəki **hər `curl` nümunəsi** `tests/docs/api-guide-examples.sh` skriptindən götürülüb və real stack-ə qarşı **icra olunur** (CI-nin e2e job-unda). Nümunə ilə skript arasında fərq olarsa `tests/docs/check-docs.py` düşür. Dəqiq sxemlər: [`api/openapi.yaml`](../../api/openapi.yaml) (OpenAPI 3.1). Gözlənilən fərqlər [§9](#9-openapi-ilə-real-davranış-arasındakı-fərqlər)-dadır.

## 1. Əsaslar

| Mövzu | Real davranış |
|---|---|
| Baza ünvan | `/v1/...`. Kənardan **yalnız Gateway** (və ya ingress) açıqdır; lokal: `http://127.0.0.1:5100` (`infra/dev/run-stack.sh start`) |
| Tenant | İstehsalda **subdomen** (`klinika.dentacore.app`). `X-Tenant` başlığı **yalnız dev/test**-də qəbul olunur (`Tenancy:AllowTenantHeader`), Gateway onu xaricdən silir. Tək-klinika qurulumunda `Tenancy:FixedTenant` hər sorğunu həmin klinikaya bağlayır |
| Autentifikasiya | `Authorization: Bearer <accessToken>` (RS256 JWT). Access token **10 dəqiqə** (`expiresIn: 600`), refresh token **7 gün**, hər yeniləmədə **dəyişir** (rotasiya) |
| Format | JSON (`camelCase`); yamalar `application/merge-patch+json`; vaxt ISO 8601, tarix `YYYY-MM-DD`; pul `number` (2 onluq) |
| Xəta | RFC 9457 `application/problem+json`: `type`, `title`, `status`, **`code`** (sabit maşın-oxunaqlı kod) + xətaya xas əlavə sahələr (§7) |
| İzləmə | `X-Request-Id` göndərsəniz eynilə qaytarılır (təhlükəsiz simvollarla), göndərməsəniz Gateway yaradır: dəstəyə müraciətdə bunu bildirin |

Aşağıdakı nümunələrdə `API`, `TENANT`, `TOKEN`, `BRANCH`, `PATIENT`, `INVOICE` və s. dəyişənlərdir: onlar əvvəlki addımların cavablarından götürülür (`TOKEN`/`REFRESH` giriş cavabından, `NEW_REFRESH` ən son yeniləmə cavabından, `PHONE` hər icrada təzə unikal telefon). Demo giriş məlumatları `DevSeed` aləti ilə yaranır (`admin@demo.test`); öz klinikanızda öz məlumatlarınızı istifadə edin.

## 2. Giriş, yeniləmə, çıxış

```bash
curl -X POST "$API/v1/auth/login" \
  -H "X-Tenant: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.test","password":"Passw0rd!e2e"}'
```

Cavab: `{"accessToken":"…","refreshToken":"…","expiresIn":600,"tokenType":"Bearer"}`. İki mərhələli doğrulama tələb olunan hesab üçün cavab `202 {"challengeId","methods"}` olur; **doğrulama endpoint-i hələ yoxdur** ([STATUS](../STATUS.md)).

Səhv parol həmişə eyni cavabı verir (hesabın olub-olmadığını bildirmir), ardıcıl uğursuz cəhdlərdən sonra hesab müvəqqəti bloklanır:

```bash
curl -X POST "$API/v1/auth/login" \
  -H "X-Tenant: $TENANT" \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@demo.test","password":"yanlis-parol"}'
```

Cari istifadəçi, rollar, **effektiv icazələr** və girişi olan filiallar:

```bash
curl "$API/v1/auth/me" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

Token olmadan `401` + `WWW-Authenticate: Bearer`:

```bash
curl "$API/v1/patients" \
  -H "X-Tenant: $TENANT"
```

Access token bitməmiş yeniləyin. **Köhnə refresh token bir dəfəlikdir**: ikinci dəfə istifadə olunsa bütün ailə ləğv olunur (oğurlanmış token zənciri). Brauzer tətbiqi bunu BFF vasitəsilə edir, refresh token-i JavaScript görmür ([security-and-privacy](security-and-privacy.md)).

```bash
curl -X POST "$API/v1/auth/refresh" \
  -H "X-Tenant: $TENANT" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$REFRESH\"}"
```

Çıxış refresh token-i ləğv edir:

```bash
curl -X POST "$API/v1/auth/logout" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"refreshToken\":\"$NEW_REFRESH\"}"
```

## 3. Pasiyentlər

```bash
curl -X POST "$API/v1/patients" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Nigar\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\",\"birthDate\":\"1990-05-01\",\"gender\":\"F\"}"
```

Cavab `201` + `Location`, gövdədə `id`, `chartNo`, `rowVersion` (ilk dəyər `1`). Telefon, e-poçt və FİN **default maskalıdır** (`+99455*******`).

**Doğrulama xətası** `422` (`code: validation.failed`; `title` ilk pozuntunu deyir, sahə-sahə `errors` siyahısı hələ yoxdur):

```bash
curl -X POST "$API/v1/patients" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"\",\"lastName\":\"Quliyeva\"}"
```

**Dublikat.** Eyni telefon və ya FİN varsa `409 patient.duplicate` + `existingPatientId`. Bu xəta deyil, qərardır (ailə üzvləri telefon paylaşır): təsdiq üçün `?confirmDuplicate=true`:

```bash
curl -X POST "$API/v1/patients" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Aysel\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\"}"
```

```bash
curl -X POST "$API/v1/patients?confirmDuplicate=true" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"firstName\":\"Aysel\",\"lastName\":\"Quliyeva\",\"phone\":\"$PHONE\"}"
```

**Oxumaq və optimistik kilid.** `GET` cavabında `ETag: "<rowVersion>"` var və `Cache-Control: no-store` (PHI keşlənmir). Dəyişdirmək üçün `PATCH` + **`If-Match`** məcburidir: olmasa `428`, köhnədirsə `412 concurrency.stale` (kimsə arada dəyişib: yenidən oxuyub təkrar edin):

(`-D` cavab başlıqlarını fayla yazır ki, `ETag` görünsün.)

```bash
curl -D "$WORK/headers" "$API/v1/patients/$PATIENT" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"

curl -X PATCH "$API/v1/patients/$PATIENT" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/merge-patch+json" \
  -d '{"fatherName":"Elçin"}'

curl -X PATCH "$API/v1/patients/$PATIENT" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "If-Match: \"99\"" \
  -H "Content-Type: application/merge-patch+json" \
  -d '{"fatherName":"Elçin"}'
```

```bash
curl -X PATCH "$API/v1/patients/$PATIENT" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "If-Match: \"1\"" \
  -H "Content-Type: application/merge-patch+json" \
  -d '{"fatherName":"Elçin"}'
```

**Maskasız məlumat** (`patient:read_sensitive` lazımdır; hər çağırış auditə `patient.read.sensitive` kimi yazılır):

```bash
curl "$API/v1/patients/$PATIENT?reveal=true" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

**Siyahı və kursor.** Cavab `{"items":[…],"nextCursor":"…"|null}`; `nextCursor` null olana qədər onu `cursor=` ilə geri verin:

```bash
curl "$API/v1/patients?limit=2" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"

curl "$API/v1/patients?limit=2&cursor=$CURSOR" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

**Axtarış.** İki yol var və fərqlidir:

```bash
curl "$API/v1/patients?q=Quliyeva" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"

curl "$API/v1/search/patients?q=Quliyeva" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

`/search/patients` Elasticsearch istifadə edir (yazdıqca, hərf səhvinə dözümlü, `ə/ı/ö/ü/ş/ç/ğ` qatlanır; ad, soyad, ata adı, kart №). Mühərrik əlçatmazdırsa `503` qaytarır: klient **adi `/patients?q=`-ya keçməlidir** (veb tətbiq bunu avtomatik edir). Adi axtarış telefon və FİN-i yalnız **dəqiq** uyğunluqla (şifrəli indeks) tapır.

## 4. Qəbullar

Boş slotlar (iş qrafiki, məzuniyyət və mövcud qəbullar nəzərə alınır; addım 15 dəq):

```bash
curl "$API/v1/appointments/availability?providerId=$DOCTOR&date=$TODAY&durationMin=30" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

Qəbul yaratmaq. Eyni həkim eyni vaxtda iki qəbul ala bilməz: bu qayda **verilənlər bazası səviyyəsindədir** (yarış halında da pozulmur), pozulanda `409 appointment.overlap` və **`alternativeSlots`** gəlir:

```bash
curl -X POST "$API/v1/appointments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"patientId\":\"$PATIENT\",\"providerId\":\"$DOCTOR\",\"start\":\"$SLOT_START\",\"end\":\"$SLOT_END\",\"reason\":\"Yoxlama\"}"
```

```bash
curl -X POST "$API/v1/appointments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"branchId\":\"$BRANCH\",\"patientId\":\"$PATIENT\",\"providerId\":\"$DOCTOR\",\"start\":\"$SLOT_START\",\"end\":\"$SLOT_END\"}"
```

Check-in qəbula **2 saat qalmışdan** mümkündür, növbə bileti (`ticketNo`) qaytarır:

```bash
curl -X POST "$API/v1/appointments/$APPT/check-in" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

Digər vəziyyət keçidləri: `POST /appointments/{id}/cancel`, `POST /appointments/{id}/no-show`; vaxt/həkim/otaq dəyişmək `PATCH` (+ `If-Match`). Təkrarlanan qəbullar (`recurrenceRule`) **dəstəklənmir** (sahə qəbul edilir, nəzərə alınmır).

## 5. Faktura və ödəniş

```bash
curl "$API/v1/services" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

```bash
curl -X POST "$API/v1/invoices" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{\"kind\":\"invoice\",\"patientId\":\"$PATIENT\",\"branchId\":\"$BRANCH\",\"items\":[{\"serviceId\":\"$SERVICE\",\"quantity\":1}]}"
```

Fakturanı vermək (`draft` → `issued`):

```bash
curl -X POST "$API/v1/invoices/$INVOICE/issue" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

**Ödəniş.** `Idempotency-Key` bu endpoint-də **məcburidir** (başlıq olmasa `400 idempotency.key_required`). Eyni açarla təkrar sorğu **eyni nəticəni** qaytarır və ikinci ödəniş yaratmır: şəbəkə kəsilsə eyni açarla təkrar etmək təhlükəsizdir. Açar mütləq unikal olmalıdır (UUID), çünki **verilənlər bazasında əbədi unikaldır**:

```bash
curl -X POST "$API/v1/invoices/$INVOICE/payments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $IDEM" \
  -d '{"method":"card","amount":30}'

curl -X POST "$API/v1/invoices/$INVOICE/payments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $IDEM" \
  -d '{"method":"card","amount":30}'
```

```bash
curl -X POST "$API/v1/invoices/$INVOICE/payments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"method":"card","amount":10}'
```

Qalıqdan çox ödəniş `422 billing.overpayment` + `balance`. Nağd ödəniş üçün istifadəçinin **açıq kassa smeni** olmalıdır (`billing.shift_required`); kart/POS/köçürmə smensiz keçir. Mümkün üsullar: `cash`, `card`, `pos`, `transfer`.

```bash
curl -X POST "$API/v1/invoices/$INVOICE/payments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuid)" \
  -d '{"method":"card","amount":999}'
```

```bash
curl "$API/v1/invoices/$INVOICE" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

Geri qaytarma: `POST /payments/{id}/refund` (+ `Idempotency-Key`), `invoice:refund` icazəsi və məbləğ limiti (`role_permissions.max_amount`) tələb edir. Ödənişlər **dəyişməzdir** (silmək/redaktə yox, düzəliş = əks əməliyyat).

## 6. İdarə paneli və icazələr

```bash
curl "$API/v1/dashboard/summary" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $TOKEN"
```

İcazəsi olmayan rol `403` alır:

```bash
curl -X POST "$API/v1/invoices/$INVOICE/payments" \
  -H "X-Tenant: $TENANT" \
  -H "Authorization: Bearer $DOCTOR_TOKEN" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: $(uuid)" \
  -d '{"method":"card","amount":5}'
```

İcazə **iki oxlu**dur: *nə* (`invoice:read`, `patient:write` …) və *harada* (`own` < `branch` < `tenant`). Başqa filialın və ya başqa tenant-ın qeydi **404** qaytarır (varlığını bildirmir).

## 7. Xəta kodları

Xəta cavabı həmişə `application/problem+json`-dur; `code` sabitdir və klientin məntiqi buna əsaslanmalıdır (`title` dəyişə bilər). Əsas kodlar:

| Status | `code` | Mənası / ekstra sahə |
|---|---|---|
| 400 | `idempotency.key_required` | Ödəniş/refund `Idempotency-Key`-siz |
| 401 | `auth.invalid_credentials`, `auth.invalid_refresh_token` | Giriş/yeniləmə uğursuz |
| 401/403 | `auth.account_locked`, `auth.account_disabled`, `auth.ip_not_allowed` | Hesab/şəbəkə məhdudiyyəti |
| 403 | `tenant.mismatch`, icazə xətaları | Token başqa tenant-ındır / icazə yoxdur |
| 404 | `tenant.not_found`, `*.not_found` | Tenant yoxdur / qeyd yoxdur və ya görünməzdir |
| 409 | `patient.duplicate` (+`existingPatientId`), `appointment.overlap` (+`alternativeSlots`), `appointment.checkin_window`, `billing.shift_closed` | Biznes toqquşması |
| 412 / 428 | `concurrency.stale` / `precondition.required` | `If-Match` köhnə / yoxdur |
| 422 | `validation.failed`, `billing.overpayment` (+`balance`), `billing.shift_required`, `appointment.outside_schedule`, `appointment.in_the_past` … | Doğrulama / biznes qaydası |
| 429 | (Gateway) | Sürət limiti; `Retry-After: 60` |
| 503 | `search.unavailable` | Axtarış mühərriki əlçatmaz: adi axtarışa keçin |

Tam siyahı üçün `api/openapi.yaml` və mənbədə `Error.*("kod", …)` çağırışları.

## 8. Real vaxt (SignalR)

`/hubs/notifications` (Gateway üzərindən). Token `accessTokenFactory` ilə verilir (WebSocket üçün `?access_token=`). Qruplar **serverdə token claim-lərindən** hesablanır: klient hansı qrupu dinləyəcəyini seçə bilmir. Hadisə yalnız **"nə dəyişdi"** daşıyır (id-lər), ad/məbləğ yox; klient müvafiq sorğunu yeniləməlidir. Hadisə sahələri: `event`, `entityId`, `patientId`, `branchId`, `at`. Token bitəndə server bağlantını bağlayır: yeni token ilə yenidən qoşulun.

## 9. OpenAPI ilə real davranış arasındakı fərqlər

Bunlar bilinən uyğunsuzluqlardır ([STATUS](../STATUS.md)-da izlənir); real davranış yuxarıdakı kimidir:

| Sənəd/sxem deyir | Real |
|---|---|
| `Idempotency-Key` yaratma əməliyyatlarında (`POST /patients`, `/invoices`, `/appointments`) eyni cavabı qaytarır | **Yalnız ödəniş və refund-da** işləyir. Digərlərində başlıq **nəzərə alınmır**: eyni açarla iki sorğu iki qeyd yaradır (pasiyentdə dublikat yoxlaması, qəbuldə üst-üstə düşmə qaydası qismən qoruyur, fakturada yox) |
| Açar 24 saat yaşayır | Ödənişdə açar **əbədi** unikaldır (`payments.idempotency_key UNIQUE`) |
| Xəta cavabında `traceId` və `errors` (sahə üzrə) | Sxemdə var, **hələ doldurulmur** |
| `RateLimit-*` başlıqları, istifadəçi başına limit | **IP başına** sabit pəncərə (`/v1/auth/*`: 30/dəq, qalanı 600/dəq, konfiqurasiya olunur), cavab `429` + `Retry-After: 60`, `RateLimit-*` başlıqları yoxdur |
| `/auth/me`: `specialty`, `locale` | Qaytarılmır (sxemdən çıxarılıb) |
| `/admin/users`, `/auth/2fa/verify`, `/auth/sessions`, sənəd yükləmə, razılıq imzası, `treatment-plans/{id}/accept` və s. | **Hələ həyata keçirilməyib** (sxemdə var) |

## 10. Nümunələri özünüz işlədin

```bash
infra/dev/run-stack.sh start                 # PG + RabbitMQ lazımdır; infra/dev/services.sh onları qaldırır
tests/docs/api-guide-examples.sh             # ~60 təsdiq; uğursuz olsa hansı addım olduğunu yazır
```

Statik HTML istinad üçün: `npx @redocly/cli build-docs api/openapi.yaml -o /tmp/api.html`.
