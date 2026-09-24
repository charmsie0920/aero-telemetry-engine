-- Hot storage for aircraft positions (PRD 7.1). Holds ~48 hours; older
-- partitions are detached by pg_partman and archived to S3 by the archiver.
--
-- Everything lives in the `telemetry` schema, not `public`: Supabase exposes
-- `public` through its auto-generated REST API, and nothing here should be
-- reachable with the anon key (ADR 0003).

CREATE SCHEMA IF NOT EXISTS telemetry;

CREATE TABLE telemetry.state_vectors (
  icao24           text             NOT NULL,  -- lowercase hex transponder address
  observed_at      timestamptz      NOT NULL,  -- OpenSky time_position; partition key
  last_contact     timestamptz,
  callsign         text,                       -- trimmed by the worker
  origin_country   text,
  latitude         double precision NOT NULL,  -- rows without a position are dropped in validation
  longitude        double precision NOT NULL,
  baro_altitude_m  real,
  geo_altitude_m   real,
  velocity_ms      real,
  true_track_deg   real,
  vertical_rate_ms real,
  on_ground        boolean          NOT NULL,
  squawk           text,
  position_source  smallint,
  ingested_at      timestamptz      NOT NULL DEFAULT now(),

  -- Idempotent writes: a stale position repeated across polls, or a batch
  -- replayed after a crash, hits this key and is skipped by ON CONFLICT DO
  -- NOTHING. A partitioned table's primary key must include the partition key.
  CONSTRAINT state_vectors_pkey PRIMARY KEY (icao24, observed_at)
) PARTITION BY RANGE (observed_at);

-- Rows arrive in time order, so a BRIN index answers "last N minutes" range
-- scans at a tiny fraction of a B-tree's size.
CREATE INDEX state_vectors_observed_at_brin
  ON telemetry.state_vectors USING brin (observed_at);

-- No partitions are created here. Daily partitions are created and detached
-- by pg_partman (P1.2). Until then, inserts fail with "no partition found".
