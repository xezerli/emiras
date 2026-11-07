# ADR-0007: PII üçün sahə səviyyəsində şifrələmə (klinik məzmun daxil deyil)

**Status:** Accepted (qismən)

## Kontekst
Əlaqə məlumatı və FİN sızarsa ən böyük zərər verir; axtarış da lazımdır.

## Qərar
Telefon, e-poçt, FİN AES-256-GCM ilə şifrələnir, axtarış üçün HMAC blind index saxlanır. SOAP qeydləri, allergiya, diaqnoz **şifrələnmir**.

## Nəticələr
Açıq boşluq: klinik məzmunun şifrələnməsi və açar rotasiyası. Bax security-and-privacy §9.
