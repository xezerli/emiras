# ADR-0001: Modular monolith-ready mikroservislər

**Status:** Accepted

## Kontekst
Mürəkkəbliyi idarə etmək; erkən tam bölünmədən qaçmaq.

## Qərar
Kod modullara bölünüb (Domain/Application/Infrastructure), host-lar (Gateway, Identity, ClinicCore, Billing, Notify, Workers) ayrıca prosesdir. Modullar arası asılılığı Architecture testləri qadağan edir.

## Nəticələr
Alternativ: erkən tam bölünmə. Nəticə: lokal serverdə eyni host-lar bir kompozisiyada qalxa bilir (`infra/dev/run-stack.sh`).
