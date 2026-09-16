# ADR 0002: Use .NET 10 for the ingestion worker

## Status
Accepted

## Context
The ingestion worker is a long-running .NET service. It polls OpenSky, validates records and writes them to Postgres. The project will run for many months and should stay on a supported runtime the whole time.

The two candidate runtimes were .NET 8 and .NET 10, and both are long-term support (LTS) releases. Support for .NET 8 ends in November 2026, which is only a few months into this project. .NET 10 is the current LTS release and is supported until November 2028.

## Decision
Build the ingestion worker (and any future .NET components) on .NET 10 (`net10.0`).

## Consequences
- The runtime stays supported for the whole project, so there is no forced upgrade partway through.
- The project can use current C# and library features, such as primary constructors and the latest `Microsoft.Extensions.*` packages.
- Container base images must be the .NET 10 images (`mcr.microsoft.com/dotnet/runtime:10.0` / `sdk:10.0`).
- Some third-party packages may lag behind on .NET 10 support. Check this before adding a dependency.
