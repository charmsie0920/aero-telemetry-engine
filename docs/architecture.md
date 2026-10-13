# Architecture

The architecture is three separate pictures, because they happen on different timelines:

| Diagram | When it happens |
|---|---|
| **A. How data flows** (the main one) | Continuously, while the system runs |
| **B. How the model is made** | Occasionally, offline on a laptop |
| **C. How code gets to the server** | On every push to `main` |

## A. How data flows

Arrows show which way the data moves. The technology is in *italics* under each box.

```mermaid
flowchart LR
    SKY["OpenSky Network<br/><i>public live flight data</i>"]

    subgraph SERVER["Our server — one AWS EC2 machine"]
        COLLECT["① Collector<br/><i>.NET</i><br/>fetches and cleans positions"]
        DETECT["③ Anomaly Detector<br/><i>Python + ML model</i><br/>flags unusual flights"]
        ARCHIVE["④ Archiver<br/><i>Python</i><br/>moves old data out"]
        DASH["⑤ Dashboard<br/><i>Grafana</i>"]
    end

    DB[("② Database<br/><i>Supabase Postgres</i><br/>last 48 h of flights<br/>+ anomaly flags")]
    LAKE[("Long-term storage<br/><i>AWS S3</i><br/>everything older")]
    USER(("Viewer"))

    SKY -->|"every 30 s:<br/>aircraft positions"| COLLECT
    COLLECT -->|"saves positions"| DB
    DB -->|"every 60 s:<br/>recent flight paths"| DETECT
    DETECT -->|"anomaly scores"| DB
    DB -->|"daily:<br/>data older than 48 h"| ARCHIVE
    ARCHIVE -->|"compressed files"| LAKE
    DB -->|"flights, anomalies,<br/>health stats"| DASH
    DASH -->|"live map + alerts"| USER
```

**In plain words:**

1. Every 30 seconds the **Collector** asks OpenSky where every aircraft in our region is. It throws out bad records and saves the rest.
2. The **Database** keeps only the last 48 hours, so it stays small and fast.
3. Every 60 seconds the **Anomaly Detector** reads each aircraft's recent path, scores how unusual it looks, and writes the score back.
4. Once a day the **Archiver** copies data older than 48 hours to cheap S3 storage, checks the copy is complete, and then deletes it from the database.
5. The **Dashboard** is what the viewer sees: a live map of aircraft, a list of flagged flights, and panels showing whether the pipeline is healthy.

**The key design decision:** the Collector and the Detector never talk to each other directly. They only share the database. If the ML side crashes, data collection carries on, and scoring catches up when the Detector comes back.

## B. How the model is made (offline, occasionally)

```mermaid
flowchart LR
    LAKE[("Long-term storage<br/><i>AWS S3</i><br/>historical flights")]
    TRAIN["Train and evaluate model<br/><i>PyTorch, on a laptop</i>"]
    MODEL[("Model file<br/><i>stored in S3</i>")]
    DETECT["Anomaly Detector<br/><i>on the server</i>"]

    LAKE -->|"training data"| TRAIN
    TRAIN -->|"exports trained model"| MODEL
    MODEL -->|"loaded at startup"| DETECT
```

Training never runs on the server. The server only loads the finished model file.

## C. How code gets to the server (on every push)

```mermaid
flowchart LR
    DEV["Developer<br/>pushes code"]
    CI["GitHub Actions<br/>build → test → security scan"]
    REG[("Image registry<br/><i>AWS ECR</i>")]
    SERVER["Our server<br/><i>AWS EC2</i>"]
    TF["Terraform<br/><i>infrastructure as code</i>"]

    DEV --> CI
    CI -->|"uploads container images"| REG
    CI -->|"tells server to update"| SERVER
    REG -->|"server pulls new images"| SERVER
    TF -->|"creates server, storage,<br/>permissions (one-time)"| SERVER
```

## Glossary

- **State vector:** one snapshot of an aircraft's position, altitude, speed, heading, etc. at a moment in time.
- **icao24:** unique 24-bit transponder address of an aircraft.
- **Squawk:** 4-digit transponder code; 7500/7600/7700 indicate hijack/radio failure/emergency.
- **Partition:** a physical sub-table holding a time range of data; dropping a partition is instant compared to deleting rows.
- **BRIN index:** a very small index type suited to naturally time-ordered data.
- **Circuit breaker:** stops calling a failing service for a period, then tests it again.
- **ADR:** Architecture Decision Record — a short document explaining one design decision.
- **ONNX:** a portable model format that can run without PyTorch installed.
