# syntax=docker/dockerfile:1.7
# Bir Dockerfile, altı host:  docker build -f infra/docker/backend.Dockerfile --build-arg HOST=DentaCore.ClinicCore.Api dental-erp
#   HOST: DentaCore.Identity.Api | DentaCore.ClinicCore.Api | DentaCore.Billing.Api | DentaCore.Notify.Api | DentaCore.Workers | DentaCore.Gateway
# Kontekst: dental-erp/ (backend/ kodu). Baza image-lər CI-də digest ilə sancılır (Renovate), burada oxunaqlı teqlər.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG HOST
WORKDIR /src
# Əvvəlcə yalnız restore üçün lazım olan faylları kopyala: kod dəyişəndə NuGet qatı yenidən yüklənməsin
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props ./backend/
COPY backend/src ./backend/src
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore "backend/src/Hosts/${HOST}/${HOST}.csproj"
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish "backend/src/Hosts/${HOST}/${HOST}.csproj" -c Release --no-restore -o /out \
      /p:UseAppHost=false /p:DebugType=none /p:TreatWarningsAsErrors=true

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
ARG HOST
LABEL org.opencontainers.image.title="dentacore-${HOST}" org.opencontainers.image.source="https://github.com/xezerli/emiras"
WORKDIR /app
COPY --from=build /out .
# Qeyri-root (aspnet image-ində `app` istifadəçisi, UID 1654), yalnız-oxu fayl sistemi K8s securityContext ilə təmin olunur
USER $APP_UID
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0 \
    DOTNET_EnableDiagnostics=0 \
    Logging__Format=json
EXPOSE 8080
# ENTRYPOINT-də HOST dəyişəni işləmir (exec forma), ona görə sabit adla çağırılır
ENV HOST_DLL=${HOST}.dll
ENTRYPOINT ["sh", "-c", "exec dotnet \"$HOST_DLL\""]
