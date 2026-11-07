# API kontraktı

`openapi.yaml` (OpenAPI 3.1) frontend tiplərinin mənbəyidir (`pnpm gen:api`), CI-də Redocly ilə yoxlanır və generasiya olunmuş tiplərin fərqi qadağandır.

```bash
npx --yes @redocly/cli@1 lint api/openapi.yaml --config api/redocly.yaml
npx --yes @redocly/cli@1 build-docs api/openapi.yaml -o /tmp/api.html     # oxunaqlı HTML
```

Kontrakt və real davranış arasındakı **bilinən fərqlər**: [api-guide §9](../docs/guides/api-guide.md#9-openapi-ilə-real-davranış-arasındakı-fərqlər). Nümunələr canlı stack-də `tests/docs/api-guide-examples.sh` ilə yoxlanır.
