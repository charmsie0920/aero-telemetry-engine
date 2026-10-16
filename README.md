# Aero Telemetry Engine

A fault-tolerant pipeline that ingests live aircraft positions from the OpenSky Network every 30 seconds, stores them in PostgreSQL, and flags unusual flights. It is designed to keep collecting data when parts of the system fail.

**Status: in progress.** The design is complete and the ingestion layer is being built now. [What works today](#what-works-today) lists exactly what exists; the rest of this page describes the design.

**Stack so far:** C# / .NET 10 · PostgreSQL 17 (`pg_partman`, `pg_cron`) · Docker Compose · Grafana · DbUp · Polly · xUnit, Testcontainers, WireMock.Net<br>
**Planned:** Python (ONNX Runtime) · Terraform · AWS (EC2, S3) · GitHub Actions

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/diagrams/architecture-dark.png">
  <img alt="Architecture: OpenSky feeds a .NET collector that writes to Postgres. An anomaly detector and an archiver read from Postgres, and Grafana shows the result." src="docs/diagrams/architecture-light.png">
</picture>

Solid boxes are being built now; dashed boxes are planned. The editable source is [docs/diagrams/architecture.excalidraw](docs/diagrams/architecture.excalidraw), and the model-training and deployment diagrams are in [docs/architecture.md](docs/architecture.md).

## What works today

| Part | State |
|---|---|
| Database schema | **Built.** Six versioned SQL migrations in [db/migrations](db/migrations), applied by a one-shot DbUp migrator. Checked on a clean Postgres 17: a second run changes nothing, and replaying a batch adds no duplicate rows. |
| Collector | **Built.** Polls OpenSky every 30 s with a cached OAuth2 token, retries with backoff, a circuit breaker and rate-limit handling. Validates records, buffers them in a bounded queue and bulk-loads them into Postgres with no duplicates. Survives a database outage without losing data (tested). Health endpoints on port 8080. |
| Tests | **Built.** 47 automated tests: parsing rules; OpenSky failure handling (401, 429, 5xx, timeouts, circuit breaker) against a fake server; database writes against a real throwaway Postgres in Docker, including duplicate handling and flushing on shutdown. |
| Time partitioning | **Built.** Daily partitions managed by `pg_partman`, with an hourly `pg_cron` job that creates new ones and detaches those older than 48 hours. |
| Dashboard | **Built.** Grafana, provisioned from files in [grafana/](grafana): live aircraft map plus pipeline health (polls per minute, new vs duplicate rows, failed polls, OpenSky credits, database size against the 500 MB budget). |
| Containers | **Built.** Multi-stage Dockerfiles, non-root, with a `HEALTHCHECK`. `docker compose up` runs Postgres → migrations → collector → Grafana. `docker stop` shuts the collector down cleanly (tested). |
| Anomaly detector, archiver | Planned |
| AWS deployment (Terraform, GitHub Actions) | Planned |

## Run it

You need Docker and a free [OpenSky](https://opensky-network.org) API client. No AWS account and no .NET SDK are needed.

```bash
cp .env.example .env            # then fill in OPENSKY_CLIENT_ID and OPENSKY_CLIENT_SECRET
docker compose up -d --build    # Postgres, migrations, collector and Grafana
```

Open **http://localhost:3000**. The dashboard fills in as polls arrive every 30 seconds. `docker compose logs -f ingestion` shows one JSON log line per poll, and `curl http://localhost:8080/health/ready` reports the collector's health.

To work on the code, install the .NET 10 SDK, stop the containerised collector (`docker compose stop ingestion`) and run `dotnet run --project src/Ingestion/Ingestion.Worker`. `dotnet test` runs the 47 tests (Docker needed for the database ones).

<!-- A screenshot or short GIF of the Grafana dashboard goes here once it exists. -->

## How it works

Every 30 seconds the collector pulls the current state of all aircraft in one 5° × 5° region from OpenSky, cleans the records and writes them to PostgreSQL. A separate detector service reads recent flight paths every 60 seconds and scores them for anomalies. A Grafana dashboard reads both. Data older than 48 hours is archived to S3 as Parquet.

## Measured so far

- **319 aircraft** in one poll of the region (north-west Europe, 18 September 2026).
- **1 OpenSky credit per call**, so 2,880 polls a day fit inside the 4,000-credit daily budget.

Throughput, write latency and recovery times will be added here once the pipeline has run end to end.

## Design decisions

Only the idempotent write path and the partitioning exist in code so far. The others are the design the collector is being built to.

**The collector and the detector never call each other.** They communicate only through the database. If the Python detector crashes or falls behind, data collection is unaffected: the pipeline degrades instead of stopping. The cost is that anomaly scores lag the raw data by up to 60 seconds, which is acceptable here.

**Writes are idempotent.** Every position has the primary key `(icao24, observed_at)`. Each batch is copied into a staging table and then inserted with `ON CONFLICT DO NOTHING`, so a crash mid-batch followed by a replay produces the same table state. This is what makes at-least-once delivery safe.

**The collector buffers through database outages.** When PostgreSQL is unreachable, batches wait in a bounded in-memory buffer. The buffer is bounded on purpose: unbounded growth turns a database outage into a crash. If it fills, the oldest batch is dropped and counted, so the loss is visible.

**Plain PostgreSQL partitioning rather than a time-series extension.** TimescaleDB is deprecated on Supabase Postgres 17, and daily partitions managed by `pg_partman` cover what this project needs: fast time-range queries and cheap removal of data older than 48 hours.

**Retry with backoff plus a circuit breaker on the OpenSky API.** The API is public and rate-limited, so hammering it during an outage makes things worse. On a rate-limit response the collector waits for the time the API asks for.

## Project layout

```
src/Ingestion/Ingestion.Worker       the collector (.NET worker service)
src/Ingestion/Ingestion.Migrations   one-shot schema migrator (DbUp)
db/migrations                        versioned SQL migrations
db/postgres                          local Postgres image with pg_partman and pg_cron
docs/PRD.md                          spec, roadmap and decision log
docs/architecture.md                 diagrams, walkthrough and glossary
docs/adr                             architecture decision records
docs/diagrams                        architecture diagram and its sources
```

`services/`, `training/`, `infra/` and `grafana/` are placeholders for the planned parts.

---

Flight data comes from [The OpenSky Network](https://opensky-network.org).
