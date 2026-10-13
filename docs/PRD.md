# PRD: Real-Time Telemetry & AI Analytics Engine

**Repo:** `aero-telemetry-engine` · **Owner:** charmaine · **Version:** 2.1 (shortened October 2026)

This document is the spec: what to build, in what order, and what has been decided. Related documents:

- [CLAUDE.md](../CLAUDE.md): how to work in this repo (commands, conventions, layout).
- [architecture.md](architecture.md): diagrams, plain-English walkthrough, glossary.
- [interview-notes.md](interview-notes.md): target roles, resume bullets, interview questions.
- [adr/](adr/): the reasoning behind individual decisions.

Section numbers are referenced from code comments and CLAUDE.md. Don't renumber them.

---

## 0. Current Status (update this block every time you work)

```
PHASE:          1 — Ingestion and Storage
CURRENT TASK:   P1.3
LAST UPDATED:   2026-10-13
DONE SINCE LAST UPDATE:
  - P1.2 done. Local Postgres is a custom image (db/postgres/Dockerfile) with
    pg_partman 5.5 and pg_cron 1.6. Migration 0006 sets up daily partitions on
    telemetry.state_vectors (2 days premade + a default partition), 48-hour
    retention that detaches rather than drops, and an hourly pg_cron job.
    Applied to the local database; inserts now work.
BLOCKERS:
  - Supabase: need the SESSION-mode pooler connection string (the direct host
    is IPv6-only and unreachable from this machine). Migrations 0001-0006 are
    not applied there yet, and 0006 is untested on Supabase.
NOTES:
  - Another project's container (recall-postgres) also uses host port 5433
    and starts with Docker. Stop it before `docker compose up`.
OPEN QUESTIONS:
  - Keep the Benelux/NW Europe box (48.0,2.0 -> 53.0,7.0) or pick another region?
  - Detached partitions use disk until the archiver (P3.1) drops them. Check
    at P1.10 whether that fits in Supabase's 500 MB.
MEASURED NUMBERS SO FAR:
  - 319 aircraft in one poll of the 25 sq deg box (2026-09-18).
  - 1 OpenSky credit per call confirmed.
```

### Instructions for AI assistants

1. Check the Status block to see where I am. Task IDs (e.g. `P1.4`) refer to Section 9.
2. The Decision Log (Section 11) is settled. Don't re-argue it unless a fact has changed.
3. Respect the Non-Goals (Section 3).
4. Section 12 facts were checked in September 2026. Check again before relying on them.
5. Prefer simple solutions I can explain in an interview over adding technologies.

---

## 1. Project Summary

A system that continuously ingests live aircraft positions from the OpenSky Network, stores them in time-partitioned PostgreSQL, archives older data to S3 as Parquet, and runs a PyTorch-trained model that scores flight trajectories for anomalies. It is containerised, provisioned with Terraform, deployed to AWS EC2 through GitHub Actions, and shown in a Grafana dashboard.

It is a portfolio project for backend, data-infrastructure, ML-infrastructure and DevOps roles.

Data arrives every ~30 seconds, so always call it **near-real-time**, never real-time.

## 2. Success Criteria

The project is done when all of these are true:

1. Ingestion has run continuously on AWS for at least 14 days, with uptime and gap statistics on the dashboard.
2. A recorded demo shows automatic recovery from a database outage, OpenSky rate limiting, and the AI service crashing.
3. The anomaly model has a written evaluation against a rule-based baseline, with precision/recall numbers.
4. Infrastructure can be rebuilt from scratch with `terraform apply` plus one pipeline run.
5. The whole system also runs locally with a single `docker compose up`.
6. The README has an architecture diagram, a failure-mode table, measured performance numbers and a demo video link.

## 3. Non-Goals

