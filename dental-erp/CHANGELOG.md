# Dəyişikliklər

Format: mərhələ üzrə. Versiya nömrəsi və buraxılış hələ yoxdur (`release.yml` heç vaxt işləməyib).

## Pilot blokerinin aradan qaldırılması: istifadəçi və rol idarəsi
- `POST/GET/PATCH /v1/users`, `PUT /v1/users/{id}/roles`, `/disable`, `/enable`, `/reset-password`, `GET /v1/roles`, `PUT /v1/roles/{code}/permissions` (hamısı `user:manage`); Gateway marşrutu; veb `/users` və `/roles` ekranları.
- Qorunmalar: özünü söndürmə/rol dəyişmə yoxdur, `director`/`clinic_admin` dəyişmir, `user:manage` verilə bilməz, sessiyalar ləğv olunur.
- Kontraktdakı heç vaxt həyata keçməyən `/admin/users` `/users` ilə əvəz olundu.
- Test: Identity inteqrasiya testləri paralel host-ların proses-geniş mühit dəyişənlərini qarışdırmasın deyə eyni xidmət kolleksiyasına salındı.

## Mərhələ 10: Sənədləşdirmə
- İstifadəçi, API, developer və təhlükəsizlik bələdçiləri; STATUS; ADR-0001..0009.
- API bələdçisinin nümunələri canlı stack-ə qarşı 76 yoxlama ilə sınanır (`tests/docs/api-guide-examples.sh`).
- Skrinşotlar real UI-dan alındı və tapılan qüsurlar düzəldildi: qrafik etiketləri, allergiya ağırlığı, ödəniş üsulu adı, keçid rəngləri.
- Refresh cookie ömrü 7 günə (token ömrünə) uyğunlaşdırıldı.
- `next` 16.4.3: 2 kritik və 1 yüksək advisory bağlandı.
- Kontrakt/doc düzəlişi: `Idempotency-Key` yalnız ödəniş/refund-da işləyir.

## Mərhələ 9: Deployment
Dockerfile-lar, compose, Helm, `migrate.sh`, backup/bərpa, observability, CI/CodeQL/release iş axınları.

## Mərhələ 8: Testing
Test strategiyası, əhatə hədləri, təhlükəsizlik testləri, yük testi, e2e.

## Mərhələ 1–7
Analiz, arxitektura, DB, API, UI, UX, qovluq quruluşu, mənbə kodu (dilimlər 1–8b).
