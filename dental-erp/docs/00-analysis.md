# Mərhələ 0 — Sistem Analizi (Dental Clinic ERP "DentaCore")

> Status: ✅ Tamamlandı. Növbəti: Mərhələ 1 (Architecture) — `01-architecture.md`

## 1. Məhsulun mövqeyi

| Rəqib | Güclü tərəf | Zəif tərəf (bizim fürsət) |
|---|---|---|
| Dentrix / Eaglesoft | Bazar payı, dərin klinik funksiyalar | Köhnə desktop UI, yavaş, cloud zəif |
| Open Dental | Açıq, genişlənə bilən | UI köhnə, UX mürəkkəb |
| Curve / Denticon / CareStack | Cloud-native, multi-location | Bahalı, AI məhdud, lokal server yoxdur |

**Fərqləndirici üstünlüklər (USP):**
1. **Hybrid deploy** — eyni kod bazası ilə Cloud SaaS və Local Server (offline-first).
2. **AI-native** — səsdən qeyd, avtomatik müalicə planı, rentgen analizi, no-show proqnozu.
3. **Minimal klik UX** — qəbul → odontoqram → plan → faktura bir axında, ≤3 klik.
4. **Region uyğunluğu** — AZ/TR/RU/EN dil, AZN/USD/EUR/TRY, ƏDV, e-qaimə, yerli SMS/WhatsApp provayderləri.

## 2. Aktorlar və rollar (RBAC əsası)

| Rol | Əsas məsuliyyət | Kritik icazələr |
|---|---|---|
| SuperAdmin | Platforma (bütün tenantlar) | Tenant yaratma, plan/billing, global monitorinq |
| Direktor | Bütün klinika | Hər şey oxu, maliyyə hesabatları, KPI |
| Menecer | Əməliyyat | İş qrafiki, stok təsdiqi, hesabatlar |
| Klinik Admin | Konfiqurasiya | İstifadəçilər, qiymət siyahısı, otaqlar |
| Reception | Qəbul/qeydiyyat | Pasiyent yarat, təqvim, növbə, ilkin ödəniş |
| Həkim (+ ixtisaslar) | Klinik iş | Öz pasiyentləri: odontoqram, plan, resept |
| Ortodont/Cərrah/İmplantoloq/Endodont/Pediatr | İxtisas modulları | Həkim + ixtisas şablonları (implant planı, ortodontik plan…) |
| Gigiyenist | Profilaktika | Periodontal chart, gigiyena qeydləri |
| Laboratoriya | Protez sifarişləri | Lab sifarişi, STL/skan faylları |
| Rentgen | Görüntüləmə | DICOM yükləmə, CBCT, şəkil |
| Maliyyə / Kassir | Pul axını | Invoice, ödəniş, refund (limitli), kassa smeni |

**Permission modeli:** `resource:action` (məs. `patient:read`, `invoice:refund`) + ABAC şərtləri (yalnız öz filialı, yalnız öz pasiyenti, məbləğ limiti). Permission Matrix UI-dan tenant səviyyəsində dəyişdirilə bilir.

## 3. Bounded Context-lərin müəyyənləşdirilməsi (DDD strateji dizayn)

| # | Context | Modullar | Tip |
|---|---|---|---|
| 1 | **Identity & Access** | Auth, JWT/Refresh, 2FA, RBAC, Device/Session, IP restriction | Generic |
| 2 | **Tenancy & Subscription** | Tenant, Branch, Plan, Feature flags, Licensing (local server) | Generic |
| 3 | **Patient** | Pasiyent kartı, tarixçə, anamnez, allergiya, ailə, sığorta, razılıq, e-imza | **Core** |
| 4 | **Scheduling** | Təqvim, rezervasiya, wait list, queue, no-show, online booking, calendar sync | **Core** |
| 5 | **Clinical** | Odontoqram, Perio chart, müalicə planı, resept, qeydlər, implant/ortodontik plan | **Core** |
| 6 | **Imaging** | X-Ray, CBCT, DICOM/PACS, annotasiya, before/after | Supporting |
| 7 | **Billing** | Qiymət, estimate, invoice, ödəniş, taksit, POS, refund, gift card, promo, insurance billing | **Core** |
| 8 | **Inventory** | Anbar, batch/expiry, PO, supplier, barkod/QR, auto-alert | Supporting |
| 9 | **Laboratory** | Lab sifarişi, status, STL, 3Shape/Exocad inteqrasiya | Supporting |
| 10 | **HR & Payroll** | İşçi, qrafik, maaş, bonus, komissiya, KPI | Supporting |
| 11 | **Accounting** | Kassa, bank, gəlir/xərc, vergi/ƏDV, maliyyə hesabatları | Supporting |
| 12 | **CRM & Marketing** | Lead, kampaniya, loyalty, referral, review, call center | Supporting |
| 13 | **Communication** | SMS/WhatsApp/Telegram/Email/Push şablon və göndərmə | Generic |
| 14 | **Reporting & BI** | 100+ hesabat, export, Power BI, dashboard | Supporting |
| 15 | **AI Platform** | Receptionist, voice notes, OCR, image analysis, proqnozlar, chatbot | Differentiator |
| 16 | **Sync** | Offline sync, conflict resolution (mobil + local server) | Platform |
| 17 | **Audit & Compliance** | Audit log, GDPR (export/erase), HIPAA nəzarətləri | Platform |
| 18 | **Notification Hub (Realtime)** | SignalR hub, push | Platform |

