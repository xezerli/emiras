# Təhlükəsizlik və məxfilik

> Bu sənəd **kodda və testlərdə təsdiq olunmuş** vəziyyəti təsvir edir; hüquqi uyğunluq (məsələn Azərbaycanın şəxsi məlumatlar haqqında qanunvericiliyi, GDPR, HIPAA) **qiymətləndirilməyib** və hüquqşünas baxışı tələb edir. Boşluqlar §9-da, qalan məhdudiyyətlər [STATUS](../STATUS.md)-dadır. Zəiflik bildirmək üçün: [SECURITY.md](../../SECURITY.md).

## 1. Məlumat təsnifatı: nə harada və necə qorunur

| Məlumat | Saxlanma | Qoruma | Qeyd |
|---|---|---|---|
| Parol | `users.password_hash` | **Argon2id** (m=19 MiB, t=2, p=1), hər parolun öz duzu; yoxlama sabit vaxtlı | Mövcud olmayan e-poçt üçün də eyni vaxt xərclənir (hesabın varlığı sızmır) |
| Refresh token | `refresh_tokens` | Yalnız **SHA-256 heşi** saxlanır; rotasiya, təkrar istifadə bütün ailəni ləğv edir | |
| TOTP sirri | `users.totp_secret_enc` | AES-256-GCM | 2FA axını hələ tamamlanmayıb |
| Telefon, e-poçt, **FİN** | `patients.*_enc` | **AES-256-GCM** (sahə səviyyəsində) + axtarış üçün **HMAC-SHA256 blind index** (`*_hash`) | API-də default **maskalı**; açmaq ayrıca icazə + audit |
| Ad, soyad, ata adı, doğum tarixi, cins | `patients` | **Şifrəsiz** (axtarış üçün) | Elasticsearch-də yalnız ad sahələri indeksləşir |
| **Klinik məzmun**: SOAP qeydləri, allergiyalar, diaqnoz, odontoqram, resept | `clinical_notes`, `allergies`, … | **Sahə səviyyəsində şifrələnmir.** Giriş nəzarəti + audit + (operatorun təmin etdiyi) disk/volume şifrələməsi | **Boşluq** (§9) |
| Ödənişlər | `payments` | Dəyişməz (DB trigger-i silmə/redaktəni qadağan edir) | |
| Audit | `audit_log` | Append-only (trigger), **hash zənciri** (`prev_hash`), aylıq partisiya | Bütövlük yoxlayıcı `IAuditChainVerifier` |
| Ehtiyat nüsxə | PVC/S3 | **age** ilə şifrəli (açıq açar yazır, gizli açar yalnız bərpada) + SHA-256 manifest | [09-deployment](../09-deployment.md) §8 |

PII açarları (`Security:PiiEncryptionKey`, `PiiHashKey`) **yalnız mühit dəyişəni/Secret**-dədir, repoda yoxdur (Architecture testi commit olunan konfiqurasiyada sirr axtarır). **Açar rotasiyası yoxdur.**

## 2. Kimlik doğrulama və sessiya

- JWT **RS256**, access token 10 dəq. İmzalama açarını **yalnız Identity** servisi görür; digərləri açıq açarla yoxlayır (Helm testi bunu təsdiq edir). `alg:none`/dəyişdirilmiş token rədd olunur (inteqrasiya testi).
- Token claim-ləri: `sub`, `tid` (tenant), `role`, `perm` (`kod@scope`), `branch`. **Tenant claim guard**: token başqa tenant-ın host-unda işləmir (`tenant.mismatch`).
- **Brute-force:** Identity-də IP başına limit, hesabda ardıcıl uğursuzluqdan sonra müvəqqəti kilid (5 cəhd / 15 dəq), Gateway-də `/v1/auth/*` üçün ayrıca limit.
- Hesab üzrə **IP icazə siyahısı** (`users.allowed_ips`) dəstəklənir.
- **Brauzer (BFF):** refresh token yalnız `HttpOnly; SameSite=Strict; Path=/api/session` (+ prod-da `Secure`) cookie-dədir, ömrü 7 gün; access token **yalnız yaddaşdadır** (localStorage/sessionStorage-da JWT yoxdur: e2e yoxlayır). BFF route-ları **Origin yoxlayır** (cross-origin → 403, upstream çağırılmır).
- Çıxış refresh token-i serverdə ləğv edir; ləğv olunmuş token işləmir (nümunə: [api-guide](api-guide.md) §2).

