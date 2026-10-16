# One-shot migrator image. Build from the repo root:
#   docker build -f src/Ingestion/Ingestion.Migrations/Dockerfile .
#
# Runs once on `docker compose up`, applies db/migrations, and exits 0.
# The ingestion service waits for it to finish before starting.

# ---- build ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Ingestion/Ingestion.Migrations/Ingestion.Migrations.csproj src/Ingestion/Ingestion.Migrations/
RUN dotnet restore src/Ingestion/Ingestion.Migrations/Ingestion.Migrations.csproj

COPY src/Ingestion/Ingestion.Migrations/ src/Ingestion/Ingestion.Migrations/
# Linked into the migrator by its .csproj, and the SQL files are embedded at
# build time, so both must be in the build context.
COPY src/Ingestion/Ingestion.Worker/Configuration/DotEnvLoader.cs src/Ingestion/Ingestion.Worker/Configuration/
COPY db/migrations/ db/migrations/
RUN dotnet publish src/Ingestion/Ingestion.Migrations/Ingestion.Migrations.csproj \
    --configuration Release --no-restore --output /app

# ---- runtime ----------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Ingestion.Migrations.dll"]
