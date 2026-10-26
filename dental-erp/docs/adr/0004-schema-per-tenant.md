# ADR-0004: Schema-per-tenant (RLS yalnız platform.login_directory-də)

**Status:** Accepted (RLS hissəsi daraldılıb)

## Kontekst
İzolyasiya və xərc balansı. Orijinal plan hər yerdə RLS idi.

## Qərar
Hər tenant ayrı PostgreSQL sxemidir (`t_<slug>`); sorğu `search_path` ilə həmin sxemə bağlanır. RLS yalnız `platform.login_directory`-dədir.

## Nəticələr
Məhdudiyyət: bütün tenant-lar eyni DB rolu ilə işləyir; ikinci qat (rol/RLS) yoxdur. Bax [security-and-privacy](../guides/security-and-privacy.md) §4, §9.
