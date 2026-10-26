# ADR-0003: RabbitMQ + transactional outbox (MassTransit olmadan)

**Status:** Accepted (ilkin qərar dəyişdirilib)

## Kontekst
Orijinal plan MassTransit idi. Real ehtiyac sadə publish/consume + etibarlı çatdırılma idi.

## Qərar
`RabbitMQ.Client` birbaşa istifadə olunur. Hadisə eyni DB tranzaksiyasında outbox-a yazılır, Workers göndərir; consumer-lər inbox ilə təkrarları süzür. MassTransit və saga **yoxdur**.

## Nəticələr
Nəticə: `dentacore.outbox.*` metrikaları var. Kafka həcm artdıqda yenidən baxılacaq.
