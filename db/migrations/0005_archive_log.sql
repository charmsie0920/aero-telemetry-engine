-- One row per archived partition (PRD 7.1, P3.1). The archiver only drops a
-- partition after row_count_db = row_count_parquet, so this table is the
-- audit trail showing that no data was lost.

CREATE TABLE telemetry.archive_log (
  partition_name    text        NOT NULL,
  row_count_db      bigint      NOT NULL,
  row_count_parquet bigint      NOT NULL,
  s3_key            text        NOT NULL,
  archived_at       timestamptz NOT NULL DEFAULT now(),
  dropped           boolean     NOT NULL DEFAULT false,

  CONSTRAINT archive_log_pkey PRIMARY KEY (partition_name)
);
