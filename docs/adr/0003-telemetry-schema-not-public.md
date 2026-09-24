# ADR 0003: Keep all tables in a `telemetry` schema, not `public`

## Status
Accepted

## Context
Supabase automatically exposes the `public` schema through its REST Data API (PostgREST). Anyone who has the project's anon key can read or write a `public` table unless row-level security (RLS) is turned on for it. That key is designed to be shipped to browsers, so it should be treated as public.

This project has no public API (PRD Section 3). Only three things should touch the database: the ingestion worker, the inference service and the archiver. Grafana also reads from it. All of them connect with Postgres credentials, not through the Data API.

## Decision
Create every table in a dedicated `telemetry` schema. DbUp's journal table (`telemetry.schemaversions`) goes there too. Do not add `telemetry` to the schemas that Supabase's Data API exposes.

## Consequences
- Nothing is reachable through the anon key, even if someone forgets RLS on a new table. A new schema grants no privileges to `anon` or `authenticated` by default.
- Every SQL statement has to schema-qualify its tables (`telemetry.state_vectors`), or the connection has to set `search_path`. The migrations qualify everything explicitly.
- The migrator creates the schema before DbUp runs, because DbUp creates its journal table before it executes the first script.
- Local Postgres and Supabase end up with the same layout, so the code doesn't need to know which one it is talking to.