## 4. Əsas biznes axınları (Event Storming xülasəsi)

1. **Yeni pasiyent → qəbul:** `PatientRegistered` → `AppointmentBooked` → `ReminderScheduled` → `PatientCheckedIn` → `QueueTicketIssued`.
2. **Müalicə:** `VisitStarted` → `ToothConditionRecorded` → `TreatmentPlanProposed` → `TreatmentPlanAccepted` → `ProcedurePerformed` → `InvoiceIssued` (saga).
3. **Ödəniş:** `PaymentReceived` → `InvoicePartiallyPaid/Paid` → `CommissionAccrued` → `LedgerEntryPosted`.
4. **Stok:** `ProcedurePerformed` → `StockConsumed` → `StockBelowMinimum` → `PurchaseOrderSuggested`.
5. **Laboratoriya:** `LabOrderCreated` → `ScanAttached` → `LabOrderInProduction` → `LabOrderReceived` → həkimə bildiriş.
6. **No-show:** `AppointmentMissed` → `NoShowRecorded` → risk skorunun yenilənməsi (AI) → `WaitListOfferSent`.

Saga/Process Manager: **Procedure→Billing→Inventory→Accounting** (Outbox + Orchestration, kompensasiya ilə).

## 5. Qeyri-funksional tələblər (NFR)

| Sahə | Hədəf |
|---|---|
| Performans | API p95 < 200 ms, UI ilk yüklənmə (LCP) < 1.5 s, odontoqram render 60 FPS |
| Əlçatanlıq | SLA 99.95% (cloud), RTO ≤ 30 dəq, RPO ≤ 5 dəq |
| Miqyas | 10 000 tenant, tenant başına 50 istifadəçi, 1M+ pasiyent/tenant-qrup |
| Təhlükəsizlik | AES-256 at-rest, TLS 1.3, field-level encryption (PHI), OWASP ASVS L2 |
| Uyğunluq | GDPR, HIPAA prinsipləri, audit izi dəyişməzdir (append-only, hash-chain) |
| Offline | Mobil/Desktop: 72 saat offline işləmə, avtomatik sinxron |
| Müşahidə | Tracing (OpenTelemetry), metrics (Prometheus), logs (Serilog→Loki/ELK) |
| i18n | AZ, EN, RU, TR; RTL hazırlığı |

## 6. Multi-tenancy strategiyası (qərar)

- **Default (SaaS):** Shared DB, **schema-per-tenant** (PostgreSQL) + Row-Level Security ikinci müdafiə qatı kimi. 
- **Enterprise:** Database-per-tenant (böyük şəbəkələr, data residency).
- **Local Server:** Tək tenant, eyni sxem; lisenziya serveri ilə yoxlama; cloud ilə **bidirectional sync**.
- Tenant həlli: subdomain (`klinika.dentacore.app`) + JWT `tid` claim; hər sorğuda `ITenantContext`.

## 7. Risklər və qərarlar

| Risk | Təsir | Azaltma |
|---|---|---|
| Microservice həddən artıq erkən bölünməsi | Yüksək mürəkkəblik | **Modular monolith-dən başla**, hər context ayrıca assembly; yetkinləşəndə servislərə çıxar. Kod strukturu artıq mikroxidmətə hazır olur |
| Offline sync konfliktləri | Data itkisi | CRDT yox, **versiya vektoru + field-level merge + manual resolve UI** |
| Tibbi AI məsuliyyəti | Hüquqi | AI yalnız "təklif"; həkim təsdiqi məcburi; model versiyası audit-də |
| DICOM həcmi | Xərc/performans | S3 + lifecycle (cold tier), thumbnails, streaming (WADO-RS) |
| Regional reqlamentlər | Bazar girişi | Compliance paketləri (plugin), data residency |

> **Vacib qeyd (dürüst):** Bütün sadalanan modulları (≈150 funksiya) bir cavabda "production-ready" yazmaq mümkün deyil. Plan: **MVP (Faza 1)** → **Faza 2** → **Faza 3**. Hər mərhələdə real, işləyən, test olunmuş kod verəcəyəm.

## 8. Faza planı

**Faza 1 — MVP (kommersiya üçün minimum):** Identity, Tenancy, Patient, Scheduling, Clinical (odontoqram + plan), Billing, Dashboard, Audit, Notification (SMS/Email), Web UI, Docker Compose, CI.
**Faza 2:** Inventory, Laboratory, HR/Payroll, Accounting, CRM, Reports (30+), Mobile (Flutter), Offline sync, Kubernetes.
**Faza 3:** Imaging/PACS/DICOM, AI modulları, Desktop (MAUI), Insurance billing, Power BI, 100+ hesabat, marketplace/integrations.

## 9. Mərhələ planı (istədiyiniz sıra)

0. ✅ Analiz
1. Architecture ← növbəti
2. Database Design (ER)
3. API Specification (OpenAPI)
4. UI Wireframes
5. UX Flow
6. Folder Structure
7. Source Code (Faza 1 vertikal dilimlər üzrə, hissə-hissə)
8. Testing
9. Deployment
10. Documentation
