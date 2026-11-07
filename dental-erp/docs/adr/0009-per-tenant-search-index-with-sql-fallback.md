# ADR-0009: Elasticsearch tenant üzrə indeks, 503 olanda SQL fallback

**Status:** Accepted

## Kontekst
Axtarış sürəti lazımdır, amma ES düşəndə qəbul/ödəniş dayanmamalıdır.

## Qərar
`dc-patients-<tenant>` indeksi outbox hadisələri ilə yenilənir. ES əlçatmaz olanda axtarış SQL-ə düşür (e2e ilə təsdiq olunub).

## Nəticələr
Nəticə: ES-siz rejimdə telefon/FİN axtarışı UI-da etibarlı deyil (bax user-guide).
