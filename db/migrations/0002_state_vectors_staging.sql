-- Landing table for the worker's binary COPY (PRD 7.1 insert path).
--
-- COPY cannot do ON CONFLICT, so each batch is:
--   TRUNCATE staging -> COPY into staging ->
--   INSERT INTO state_vectors SELECT ... FROM staging ON CONFLICT DO NOTHING
-- in one transaction.
--
-- UNLOGGED: skips the WAL, so COPY is faster. The contents are lost on a
-- crash, which is fine because the table is empty outside a transaction.
-- No primary key, so duplicates inside a batch never fail the COPY; the
-- INSERT into state_vectors removes them.
--
-- A single shared table works because there is exactly one writer. A second
-- concurrent writer would need its own staging table (or a TEMP table).

CREATE UNLOGGED TABLE telemetry.state_vectors_staging (
  LIKE telemetry.state_vectors INCLUDING DEFAULTS
);
