using Ingestion.Core;

namespace Ingestion.Worker.Pipeline;

/// <summary>
/// One poll cycle's output, passed from <see cref="PollingService"/> to
/// <see cref="WriterService"/> through the buffer. Failed polls are batches
/// too (with no records and <see cref="Error"/> set), so every cycle gets a
/// row in telemetry.ingestion_runs.
/// </summary>
public sealed record PollBatch(
    Guid RunId,
    DateTimeOffset StartedAt,
    int DurationMs,
    int? HttpStatus,
    int RecordsReceived,
    IReadOnlyList<StateVector> Records,
    int Retries,
    int? CreditsRemaining,
    string? Error);
