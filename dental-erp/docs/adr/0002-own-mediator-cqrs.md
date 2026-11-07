# ADR-0002: Öz yüngül mediator-u + məntiqi CQRS

**Status:** Accepted (ilkin qərar dəyişdirilib)

## Kontekst
Orijinal plan MediatR idi. Praktikada asılılıq və lisenziya riskini azaltmaq üçün kiçik `ISender` pipeline-ı yazıldı (icazə, validasiya, tranzaksiya, audit davranışları).

## Qərar
MediatR **istifadə olunmur**. Əmr/sorğular `ISender` ilə göndərilir; eyni DB ilə başlanıb, axtarış üçün Elasticsearch ayrıca oxu modeli kimi saxlanır.

## Nəticələr
Nəticə: pipeline davranışları BuildingBlocks-dadır və unit testlə örtülüb. Event sourcing tətbiq olunmayıb (bax ADR-0006).
