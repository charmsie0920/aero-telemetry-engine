-- Written by the inference service every 60s (PRD 7.1, P3.9).

CREATE TABLE telemetry.anomaly_scores (
  icao24        text        NOT NULL,
  window_end    timestamptz NOT NULL,  -- last timestamp in the scored window
  score         real        NOT NULL,  -- reconstruction / prediction error
  is_anomaly    boolean     NOT NULL,  -- score above the model's threshold
  rule_flags    text[]      NOT NULL DEFAULT '{}',  -- baseline rules that fired
  model_version text        NOT NULL,
  scored_at     timestamptz NOT NULL DEFAULT now(),

  -- Same idempotency idea as state_vectors: if the scorer restarts and
  -- re-scores a window with the same model, the second write is a no-op.
  -- A new model version gets its own row, so models can be compared.
  CONSTRAINT anomaly_scores_pkey PRIMARY KEY (icao24, window_end, model_version)
);

CREATE INDEX anomaly_scores_scored_at_idx
  ON telemetry.anomaly_scores (scored_at);
