# Ingestion worker image (P2.1). Build from the repo root:
#   docker build -f src/Ingestion/Ingestion.Worker/Dockerfile .
#
# Multi-stage: the large SDK image compiles the code, and only the published
# output is copied into the much smaller ASP.NET runtime image.

# ---- build ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Project files first, then restore. Docker caches each step, so as long as
# no .csproj or package version changes, later builds skip the restore.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ingestion/Ingestion.Core/Ingestion.Core.csproj src/Ingestion/Ingestion.Core/
COPY src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj src/Ingestion/Ingestion.Worker/
RUN dotnet restore src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj

COPY src/Ingestion/Ingestion.Core/ src/Ingestion/Ingestion.Core/
COPY src/Ingestion/Ingestion.Worker/ src/Ingestion/Ingestion.Worker/
RUN dotnet publish src/Ingestion/Ingestion.Worker/Ingestion.Worker.csproj \
    --configuration Release --no-restore --output /app

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Run as the image's built-in unprivileged user, not root.
USER $APP_UID

EXPOSE 8080

# The image has no curl, so the worker probes its own /health/ready endpoint
# (see Health/HealthProbe.cs). start-period gives the first poll time to land.
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["dotnet", "Ingestion.Worker.dll", "healthcheck"]

# Exec form, so the worker is PID 1 and receives SIGTERM from `docker stop`
# directly. That is what triggers the graceful shutdown and buffer flush.
ENTRYPOINT ["dotnet", "Ingestion.Worker.dll"]
