# Architecture Decision Records

Format: `NNNN-qisa-ad.md` (Status, Kontekst, Qərar, Nəticələr). Qərarlar **real koda görə** yazılıb: Mərhələ 1-dəki ilkin planın üç bəndi (MediatR, MassTransit, event sourcing) tətbiq olunmayıb və aşağıda "dəyişdirilib" kimi qeyd edilib.

| № | Qərar | Status |
|---|---|---|
| [0001](0001-modular-monolith-ready-services.md) | Modular monolith-ready mikroservislər | Accepted |
| [0002](0002-own-mediator-cqrs.md) | Öz yüngül mediator-u + məntiqi CQRS | Accepted (ilkin qərar dəyişdirilib) |
| [0003](0003-rabbitmq-transactional-outbox.md) | RabbitMQ + transactional outbox (MassTransit olmadan) | Accepted (ilkin qərar dəyişdirilib) |
| [0004](0004-schema-per-tenant.md) | Schema-per-tenant (RLS yalnız platform.login_directory-də) | Accepted (RLS hissəsi daraldılıb) |
| [0005](0005-yarp-gateway.md) | YARP Gateway | Accepted |
| [0006](0006-no-event-sourcing.md) | Event sourcing tətbiq olunmadı; audit hash zənciri ilə | Accepted (ilkin qərar dəyişdirilib) |
| [0007](0007-pii-field-encryption.md) | PII üçün sahə səviyyəsində şifrələmə (klinik məzmun daxil deyil) | Accepted (qismən) |
| [0008](0008-bff-refresh-cookie.md) | Brauzerdə BFF: refresh token httpOnly cookie, access token yaddaşda | Accepted |
| [0009](0009-per-tenant-search-index-with-sql-fallback.md) | Elasticsearch tenant üzrə indeks, 503 olanda SQL fallback | Accepted |

Yeni qərar üçün növbəti nömrəni götür, mövcud ADR-i silmə: dəyişəndə statusunu `Superseded by ADR-NNNN` et.
