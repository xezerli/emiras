# ADR-0006: Event sourcing tətbiq olunmadı; audit hash zənciri ilə

**Status:** Accepted (ilkin qərar dəyişdirilib)

## Kontekst
Orijinal plan Ledger + Audit üçün event sourcing idi. Dəyişməzlik tələbi append-only cədvəl və trigger ilə ödənir.

## Qərar
Ödənişlər və audit append-only cədvəllərdir (DB trigger-i dəyişdirməyi qadağan edir); audit sətirləri `prev_hash` ilə zəncirlənir.

## Nəticələr
Nəticə: hadisələrdən vəziyyət bərpası yoxdur; düzəliş əks-əməliyyatla (refund) edilir.
