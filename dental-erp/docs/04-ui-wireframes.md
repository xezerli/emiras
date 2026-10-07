# Mərhələ 4 — UI Wireframes

> Status: ✅ Tamamlandı. 5 əsas ekran işləyən HTML wireframe kimi hazırdır, light/dark və mobil eni brauzerdə yoxlanılıb.
> Növbəti: Mərhələ 5 (UX Flow)

## 1. Necə baxmalı

`ui/wireframes/` qovluğunu brauzerdə açın (build lazım deyil, asılılıq yoxdur):

```bash
cd dental-erp/ui/wireframes && python3 -m http.server 8080   # → http://localhost:8080
```

Yuxarı panelin sağındakı 🌓 düyməsi dark/light rejimi dəyişir (seçim yadda qalır). Ekran görüntüləri `ui/wireframes/screenshots/` qovluğundadır.

| Ekran | Fayl | Nümunə interaktivlik |
|---|---|---|
| Dashboard | `index.html` | KPI, 14 günlük gəlir qrafiki, canlı kabinet vəziyyəti, top xidmət/həkim |
| Təqvim | `calendar.html` | Həkim sütunları, qəbul statusu rəngləri, iş saatı xaricində zolaq, "indi" xətti, növbə və wait list |
| Pasiyent kartı | `patient.html` | Allergiya xəbərdarlıq banneri, maskalanmış telefon (göstərmək audit-ə yazılır), tarixçə timeline, tibbi profil |
| Odontoqram | `odontogram.html` | 32 dişi klik/klaviatura ilə seç, vəziyyət təyin et, geri al, vizit jurnalı |
| Billing / POS | `billing.html` | Faktura sətirləri, ödəniş üsulları, qismən ödəniş, artıq ödəniş yoxlaması (`billing.overpayment`) |

Bu fayllar **wireframe/prototipdir**, istehsal kodu deyil. Mərhələ 7-də eyni tokenlər Next.js dizayn sisteminə (`@dentacore/ui`) köçürüləcək.

## 2. Dizayn sistemi

**Tokenlər:** `ui/wireframes/tokens.css`. Komponentlər rəng, məsafə, radius və animasiya üçün yalnız token işlədir.

| Qrup | Qərar |
|---|---|
| Rəng | Material Design 3 rolları: `primary`, `secondary`, `tertiary`, `error`, `surface` və `-container` cütləri. Əlavə semantik rənglər: `success`, `warning` |
| Dark/Light | Hər token iki dəyərlidir (`:root` və `[data-theme="dark"]`). Seçim `localStorage`-da, təhlükəsiz try/catch ilə |
| Glassmorphism | Yalnız səth qatlarında (sidebar, topbar, kartlar): yarı şəffaf fon + `backdrop-filter: blur(18px)` + nazik işıqlı sərhəd. Mətn həmişə kifayət qədər kontrastlı fonda qalır, şüşə effekti oxunaqlılığı pozmur |
| Tipoqrafiya | Inter / sistem şrifti. 12 / 14 / 18 / 24 / 32 px şkalası |
| Məsafə / radius | 4-px şəbəkə; radius 10 / 16 / 24 |
| Animasiya | 120 ms (mikro) və 240 ms (keçid), `cubic-bezier(.2,0,0,1)`. `prefers-reduced-motion` altında 0 ms |

**Klinik rənglər (odontoqram):** vəziyyət yalnız rənglə fərqləndirilmir, hər birinin ayrıca simvolu var (● kariyes, ■ plomb, ♛ tac, ⚙ implant, ┃ kanal, ✕ çıxarılıb). Rəng korluğu olan istifadəçi üçün də oxunur.

## 3. Naviqasiya və qabıq (shell)

- **Sol sidebar** (232 px): modul naviqasiyası. Hələ yazılmamış modullar (Anbar, Lab, Rentgen, HR, Mühasibat, CRM, Hesabatlar) soluq görünür, fazalar aydındır.
- **Topbar:** qlobal axtarış (`Ctrl K`, pasiyent/telefon/kart №), filial seçimi, bildirişlər, tema, **Online/Offline göstəricisi**, cari istifadəçi.
- **Rola görə menyu:** rol və permission-lara uyğun olaraq görünməyən bölmələr gizlədilir (məs. Reception maliyyə hesabatını görmür). Server yenə də yoxlayır, UI yalnız rahatlıq üçündür.
- **Responsiv:**
  - ≥ 1100 px: tam şəbəkə;
  - 760–1100 px: kartlar 2 sütuna, yan panellər aşağı düşür;
  - < 760 px: sidebar gizlənir (mobil üçün bottom-nav Mərhələ 5-də), topbar sıxılır. 390 px eninde üfüqi scroll yoxdur (ölçüldü: `scrollWidth = 390`).

