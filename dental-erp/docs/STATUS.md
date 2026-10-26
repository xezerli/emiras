# DentaCore: real vəziyyət

> Bu sənəd **nə işləyir, nə yoxdur, nə yoxlanmayıb** sualına dürüst cavabdır. Marketinq deyil; qərar verən (alıcı, investor, yeni developer) üçündür. Hər "yoxlanıb" iddiası bir testə və ya əmrə bağlıdır.

## 1. Bir cümləlik xülasə

Tək klinika üçün **işləyən vertikal kəsik** (giriş → pasiyent → qəbul → klinik qeyd → faktura → ödəniş → panel) real PostgreSQL/RabbitMQ ilə qurulub və testlərlə sübut olunub. İlkin spesifikasiyadakı geniş ERP-nin **çox hissəsi hələ yazılmayıb** (§3). Hazırkı kod **pilot** üçün əsasdır, tam komersiya məhsulu deyil.

## 2. İşləyir və yoxlanıb

| Sahə | Nə var | Sübut |
|---|---|---|
| Identity | Giriş, JWT RS256, refresh rotasiyası + təkrar istifadədə ailənin ləğvi, kilid, IP siyahısı, çıxış | İnteqrasiya testləri, [api-guide](guides/api-guide.md) 58 yoxlama (canlı stack) |
| Multi-tenancy | Schema-per-tenant, subdomen/FixedTenant/dev başlığı, tenant claim guard | İnteqrasiya testləri, `db/test-migrate.sh` |
| Pasiyent | Qeydiyyat (dublikat xəbərdarlığı), PII şifrələmə + maskalama, axtarış (ES, 503 → SQL), allergiya, ETag/If-Match | Unit + inteqrasiya + e2e |
| Qəbul | Təqvim, double-booking qorunması, boş slotlar, status keçidləri, növbə | Unit + inteqrasiya + e2e |
| Klinik | Vizit, odontoqram, müalicə planı, resept (allergiya yoxlaması), imzalanan qeyd | Unit + inteqrasiya |
| Maliyyə | Qiymət siyahısı, faktura, ödəniş (idempotent), refund (limitli), kassa smeni, dəyişməz ödənişlər | Unit + inteqrasiya + e2e |
| Hadisələr | Transactional outbox → RabbitMQ → inbox/consumer; SignalR bildirişləri | İnteqrasiya, yük testi (outbox boşalması) |
| Audit | Append-only, hash zənciri, Elasticsearch indeksi | İnteqrasiya |
| Offline sync (server) | Pull/snapshot/push, konflikt həlli | İnteqrasiya |
| Veb | Giriş, panel, pasiyentlər, təqvim, faktura/kassa; BFF cookie; WCAG 2.2 AA (axe, açıq+tünd) | Vitest + Playwright (13 e2e real backend ilə) |
| Deploy | Dockerfile-lar, compose, Helm (36/36 kubeconform), `migrate.sh`, age ilə şifrəli backup + bərpa təlimi, Prometheus qaydaları (promtool), OTel Collector konfiqurasiyası | Əmrlər Mərhələ 9-da işlədilib |
| CI | 9 yoxlama; `ci` və `codeql` real GitHub runner-lərdə yaşıl | PR #1 |
| Asılılıqlar | `pnpm audit` və NuGet zəiflik yoxlaması təmiz (Mərhələ 10-da `next` 16.4.3-ə qaldırıldı) | CI `security` işi |

Say (Mərhələ 8–9-da ölçülüb, hər dəfə yenidən ölçülməyib): backend 674 test (646 keçir, 28 Elasticsearch tələb edir), frontend 91 unit (api-client 13, ui 12, web 66), 13 e2e.

## 3. Yazılmayıb (spesifikasiyada var)

Flutter mobil, .NET MAUI desktop, AI modulları, Laboratoriya, Radiologiya, HR/Əmək haqqı, Mühasibatlıq, CRM/marketinq, Anbar/inventar, sığorta, e-qaimə/vergi inteqrasiyaları, ödəniş şlüzləri, SMS/WhatsApp/e-poçt göndərişi (bildiriş **planlaşdırılır**, göndərmə inteqrasiyası yoxdur), çoxdilli UI (yalnız Azərbaycan dili), offline klient UI-ı (server tərəfi var).

## 4. Qismən / bilinən boşluqlar

1. **2FA təsdiq endpoint-i yoxdur:** 2FA aktiv hesab girə bilmir.
2. **Açar rotasiyası yoxdur** (JWT/JWKS və PII açarı).
3. **Klinik məzmun şifrələnmir** (yalnız əlaqə məlumatı və FİN).
4. **5 rolun icazəsi boşdur** (manager, hygienist, lab, radiology, finance); kassirin refund limiti 0. **İstifadəçi/rol idarəetmə UI-ı və API-si yoxdur**: istifadəçilər bazada/seed ilə yaradılır.
5. `Idempotency-Key` yalnız ödəniş və refund-da; kontrakt əvvəl daha geniş vəd edirdi (Mərhələ 10-da düzəldildi).
6. Xəta cavablarında `traceId` yoxdur; `RateLimit-*` başlıqları yoxdur; limit IP başınadır.
7. UI: dublikat xəbərdarlığı mövcud pasiyentə keçid vermir; ES olmadan telefon/FİN axtarışı UI-da etibarlı deyil.
8. Pasiyent silmə / anonimləşdirmə, məlumat ixracı, razılıq (consent) iş axını yoxdur; klinik/maliyyə qeydləri üçün saxlama siyasəti təyin olunmayıb.
9. Veb-də CSP başlığı yoxdur.
10. Gateway telemetriyası yoxdur (digər host-larda OTLP opt-in).

## 5. Yoxlanmayıb

- **Heç bir konteyner imicı qurulmayıb** (sandbox-da Docker daemon yoxdur); Dockerfile-lar `hadolint` ilə yoxlanıb, build olunmayıb. `release.yml` heç vaxt işləməyib.
- Helm chart real klasterə qurulmayıb (şablonlar render və sxemlə yoxlanıb).
- **PITR və RTO/RPO ölçülməyib**; backup bərpa təlimi məntiqi nüsxə ilə işləyir.
- Yük testi bir maşında, kiçik həcmlə aparılıb; istehsal həcmi, çoxlu tenant yükü ölçülməyib.
- Penetrasiya testi, DAST, xarici audit aparılmayıb. Hüquqi uyğunluq (Azərbaycan qanunvericiliyi, GDPR, HIPAA) qiymətləndirilməyib.
- Real brauzerdə yalnız Chromium; Safari/Firefox və mobil brauzerlər sınanmayıb.
- `next dev` ilə tam axın ayrıca yoxlanmayıb (yoxlanılan yol: build + start).

## 6. Növbəti addımlar (təklif, prioritet sırası ilə)

1. Rol/istifadəçi idarəetməsi + boş rolların icazələri (pilot üçün bloker).
2. Real mühitdə (Docker/K8s) imic build + `release.yml` + staging quraşdırması + backup bərpa təlimi.
3. 2FA tamamlanması, açar rotasiyası, CSP.
4. Klinik məzmun şifrələməsi və pasiyent anonimləşdirmə axını.
5. SMS/e-poçt göndərişi (xatırlatmalar), sonra Laboratoriya/Anbar/Mühasibatlıq modulları.
