# ADR-0008: Brauzerdə BFF: refresh token httpOnly cookie, access token yaddaşda

**Status:** Accepted

## Kontekst
XSS halında token oğurlanmasını çətinləşdirmək.

## Qərar
Next.js route handler-ləri `/api/session/*` refresh token-i `HttpOnly; SameSite=Strict` cookie-də saxlayır (ömür 7 gün, API ilə eyni), Origin yoxlayır. Access token yalnız yaddaşdadır.

## Nəticələr
Nəticə: e2e testi storage-da token olmadığını yoxlayır. CSP hələ əlavə edilməyib.
