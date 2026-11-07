# syntax=docker/dockerfile:1.7
# docker build -f infra/docker/frontend.Dockerfile dental-erp
# Next.js "standalone" çıxışı: kiçik image, yalnız lazım olan node_modules. Produksiyada /v1 və /hubs-u ingress yönləndirir,
# ona görə dev rewrite-ları söndürülür (DISABLE_API_REWRITE=1). NEXT_PUBLIC_DEV_TENANT burada TƏYİN EDİLMİR (produksiyada tenant host adından tanınır).

FROM node:22-slim AS deps
RUN corepack enable
WORKDIR /repo/frontend
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
COPY frontend/apps/web/package.json apps/web/
COPY frontend/packages/api-client/package.json packages/api-client/
COPY frontend/packages/ui/package.json packages/ui/
COPY frontend/packages/config/package.json packages/config/
RUN --mount=type=cache,target=/root/.local/share/pnpm/store pnpm install --frozen-lockfile

FROM deps AS build
COPY frontend ./
ENV NEXT_OUTPUT=standalone DISABLE_API_REWRITE=1 NEXT_TELEMETRY_DISABLED=1
RUN pnpm --filter @dentacore/web build

FROM node:22-slim AS runtime
ENV NODE_ENV=production NEXT_TELEMETRY_DISABLED=1 PORT=3000 HOSTNAME=0.0.0.0
WORKDIR /app
# node image-də istifadəçi `node` (UID 1000)
COPY --from=build --chown=node:node /repo/frontend/apps/web/.next/standalone ./
COPY --from=build --chown=node:node /repo/frontend/apps/web/.next/static ./apps/web/.next/static
USER node
EXPOSE 3000
CMD ["node", "apps/web/server.js"]
