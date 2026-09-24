-- One row per poll cycle (PRD 7.1, P1.7). Feeds the pipeline-health panels
-- in Grafana. ~2,880 rows/day at 30s polling, so it is small enough not to
-- need partitioning.

CREATE TABLE telemetry.ingestion_runs (
  run_id            uuid        NOT NULL DEFAULT gen_random_uuid(),  -- also the log correlation ID
  started_at        timestamptz NOT NULL,
  duration_ms       integer,
  http_status       smallint,             -- null when no response came back (timeout, DNS, ...)
  records_received  integer     NOT NULL DEFAULT 0,
  records_valid     integer     NOT NULL DEFAULT 0,
  records_inserted  integer     NOT NULL DEFAULT 0,
  records_duplicate integer     NOT NULL DEFAULT 0,
  retries           integer     NOT NULL DEFAULT 0,
  credits_remaining integer,              -- X-Rate-Limit-Remaining, when present
  error             text,

  CONSTRAINT ingestion_runs_pkey PRIMARY KEY (run_id)
);

CREATE INDEX ingestion_runs_started_at_idx
  ON telemetry.ingestion_runs (started_at);
