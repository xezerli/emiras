# Mərhələ 10: Sənədləşdirmə (Dental Clinic ERP "DentaCore")

> Status: ✅ Tamamlandı. Bu, sonuncu mərhələdir. Sənədlərin indeksi: [README](README.md); real vəziyyət: [STATUS](STATUS.md).

## 1. Nə yazıldı

| Sənəd | Auditoriya | Necə yoxlanıb |
|---|---|---|
| [README](../README.md), [docs/README](README.md) | Hamı | `check-docs.py` (keçidlər) |
| [STATUS](STATUS.md) | Qərar verənlər | Hər iddia testə/əmrə bağlıdır; "yoxlanmayıb" bölməsi var |
| [user-guide](guides/user-guide.md) | Klinika işçiləri | 11 skrinşot **real UI-dan** (təmiz DB, uydurma məlumat); rol cədvəli DB-dən götürülüb |
| [api-guide](guides/api-guide.md) | İnteqratorlar | Hər `curl` bloku `tests/docs/api-guide-examples.sh`-dən götürülüb; skript canlı stack-ə qarşı **81 yoxlama** keçir; CI-nin e2e işində də işləyir |
| [developer-guide](guides/developer-guide.md) | Developer-lər | Quraşdırma və test əmrləri işlədilib; `next dev` axını tam yoxlanmayıb (yazılıb) |
| [security-and-privacy](guides/security-and-privacy.md) | Təhlükəsizlik/hüquq | İddialar koda qarşı yoxlanıb (Argon2id parametrləri, kilid siyasəti, başlıqlar, CSP-nin olmaması, OTel atributları); boşluqlar §9 |
| [adr/](adr/README.md) | Developer-lər | 9 ADR; **real koda görə**: MediatR, MassTransit və event sourcing planlaşdırılmışdı, tətbiq olunmayıb |
| CONTRIBUTING, SECURITY, CHANGELOG, api/README | Töhfəçilər | Keçidlər yoxlanır |

## 2. Sənədləşdirmə zamanı tapılan real problemlər

Skrinşotlar və nümunələr kodu sənədləşdirməzdən əvvəl işlətməyə məcbur etdi və bunları tapdı:

1. **Kontrakt vs davranış:** `Idempotency-Key` kontraktda yaratma əməliyyatları üçün vəd olunurdu, yalnız ödəniş/refund-da işləyir. Kontrakt və docs/03 düzəldildi ([api-guide §9](guides/api-guide.md) digər fərqləri sadalayır).
2. **UI qüsurları:** gəlir qrafikində üst-üstə düşən etiketlər, allergiya ağırlığı xam kodla göstərilirdi, ödəniş üsulu xam kodla, keçidlər brauzer rəngində. Hamısı düzəldildi və testlə qorunur.
3. **Cookie ömrü uyğunsuzluğu:** refresh token 7 gün, cookie isə daha qısa idi; 7 günə uyğunlaşdırıldı.
4. **Təhlükəsizlik:** `next` 16.4.0 və aşağıda 2 kritik, 1 yüksək advisory; 16.4.3-ə qaldırıldı, audit təmizdir. (Build çıxışında `next upgrade --agent=security` əmri təklif edildi; bu, istifadəçinin sorğusunun hissəsi deyildi, icra olunmadı. Yalnız normal `package.json` yeniləməsi edildi.)
5. **Sənədlərin özü:** ilkin ADR cədvəli real kodla uyğun deyildi; `docs/01` §12-yə qeyd əlavə olundu və ADR-lər yenidən yazıldı.

## 3. Avtomatik qoruma

- `tests/docs/check-docs.py`: nisbi keçidlər, başlıq anchor-ları, şəkillər, bələdçilərdəki skript yolları, ADR indeksi. **Mutasiya ilə yoxlanıb:** sınıq anchor, olmayan fayl, olmayan şəkil, olmayan skript və indeksdə olmayan ADR pozulanda 7 problem tapıldı, bərpadan sonra təmiz.
- CI: `API kontraktı` işində `check-docs.py`, `E2E` işində `api-guide-examples.sh`.

## 4. Məhdudiyyətlər (dürüst)

- Skrinşotlar bir dəfə alınır və UI dəyişəndə **əl ilə** yenilənməlidir (`frontend/apps/web/scripts/capture-docs.mjs` bunu təkrarlayır, CI-də işləmir).
- `check-docs.py` mətnin **doğruluğunu** yox, keçidlərin və fayl yollarının mövcudluğunu yoxlayır; doğruluq üçün nümunə skripti və yuxarıdakı əl ilə yoxlama var.
- Bələdçilər Azərbaycan dilindədir; ingilis versiyası yoxdur.
- Hüquqi uyğunluq sənədləşdirilməyib, çünki qiymətləndirilməyib ([security-and-privacy](guides/security-and-privacy.md) §9).
- Lisenziya faylı yoxdur.

## 5. Layihənin ümumi nəticəsi

Mərhələ 0–10 tamamlandı. Nəticə **pilot üçün əsas** olan, testlərlə sübut olunmuş vertikal kəsikdir; spesifikasiyanın geniş hissəsi yazılmayıb və deploy real mühitdə sınanmayıb. Dəqiq siyahı və təklif olunan növbəti addımlar: [STATUS §3–6](STATUS.md).
