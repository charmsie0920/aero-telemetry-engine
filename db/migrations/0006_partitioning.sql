-- Daily partitions and 48-hour retention for telemetry.state_vectors
-- (PRD 7.2, P1.2).
--
-- pg_partman creates the partitions and detaches the old ones. pg_cron is the
-- scheduler that runs pg_partman's maintenance every hour. Both ship with
-- Supabase; the local image in db/postgres/Dockerfile installs them.

-- pg_partman keeps its own tables and functions in a `partman` schema, apart
-- from the data in `telemetry`.
CREATE SCHEMA IF NOT EXISTS partman;
CREATE EXTENSION IF NOT EXISTS pg_partman WITH SCHEMA partman;

-- pg_cron always installs into its own `cron` schema. It only works in the
-- database named by the server setting cron.database_name.
CREATE EXTENSION IF NOT EXISTS pg_cron;

-- Hand the table to pg_partman. This creates one partition per day: today,
-- the 2 days before it and the 2 days after it.
SELECT partman.create_parent(
  p_parent_table  := 'telemetry.state_vectors',
  p_control       := 'observed_at',
  p_interval      := '1 day',
  p_premake       := 2,
  -- Catch-all partition for rows whose timestamp matches no daily partition.
  -- Without it, one bad timestamp would fail the whole batch insert. It
  -- should stay empty; partman.check_default() lists rows that land in it.
  p_default_table := true
);

UPDATE partman.part_config
SET
  -- A partition leaves the table once all of its rows are older than this.
  retention                = '48 hours',
  -- Detach, do not drop: the old partition stays behind as an ordinary table
  -- (telemetry.state_vectors_pYYYYMMDD) until the archiver has copied it to
  -- S3 and drops it (P3.1).
  retention_keep_table     = true,
  -- Keep creating future partitions even when no data is arriving. Otherwise
  -- a worker outage longer than 2 days would end with no partition for "now".
  infinite_time_partitions = true
WHERE parent_table = 'telemetry.state_vectors';

-- Every hour at minute 5: create upcoming partitions, detach expired ones.
-- Scheduling under a job name makes this safe to run again: the same name
-- replaces the existing job.
SELECT cron.schedule(
  'partman-maintenance',
  '5 * * * *',
  $$CALL partman.run_maintenance_proc()$$
);