## 4. Ekran qərarları

### Dashboard
- 4 KPI: bugünkü qəbul, gəlir, borc, yeni pasiyent. Hər birinin dəyişmə göstəricisi var (rəng + ▲/▼ simvolu).
- Qrafikin `aria-label`-i əsas rəqəmi söyləyir, həmçinin cədvəl görünüşü planlaşdırılıb (a11y).
- Kabinet vəziyyəti SignalR `dashboard.tick` və `queue.updated` ilə canlı yenilənir. Boş kabinet və gözləyən pasiyent varsa "Növbəti pasiyenti çağır" təklif olunur.
- "Diqqət tələb edir" bloku: təsdiqlənməmiş qəbullar, vaxtı keçmiş taksit, gecikən lab sifarişi.

### Təqvim
- Sütun = həkim (+ kabinet). Gün/Həftə/Ay. `N` qısayolu yeni qəbul açır.
- Sürüklə-burax ilə vaxt/həkim dəyişir. Server 409 qaytarsa blok əvvəlki yerinə qayıdır və toast göstərilir (`appointment.overlap`).
- Status rəngləri: planlanmış, təsdiqlənmiş, gəlib/prosesdə, no-show (üstü xətli).
- Sağ panel: wait list və lobbi növbəsi.

### Pasiyent kartı
- Qırmızı **allergiya/risk banneri** hər tabda görünür və bağlanmır (təhlükəsizlik üçün).
- Telefon maskalıdır. "Göstər" düyməsi açır və `patient.read.sensitive` audit hadisəsi yazır.
- Tablar: İcmal, Odontoqram, Müalicə planı, Anamnez, Görüntülər, Sənədlər, Maliyyə, Ailə.
- Həkim üçün üç əsas hərəkət başlıqda: Qəbul, Vizitə başla, Faktura.
- AI tərəfindən doldurulmuş qeydlər ayrıca işarələnir və həkim təsdiqi gözləyir.

### Odontoqram
- FDI nömrələmə, 2 sıra × 16 diş. Klik və ya Tab + Enter ilə seçim. Hər dişin `aria-label`-i ("Diş 14: Kariyes").
- Sağ panel: vəziyyət düymələri, səth (M/D/O/B/L), saxla/geri al, bu vizitin jurnalı və "Plana əlavə et".
- Qeyd köhnəni əvəz edir, tarixçə saxlanır (DB-də `superseded_at`).

### Billing / POS
- Solda sətirlər (xidmət, diş, say, qiymət, endirim), sağda cəmi/sığorta/ödənilib/qalıq və ödəniş paneli.
- Üsullar: nağd, kart, POS, köçürmə, hədiyyə kartı, taksit.
- Məbləğ qalığı aşarsa xəta göstərilir (server qaydası ilə eyni). "Tam" düyməsi qalığı doldurur.
- Kassa smeni açıq deyilsə nağd/POS deaktiv olmalıdır (prototipdə yalnız qeyd kimi göstərilib).

## 5. Əlçatanlıq (WCAG 2.2 AA hədəfi)

- Klaviatura ilə tam istifadə, görünən fokus halqası (3 px), semantik landmark-lar (`nav`, `header`, `main`, `aside`).
- Rəng tək məlumat daşıyıcısı deyil (simvol + mətn).
- `role="alert"` ilə xəta və kritik xəbərdarlıqlar, `aria-current="page"` aktiv menyu üçün.
- Toxunma hədəfləri ≥ 40 px.
- `prefers-reduced-motion` və `prefers-color-scheme` hörmət olunur.

## 6. Qalan və bilərəkdən buraxılanlar (dürüst qeyd)

- Kontrast nisbəti avtomatik alətlə hələ ölçülməyib. Mərhələ 7-də Storybook və axe-core ilə CI-yə əlavə olunacaq. Glass səthlərdə mətn kontrastını xüsusi yoxlayacağam.
- `₼` işarəsi bu mühitin ehtiyat şriftində qeyri-dəqiq çəkilir. Real Inter/Noto şriftlərində düzgündür, istehsalda şrift paketi ilə təmin olunacaq.
- Bu mərhələdə yoxdur: Periodontal chart, implant planlama, ortodontik planlama, DICOM viewer, AI paneli, mobil və tablet ekranları, hesabat konstruktoru. Onlar öz fazalarında əlavə olunacaq.
- Mobil üçün ayrıca bottom-navigation və offline göstəricilər UX Flow mərhələsində dəqiqləşir.

> Növbəti: **Mərhələ 5 — UX Flow**: rol üzrə istifadəçi səyahətləri (Reception, Həkim, Kassir, Direktor), "minimal klik" hədəfləri, xəta/offline halları, onboarding. Davam etmək üçün **"Davam et"** yazın.