- No Kafka, RabbitMQ or other message broker. Postgres is the integration point.
- No Kubernetes. Docker Compose on one EC2 instance.
- No custom frontend. Grafana is the UI.
- No user accounts or public write APIs.
- No global coverage. One regional bounding box.
- No training on EC2. The server only serves the model.

## 4. Architecture

Diagrams are in [architecture.md](architecture.md).

| Component | Responsibility |
|---|---|
| **Ingestion Worker** (.NET 10) | Poll OpenSky, validate records, write to Postgres |
| **Postgres** (Supabase) | Hot storage for ~48 hours, anomaly scores, pipeline metrics |
| **Archiver** (Python) | Daily: copy detached partitions to S3 as Parquet, verify row counts, drop them |
| **Inference Service** (Python) | Every 60 s: score recent trajectory windows and write scores back. Also `POST /score` |
| **Training pipeline** (Python, offline) | Build datasets from Parquet, train baselines and a GRU model, export ONNX |
| **Grafana** | Live map, anomaly list, pipeline health |

**Communication rules**

- Ingestion never calls the AI service. They share only the database, so an AI failure never stops data collection.
- `POST /score` is for demos and on-demand use. It is reachable only inside the Docker network or through a tunnel, and requires an API key.
- Only Grafana is exposed publicly (behind login), or nothing is and access is through an SSH/SSM tunnel.

## 5. Data Source: OpenSky Network

- **Endpoint:** `GET https://opensky-network.org/api/states/all` with `lamin`, `lomin`, `lamax`, `lomax`.
- **Auth:** OAuth2 client credentials. Tokens last about 30 minutes.
- **Budget:** 4,000 credits/day. A box of 25 sq° or less costs 1 credit per call.
- **Configuration:** one 5° × 5° box, polled every 30 s = 2,880 credits/day. The box lives in config, not code.
- **Rate limits:** on HTTP 429, wait for `X-Rate-Limit-Retry-After-Seconds`. Log `X-Rate-Limit-Remaining` on every call.
- Credit OpenSky in the README and review its terms before publishing.

### 5.1 Known data quality problems (must be handled)

- Null positions or null `time_position`.
- Stale positions: the same `time_position` returned in several polls (duplicates).
- Aircraft on the ground (`on_ground = true`).
- Null or space-padded callsigns.
- Physically impossible jumps (sensor glitches).
- Gaps when an aircraft leaves coverage.

## 6. Tech Stack

| Layer | Technology |
|---|---|
| Ingestion | .NET 10, `BackgroundService`, `Microsoft.Extensions.Http.Resilience` (Polly v8), `System.Threading.Channels`, Npgsql (binary COPY), Serilog |
| Migrations | DbUp with plain SQL files |
| .NET tests | xUnit, Testcontainers, WireMock.Net |
| Database | Supabase PostgreSQL 17, `pg_partman`, `pg_cron` |
| Archive | AWS S3, Parquet (`pyarrow` / `polars`) |
| Python | 3.12, uv, pytest, ruff, psycopg 3 |
| ML | PyTorch (GRU), scikit-learn (Isolation Forest), ONNX Runtime, FastAPI |
| Dashboard | Grafana, provisioned from files |
| Infrastructure | Docker Compose, Terraform, AWS EC2 t3.small, ECR, SSM Parameter Store |
| CI/CD | GitHub Actions with OIDC to AWS, Trivy scanning |

## 7. Database Design

### 7.1 Tables

The migrations in `db/migrations/` are the source of truth for columns. All tables are in the `telemetry` schema (D13).

| Table | Purpose |
|---|---|
| `state_vectors` | One row per aircraft position. Partitioned by day on `observed_at`. |
| `state_vectors_staging` | Unlogged landing table for the worker's COPY. |
| `ingestion_runs` | One row per poll cycle (pipeline health). |
| `anomaly_scores` | One row per scored trajectory window. |
| `archive_log` | One row per archived partition. |