## 3. İcazə modeli

İki ox: **nə** (`patient:read`, `invoice:refund` …) və **harada** (`own` < `branch` < `tenant`); əlavə olaraq pul əməliyyatları üçün **məbləğ limiti** (`role_permissions.max_amount`).

- Hər əmr/sorğu mediator pipeline-ında icazə yoxlamasından keçir (`IRequiresAccess`): yoxlama **serverdədir**, UI menyusu yalnız rahatlıqdır.
- Başqa filialın/tenant-ın qeydi **404** qaytarır (varlığı sızmır), 403 yox.
- **Default-deny:** hər host üçün avtorizasiyasız marşrutların siyahısı testdə dəqiq sabitlənib (`GET /health`, `/health/ready`; Identity-də `login`, `refresh`). Yeni endpoint `RequireAuthorization()`-sız qalsa test düşür.
- Hazırkı rol icazələri: [user-guide §2](user-guide.md#2-naviqasiya-və-rollar). **5 rolun icazəsi boşdur.**

## 4. Tenant izolyasiyası

- **Schema-per-tenant** (`t_<slug>`) + `platform` sxemi. Hər sorğuda tenant host-dan (və ya `FixedTenant`-dan) təyin olunur, `NpgsqlDataSource` həmin sxemə `search_path` ilə bağlanır.
- `X-Tenant` yalnız dev/test-də qəbul olunur; Gateway onu və `X-Forwarded-*`-i xaricdən gələndə silir/əzir.
- Elasticsearch indeksləri **tenant üzrə ayrıdır** (`dc-patients-<tenant>`), audit indeksi aylıq.
- Outbox/inbox/sync/indexer hər tenant üçün ayrıca işləyir; hadisə zərfində tenant slug-ı var və consumer onu doğrulayır.
- **Məhdudiyyət:** bütün tenant-lar **eyni verilənlər bazası rolu** ilə işləyir. Sxem sərhədi tətbiq qatında təmin olunur (testlərlə sınanıb), PostgreSQL səviyyəsində rol/RLS ilə **ikinci qat yoxdur** (RLS yalnız `platform.login_directory`-dədir). Bax [ADR-0004](../adr/0004-schema-per-tenant.md).

## 5. PHI-nin yayılma yolları və nəzarət

| Kanal | Nəzarət |
|---|---|
| API cavabı | Telefon/e-poçt/FİN default maskalı; `reveal=true` ayrıca icazə (`patient:read_sensitive`) və hər açılış auditdə; PHI cavabları `Cache-Control: no-store` |
| UI | Açıq məlumat 30 s-dən sonra avtomatik maskalanır |
| Log | `Microsoft.AspNetCore` səviyyəsi Warning (sorğu sətri, yəni axtarış mətni log-lanmır): **Architecture testi** yoxlayır; JSON log |
| Metrika/iz | Tenant adı etiket deyil; OTel Collector `url.query`, `db.statement` kimi atributları silir (konfiqurasiya validasiya olunub, davranış ayrıca sınanmayıb) |
| Axtarış indeksi | Yalnız ad sahələri və kart №; əlaqə məlumatı/qeyd **indeksləşmir** (test yoxlayır) |
| Real vaxt (SignalR) | Bildiriş yalnız `event` + id-lər daşıyır, ad/məbləğ yox; qrup üzvlüyü serverdə token claim-lərindən hesablanır |
| Ehtiyat nüsxə | age ilə şifrəli, açıq açarla yazılır |
| Sync API | Eyni icazə və scope qaydaları; offline klient hələ yoxdur |

## 6. Audit

Yazılan əməliyyatlar: pasiyent yaratma/dəyişmə/silmə/**axtarış**/**oxu** (maskalı və maskasız ayrı), allergiya, qəbul əməliyyatları, klinik qeyd (oxu/yenilə/imza), faktura, ödəniş, refund, smen, resept siyahısı, auditin özünə baxış (`audit.search`), sync push. Cədvəl **append-only** (trigger), hər sətir əvvəlkinin heşini daşıyır. Audit hadisəsi eyni tranzaksiyada outbox-a yazılır və Elasticsearch-ə indekslənir (`GET /v1/admin/audit`, `audit:read`). Audit cavabı **kim/nə/nə vaxt** göstərir, detalları (dəyişən məzmunu) yox.

## 7. Şəbəkə və infrastruktur

TLS ingress-də bitir (cert-manager/wildcard sertifikat sizin işinizdir). Kubernetes: bütün pod-lar non-root, `readOnlyRootFilesystem`, bütün capability-lər atılıb, `seccomp: RuntimeDefault`, token mount yoxdur; **NetworkPolicy** default-deny ingress, backend-lərə yalnız Gateway çata bilir; Secret-ləri chart yaratmır (Helm testi). Detallar: [09-deployment](../09-deployment.md).

## 8. Asılılıq və kod təhlükəsizliyi

CI-də hər PR: `pnpm audit --prod`, `dotnet list package --vulnerable --include-transitive`, Trivy (sirr + IaC), CodeQL (C#, JS/TS). Bunlar real nəticə verir: bu mərhələdə **iki real advisory** tutuldu və düzəldildi (SignalR.Common test-only tranzitiv, `next` 16.4.0: iki kritik RCE + bir DoS, 16.4.3-ə qaldırıldı).

## 9. Bilinən boşluqlar (təhlükəsizlik baxımından, ciddilik sırası ilə)

1. **Klinik məzmun sahə səviyyəsində şifrələnmir** (yalnız əlaqə məlumatı və FİN). Disk şifrələməsi operatordan tələb olunur.
2. **PII və JWT açar rotasiyası yoxdur**; JWKS yoxdur. Açar sızması hadisəsində plan yalnız əl ilə prosedurdur.
3. **Veb-də Content-Security-Policy yoxdur** (yalnız `nosniff`, `X-Frame-Options`, `Referrer-Policy`, `Permissions-Policy`). HSTS ingress-ə həvalədir.
4. **Tək DB rolu** bütün tenant-lar üçün (§4).
5. **2FA axını tamamlanmayıb** (giriş `202` qaytarır, təsdiq endpoint-i yoxdur): 2FA-lı hesab girə bilmir.
6. **Pasiyent silmə (GDPR "unudulma hüququ") yoxdur:** yalnız soft-delete. Audit dəyişməzdir, ona görə silmə iş axını ayrıca dizayn tələb edir (sətir əvəzinə anonimləşdirmə).
7. **Məlumat ixracı** (`patient:export` icazəsi var, endpoint yoxdur) və **razılıq (consent) imzalama** yoxdur.
8. **Saxlama müddəti siyasəti** yalnız texniki cədvəllər üçündür (`change_log`, sync, inbox); klinik/maliyyə qeydləri üçün qanuni saxlama müddəti təyin olunmayıb.
9. `Idempotency-Key` yalnız ödəniş və refund-da işləyir ([api-guide §9](api-guide.md#9-openapi-ilə-real-davranış-arasındakı-fərqlər)).
10. Sürət limiti **IP başınadır** (NAT arxasındakı klinika bir IP kimi sayılır); istifadəçi başına limit yoxdur.
11. Penetrasiya testi, DAST və xarici audit **aparılmayıb**.

## 10. İş axınları üçün qısa təhlükəsizlik checklist-i (yeni kod yazanlara)

- Yeni endpoint: `RequireAuthorization()` + əmr/sorğuda `IRequiresAccess` (permission + scope). Default-deny testi qoruyur.
- Yeni PII sahəsi: `IPiiProtector` ilə şifrələ, axtarış lazımdırsa `BlindIndex`; API-yə **maskalı** çıxar.
- Yeni log: sorğu gövdəsi, query string və PHI yazma; kod və id-lər kifayətdir.
- Yeni miqrasiya: additiv, bax [developer-guide](developer-guide.md#5-resept-yeni-miqrasiya).
- Yeni asılılıq: `Directory.Packages.props`/`pnpm`-də sancılır; CI auditi qırmızı olsa merge etmə.
