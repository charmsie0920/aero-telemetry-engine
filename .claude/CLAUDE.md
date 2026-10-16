# CLAUDE.md

Context for Claude Code when working in this repository.

## What this project is

`aero-telemetry-engine` is a portfolio project: a near-real-time pipeline that polls live aircraft positions from the OpenSky Network every 30 seconds, stores them in partitioned PostgreSQL, archives old data to S3, and scores flight paths for anomalies. It is built to be explained in job interviews, so understanding matters more than speed.

**[docs/PRD.md](docs/PRD.md) is the single source of truth.** Read these parts before doing any task:

- **Section 0 (Current Status)**: the current phase, current task, blockers and open questions.
- **Section 9 (Roadmap)**: task IDs such as `P1.2`. When the owner says "do P1.3" or "next step", this is what they mean.
- **Section 11 (Decision Log)** and [docs/adr/](docs/adr/): settled decisions. Don't re-argue them unless a fact has changed.
- **Section 3 (Non-Goals)**: no Kafka, no Kubernetes, no React frontend, no extra technologies.

## How to work with the owner (most important section)

The owner is new to .NET, Docker, Postgres and Supabase, and is building this project to learn. Code they can't explain is worth nothing to them.

### When asked to complete a phase, task or step

**Do not edit the project files yourself. Teach instead.** Give a walkthrough the owner follows by hand:

1. **Start with the goal.** Two or three plain sentences: what this step adds, why the project needs it, and how to tell it worked.
2. **List every file that will be touched** before any code, marked `NEW` or `EDIT`, with a one-line reason for each.
3. **Number the steps in the order to do them.** Each step says exactly one thing to do.
4. **Give the FULL code for every file.**
   - New file: the complete contents, top to bottom, with the full path from the repo root as a heading (for example `db/migrations/0006_partitioning.sql`).
   - Existing file: the complete updated file, so it can be pasted over the old one. If the file is long and the change is small, show the exact existing lines to find and the exact lines to replace them with, and say where in the file they are.
   - Never write `// ... rest unchanged`, `// existing code here`, or any other placeholder. Never leave a part "as an exercise".
5. **Say where everything goes.** For each snippet: which file, whether to create it or replace its contents, and for a partial edit, which lines it goes after or replaces.
6. **Give every terminal command in full**, in its own code block, with the folder to run it from. The shell is PowerShell on Windows.
7. **Explain the code after showing it.** Walk through what each part does and why it is written that way. Define each technical term the first time it appears, and link the concept to a file already in this repo where possible.
8. **End with how to verify.** The exact command to run and the output to expect, plus the one or two most likely errors and what each means.
9. **Finish with the PRD update.** Give the exact new text for the Status block in PRD Section 0 and the checkbox to tick in Section 9.

Before writing a walkthrough, read the current files so the code fits what is really there, and check that the code is correct (for example by building it in the scratchpad). Don't hand over code that hasn't been checked.

For a whole phase, go one task at a time and stop after each so the owner can run it. Don't deliver ten tasks in one message.

The owner can override this at any time with something like "just do it" or "apply it yourself". That applies to that request only.

### When explaining or answering questions

- Plain language first, then the precise term.
- Point to the real file and line in this repo that does the thing.
- Prefer a small step the owner can run and see over a long explanation.
- Suggest the simplest solution that is easy to justify in an interview.

## Repository layout

| Path | What it is |
|---|---|
| `src/Ingestion/Ingestion.Worker/` | The collector: polls OpenSky every 30 s (`OpenSky/`), buffers batches and writes them to Postgres (`Pipeline/`), serves `/health/live` and `/health/ready` on port 8080. |
| `src/Ingestion/Ingestion.Migrations/` | One-shot console app that applies the SQL migrations with DbUp. |
| `src/Ingestion/Ingestion.Core/` | Parsing and validation of OpenSky responses. Pure logic, no I/O. |
| `src/Ingestion/Ingestion.Tests/` | Empty placeholder (tests come in P1.9). |
| `db/migrations/` | Numbered SQL files (`0001_...sql`). The single source of truth for the schema. |
| `db/postgres/Dockerfile` | The local Postgres image: Postgres 17 plus `pg_partman` and `pg_cron`. |
| `docker-compose.yml` | Runs the local Postgres. Other services are commented out until they exist. |
| `docs/PRD.md` | Spec, roadmap, decision log. Keep it short; don't renumber its sections (code comments cite them). |
| `docs/architecture.md`, `docs/diagrams/` | Diagrams, plain-English walkthrough, glossary. |
| `docs/interview-notes.md` | Target roles, resume bullets, interview questions. |
| `docs/adr/` | Decision records. |
| `services/`, `training/`, `infra/`, `grafana/` | Empty placeholders for Phases 2 and 3. |

## Commands

Run from the repo root, in PowerShell.

```powershell
docker compose up -d                                      # start local Postgres (host port 5433)
dotnet build                                              # build everything
dotnet run --project src/Ingestion/Ingestion.Migrations   # apply db/migrations
dotnet run --project src/Ingestion/Ingestion.Worker       # run the collector; Ctrl+C to stop
curl.exe http://localhost:8080/health/ready               # is the running worker healthy?
docker compose exec postgres psql -U aero -d aero         # open a SQL prompt in the local database
```

There are no tests yet.

## Conventions and things that are easy to get wrong

- **.NET 10**, pinned in `global.json`. Shared build settings are in `Directory.Build.props`; **warnings are errors**.
- **Package versions live only in `Directory.Packages.props`.** A `.csproj` references a package with no `Version` attribute.
- **Every table is in the `telemetry` schema**, never `public` (ADR 0003). Always write the schema name: `telemetry.state_vectors`.
- **Migrations are append-only.** Never edit a migration that has been applied; add the next numbered file. DbUp records applied scripts in `telemetry.schemaversions` and skips them.
- **Migrations are embedded from `db/migrations/`** into the migrator at build time. Don't copy SQL files into the project, and don't mount them into Postgres's `/docker-entrypoint-initdb.d`.
- **Two separate databases.** `LOCAL_POSTGRES_CONNECTION_STRING` (Docker, used when `DOTNET_ENVIRONMENT=Development`) and `POSTGRES_CONNECTION_STRING` (Supabase). `dotnet run` uses Development through `launchSettings.json`. Never point local work at Supabase by accident.
- **Local Postgres is on host port 5433**, because a native Windows Postgres owns 5432. Another project's container, `recall-postgres`, also uses 5433 and starts with Docker; stop it first (`docker stop recall-postgres`).
- **Supabase needs the session-mode pooler connection string.** The direct host is IPv6-only and unreachable from this machine.
- **Secrets live in `.env`** (git-ignored). Never print, commit or paste its values. New variables also go in `.env.example` with an empty value.
- **Formatting** follows `.editorconfig`: LF line endings, 4 spaces for C#, 2 spaces for SQL, YAML, JSON and Markdown.
- **Say "near-real-time"**, never "real-time". Data arrives every 30 seconds.
- **Facts about outside services** (OpenSky limits, Supabase and AWS free tiers, library versions) in PRD Section 12 were checked in September 2026. Check again before relying on them.

## Git

- Commit messages use a type prefix: `feat:`, `fix:`, `docs:`, `chore:`.
- Commit or push only when asked.

## Keeping this file useful

Keep this file short and stable. Project status belongs in PRD Section 0, not here. Update this file only when a convention, a command or the way of working changes.
