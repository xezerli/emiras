# ADR-0005: YARP Gateway

**Status:** Accepted

## Kontekst
.NET ekosistemi, yüngül, tenant həlli və başlıq təmizləməsi eyni kodda.

## Qərar
`Yarp.ReverseProxy` ilə Gateway: marşrutlama, `X-Tenant`/`X-Forwarded-*` təmizləmə, auth marşrutları üçün sürət limiti.

## Nəticələr
Alternativ: Kong/Envoy. Məhdudiyyət: Gateway telemetriyası hələ yoxdur.
