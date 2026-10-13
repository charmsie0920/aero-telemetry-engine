# Interview Notes

Fill in the bracketed numbers in Phase 4 (PRD task P4.6).

**One-sentence pitch:** "A fault-tolerant, near-real-time aviation telemetry pipeline in .NET and PostgreSQL, with a decoupled PyTorch anomaly-detection service, deployed to AWS through Terraform and CI/CD, with measured recovery from failures."

## What the project proves, by role

| Target role | What the project proves | Where it's proven |
|---|---|---|
| Backend / systems engineer (defence, aerospace) | Reliable long-running services, fault tolerance, clean failure handling | Ingestion worker (Phases 1, 4) |
| Data / research infrastructure | Time-series schema design, partitioning, retention, archival | Storage layer (Phases 1, 3) |
| ML engineer / ML infrastructure | Data cleaning, feature pipelines, model evaluation, model serving | AI service (Phase 3) |
| DevOps / cloud / forward-deployed | Containers, IaC, CI/CD, secrets management, observability, cost control | Ops layer (Phase 2) |

## Resume bullets (templates)

- Built a fault-tolerant .NET 10 ingestion service processing **[N] aircraft state vectors/day** from the OpenSky Network, with OAuth token management, circuit breaking, and rate-limit-aware backoff; recovered from database outages in **[X] s** with zero data loss.
- Designed a time-partitioned PostgreSQL schema with automated retention and S3 Parquet archival, sustaining **[N] rows/s** inserts within a 500 MB storage budget.
- Trained a GRU autoencoder in PyTorch for flight trajectory anomaly detection, achieving **[X] precision / [Y] recall** on injected anomalies versus **[A]/[B]** for a rule-based baseline; served via ONNX Runtime and FastAPI.
- Provisioned AWS infrastructure with Terraform and automated deployments via GitHub Actions with OIDC, Trivy image scanning, and automatic rollback on failed health checks.

## Questions to be ready for

1. Why is it "near-real-time" and not real-time? (API credit budget, polling interval.)
2. Why not TimescaleDB / Kafka / Kubernetes? (Decision Log.)
3. What happens when the database is down for 10 minutes? For 2 hours? (Buffer capacity, drop policy.)
4. How do you prevent duplicate rows? (PK + ON CONFLICT, staging table.)
5. How do you know the model works without labels? (Injected anomalies, baselines, time-based split.)
6. Why did you split data by time instead of randomly? (Leakage.)
7. How are secrets managed? (SSM, OIDC, nothing in images.)
8. What would you change to handle global coverage? (Licensed tier, more storage, possibly a broker at that point.)
