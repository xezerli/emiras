# Frontend (pnpm workspace)

- `apps/web`: Next.js (App Router) + TypeScript. Feature-based quruluş (`src/features/<modul>`), paylaşılan kod `src/shared`.
- `packages/ui`: `@dentacore/ui` dizayn sistemi. `tokens.css` Mərhələ 4 wireframe tokenlərindən götürülüb (mənbə həqiqət artıq burasıdır).
- `packages/api-client`: `api/openapi.yaml`-dan generasiya olunur (`pnpm gen:api`), əl ilə yazılmır.
- `packages/config`: ortaq ESLint/TS/Prettier konfiqurasiyası.