- **Primary key** of `state_vectors` is `(icao24, observed_at)`. It makes writes idempotent and removes stale duplicates.
- **Insert path:** COPY into staging, then `INSERT ... SELECT ... ON CONFLICT DO NOTHING` into `state_vectors`, in one transaction. (COPY can't do `ON CONFLICT`.)
- **Index:** BRIN on `observed_at`.

### 7.2 Retention and storage budget

- Supabase free tier: **500 MB**; writes are rejected beyond it. Target: stay under 350 MB.
- **48 hours** of hot data. `pg_partman` detaches older daily partitions; the archiver drops them after copying to S3.
- P1.10 measures real MB/day. If it doesn't fit: shorter retention, hourly partitions, or Supabase Pro (record the choice in the Decision Log).

### 7.3 Connections

- Use the Supabase **session-mode pooler**, not transaction mode. The direct host is IPv6-only, and EC2 default VPCs are often IPv4-only.

## 8. Reliability and Failure Handling

### 8.1 Mechanisms in the ingestion worker

1. **OAuth token handler:** cache the token, refresh ~2 minutes before expiry, force one refresh on HTTP 401.
2. **Resilience pipeline** per request: timeout (10 s) → retry (exponential backoff with jitter, max 4 attempts, only on 5xx/timeouts) → circuit breaker (half-opens after 60 s).
3. **429 handling:** wait for `X-Rate-Limit-Retry-After-Seconds`; don't retry blindly.
4. **Bounded buffer:** poll results go into a bounded `Channel<T>` (about 100 batches). If the database is down, batches queue. If the buffer fills, the oldest batch is dropped and counted.
5. **Idempotent writes:** replays after a crash never create duplicates.
6. **Graceful shutdown:** on SIGTERM, stop polling, flush the buffer, exit.
7. **Health endpoints:** `/health/live` (process running) and `/health/ready` (database reachable, last successful poll within 2 minutes).

### 8.2 Failure-mode table (goes in the README; fill in "Measured" in Phase 4)

| Failure | Detection | Automatic response | Data loss? | Measured |
|---|---|---|---|---|
| OpenSky 5xx / timeout | HTTP status / timeout | Retry with backoff; circuit breaker | One poll interval at most | TBD |
| OpenSky 429 | HTTP 429 | Wait for Retry-After | Gap until credits refill | TBD |
| Expired OAuth token | HTTP 401 | Refresh token, retry once | None | TBD |
| Database unreachable | Npgsql exception | Buffer in channel, retry writes | None until buffer is full | TBD |
| Database full (500 MB) | Write rejected / size alert | Alert; archiver runs early | Possible | TBD |
| Inference service down | Docker healthcheck | Restart container; ingestion unaffected | None (scores catch up) | TBD |
| Worker crash | Docker healthcheck | Restart; idempotent replays | ≤ one poll interval | TBD |
| EC2 reboot | — | Compose services start on boot | Gap during reboot | TBD |
| Bad deployment | Readiness check fails | Pipeline redeploys previous image tag | None | TBD |
| Malformed OpenSky records | Validation | Dropped, counted in `ingestion_runs` | Invalid rows only | — |

## 9. Roadmap and Tasks

About 10–15 hours a week. **Sequencing rule:** ingestion must be running 24/7 by the end of Phase 2, because the model needs weeks of data before training.

### Phase 0 — Foundation ✅

Done (P0.1–P0.7): repo, OpenSky and Supabase accounts, worker scaffold, local Postgres, first ADRs, README stub.

### Phase 1 — Ingestion and Storage

- [x] **P1.1** SQL migrations (DbUp): `state_vectors` partitioned table, staging table, `ingestion_runs`, `anomaly_scores`, `archive_log`.
- [x] **P1.2** Enable `pg_partman` + `pg_cron`; daily partitions and 48-hour retention (detach, not drop).
- [ ] **P1.3** Typed OpenSky client with OAuth token handler (8.1 item 1).
- [ ] **P1.4** Resilience pipeline: timeout, retry with jitter, circuit breaker, 429 handling.
- [ ] **P1.5** Parsing and validation: map OpenSky's array format to a typed record; drop and count invalid rows (5.1).
- [ ] **P1.6** Bounded channel between poller and writer; writer does binary COPY to staging + `INSERT ... ON CONFLICT DO NOTHING`.
- [ ] **P1.7** Write one `ingestion_runs` row per cycle.
- [ ] **P1.8** Health endpoints, graceful shutdown, Serilog JSON logging, options validation on startup.
- [ ] **P1.9** Tests: unit tests for parsing/validation; integration tests with Testcontainers; WireMock.Net tests for 429, 500, 401 and timeouts.
- [ ] **P1.10** Run against Supabase for 24 hours locally; measure MB/day and record it in the Status block.

**Exit:** 24-hour run against Supabase with no manual intervention; all failure tests pass; database size within budget.

### Phase 2 — Ops and Deployment

- [ ] **P2.1** Multi-stage Dockerfile for the worker (non-root user, `HEALTHCHECK`).
- [ ] **P2.2** Terraform: S3 state backend, VPC/security group, EC2 t3.small with instance role, ECR repos with lifecycle policies, S3 archive bucket, SSM parameters, AWS Budget alarm.
- [ ] **P2.3** EC2 bootstrap (user data): install Docker + Compose, pull secrets from SSM, enable services on boot.
- [ ] **P2.4** GitHub Actions `ci.yml`: build + test (.NET and Python), `ruff`, Docker build, Trivy scan. Runs on every PR.
- [ ] **P2.5** GitHub OIDC trust with AWS (no access keys).
- [ ] **P2.6** `deploy.yml`: on merge to `main`, push images to ECR tagged by git SHA, deploy via SSM Run Command, wait for readiness, roll back to the previous tag on failure.
- [ ] **P2.7** Grafana with provisioned datasource and dashboards: live aircraft map, polls/min, records inserted, credits remaining, error rate, database size.
- [ ] **P2.8** Deploy. Start the 14-day continuous-run clock.

**Exit:** a merge to `main` deploys automatically; ingestion runs on EC2; Grafana shows live data; budget alarm is active.

### Phase 3 — Archival and Intelligence

- [ ] **P3.1** Archiver: export detached partitions to S3 Parquet (`s3://.../state_vectors/date=YYYY-MM-DD/`), verify row counts, log to `archive_log`, then drop the partition.
- [ ] **P3.2** Data preparation (`training/`): load Parquet; filter `on_ground`; dedupe; segment into flights (gap > 10 min = new segment); convert lat/lon to local east/north metres; encode heading as sin/cos; build windows of 20 steps (~10 minutes); normalise with training-set statistics only.
- [ ] **P3.3** Split by time (first 70% of days train, next 15% validation, last 15% test). Never random, to avoid leakage.
- [ ] **P3.4** Rule-based baseline: impossible speed, altitude change rate, emergency squawks (7500/7600/7700), large position jumps.
- [ ] **P3.5** Classical baseline: Isolation Forest on window summary features.
- [ ] **P3.6** GRU autoencoder in PyTorch; score = reconstruction error; threshold = 99th percentile of validation scores.
- [ ] **P3.7** Evaluation with injected synthetic anomalies on the test set (altitude drop, heading reversal, position jump, speed spike, circling). Report precision, recall, PR-AUC and alerts per hour for all three approaches.
- [ ] **P3.8** Export to ONNX; upload to S3 with a metadata JSON (version, training date range, normalisation stats, threshold, metrics).
- [ ] **P3.9** Inference service: FastAPI + ONNX Runtime; 60-second scoring loop writing to `anomaly_scores`; `POST /score` with API key; `/health`; loads the model by version from S3.
- [ ] **P3.10** Add inference + archiver to CI/CD and Compose; add Grafana panels for anomalies.
- [ ] **P3.11** Write `docs/model-card.md`: data, method, metrics, limitations.

**Exit:** model served on EC2; anomaly scores in Grafana; evaluation compares the model against both baselines.

### Phase 4 — Proof and Documentation

- [ ] **P4.1** Chaos demo: stop the database connection, trigger 429s (WireMock locally), kill the inference container. Record recovery times in the failure-mode table.
- [ ] **P4.2** Benchmark insert throughput (rows/s) and p95 write latency.
- [ ] **P4.3** Final architecture diagram.
- [ ] **P4.4** README: pitch, diagram, quick start, design decisions, failure-mode table, measured numbers, model evaluation summary, screenshots, demo video link.
- [ ] **P4.5** Record a 3–5 minute demo video.
- [ ] **P4.6** Fill in [interview-notes.md](interview-notes.md) with real numbers.
- [ ] **P4.7** Confirm the 14-day continuous run and publish uptime/gap stats.

**Exit:** all six success criteria in Section 2 are met.

**Stretch (only after Phase 4):** OpenTelemetry + Prometheus, ARM64 images on Graviton, automated weekly retraining, blue/green deployment, a second bounding box.

## 10. Repository Structure

See the layout table in [CLAUDE.md](../CLAUDE.md).

## 11. Decision Log

| # | Decision | Instead of | Reason |
|---|---|---|---|
| D1 | .NET 10 | .NET 8 | .NET 8 support ends November 2026; .NET 10 is the current LTS |
| D2 | Native partitioning + pg_partman | TimescaleDB | TimescaleDB is deprecated on Supabase Postgres 17 |
| D3 | One 5°×5° region, 30 s polling | Global coverage | Fits the OpenSky credit budget and Supabase's 500 MB |
| D4 | 48 h hot data in Postgres, older data in S3 Parquet | Everything in Postgres | Storage limit; Parquet is also the training format |
| D5 | Ingestion and inference decoupled through Postgres | Ingestion calls the AI service | AI failures must never stop data collection |
| D6 | `/score` endpoint for on-demand use only | No API | Shows API design without coupling |
| D7 | Train offline, serve with ONNX Runtime | PyTorch on EC2 | Small image, low memory on a 2 GB instance |
| D8 | Rule-based + Isolation Forest baselines | Model only | Without baselines the model can't be judged |
| D9 | Docker Compose on one EC2 instance | Kubernetes / ECS | Enough for the load; simpler to explain |
| D10 | No message broker | Kafka / RabbitMQ | Volume doesn't justify one |
| D11 | GitHub OIDC → AWS, SSM for deploy and secrets | Access keys, SSH deploys | No static credentials, no open SSH port |
| D12 | Grafana as the UI | React frontend | Faster, and doubles as observability |
| D13 | All tables in a `telemetry` schema (ADR 0003) | `public` schema + RLS | Supabase exposes `public` through its REST API |

Add new decisions here as D14, D15, …

## 12. Constraints and Verified Facts (checked September 2026)

**OpenSky**
- Basic auth was removed on 18 March 2026; OAuth2 client credentials are required.
- 4,000 credits/day per endpoint group. `/states/all` costs 1 credit for boxes ≤ 25 sq°, up to 4 for global.
- 429 responses include `X-Rate-Limit-Retry-After-Seconds`.

**Supabase (free plan)**
- 500 MB per project; writes rejected beyond it. No backups.
- Projects pause after 7 days of inactivity (constant ingestion should prevent this — verify).
- TimescaleDB is not available on Postgres 17; pg_partman is the replacement.

**AWS (accounts created after 15 July 2025)**
- Free plan: $100 credits at signup, up to $100 more from onboarding tasks. It closes after 6 months or when credits run out.
- So: set a budget alarm on day one, keep the local `docker compose up` path and the demo video working, and decide by month 5 whether to move to a paid plan.
