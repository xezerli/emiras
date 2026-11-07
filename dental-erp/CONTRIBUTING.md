# Töhfə qaydaları

Başlamazdan əvvəl [developer-guide](docs/guides/developer-guide.md) və [STATUS](docs/STATUS.md)-u oxu.

1. **Qol:** qısa ömürlü qol aç, PR göndər. `main`-ə birbaşa push yoxdur.
2. **Yoxlamalar lokal:** `dotnet test backend/DentaCore.sln`, `cd frontend && pnpm lint && pnpm typecheck && pnpm test`; kontrakt dəyişibsə `npx @redocly/cli@1 lint api/openapi.yaml --config api/redocly.yaml`; sənəd dəyişibsə `python3 tests/docs/check-docs.py`.
3. **Test məcburidir.** Xəta düzəlişi onu tutan testlə gəlir; testin düşdüyünü kodu pozub yoxla. Testi atlamaq/söndürmək olmaz (CI `assert-no-skips`).
4. **Miqrasiya** mövcud faylı dəyişmir, yeni fayl əlavə edir (checksum qoruyur).
5. **Kontrakt əvvəl:** API dəyişikliyi `api/openapi.yaml`-dan başlayır; real davranışla kontrakt ayrılırsa [api-guide §9](docs/guides/api-guide.md) cədvəlinə yaz.
6. **Təhlükəsizlik:** PHI log-lama, sirri repoya qoyma, yeni endpoint `RequireAuthorization()` ilə. Zəiflik bildirişi: [SECURITY.md](SECURITY.md).
7. **Qərar:** əhəmiyyətli arxitektura qərarı yeni ADR-dir ([docs/adr](docs/adr/README.md)).
8. **Commit mesajı** nə və niyə dəyişdiyini yazır; PR təsviri yoxlanmış və yoxlanmamış hissələri ayırır.
