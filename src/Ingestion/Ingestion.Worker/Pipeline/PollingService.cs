using System.Diagnostics;
using System.Threading.Channels;
using Ingestion.Core;
using Ingestion.Worker.Configuration;
using Ingestion.Worker.OpenSky;
using Microsoft.Extensions.Options;

namespace Ingestion.Worker.Pipeline;

/// <summary>
/// Polls OpenSky on a fixed interval and puts each result into the buffer.
/// It never touches the database, so a database outage never stops polling.
/// </summary>
public sealed class PollingService(
    OpenSkyClient openSky,
    Channel<PollBatch> buffer,
    PipelineState state,
    IOptions<OpenSkyOptions> options,
    TimeProvider timeProvider,
    ILogger<PollingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.PollIntervalSeconds);
        logger.LogInformation(
            "Polling OpenSky every {IntervalSeconds} s for a {Area:F1} sq deg box",
            interval.TotalSeconds,
            options.Value.BoundingBoxAreaSquareDegrees);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var startedAt = timeProvider.GetUtcNow();
                var stopwatch = Stopwatch.StartNew();

                var result = await openSky.FetchStatesAsync(stoppingToken);
                stopwatch.Stop();

                if (result.Succeeded)
                {
                    state.RecordSuccessfulPoll(startedAt);
                    LogRejected(result.Parsed!);
                }

                // Always succeeds: a full buffer drops its oldest batch instead.
                buffer.Writer.TryWrite(new PollBatch(
                    RunId: Guid.CreateVersion7(startedAt),
                    StartedAt: startedAt,
                    DurationMs: (int)stopwatch.ElapsedMilliseconds,
                    HttpStatus: result.HttpStatus,
                    RecordsReceived: result.Parsed?.Received ?? 0,
                    Records: result.Parsed?.Valid ?? [],
                    Retries: result.Retries,
                    CreditsRemaining: result.CreditsRemaining,
                    Error: result.Error));

                // Keep a steady cadence: subtract the time the poll took. After
                // a 429, wait as long as OpenSky asked (PRD 8.1 item 3).
                var wait = result.RetryAfter is { } retryAfter && retryAfter > interval ? retryAfter : interval;
                wait -= stopwatch.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            // Tells the writer no more batches are coming, so it can flush
            // what is left and exit (PRD 8.1 item 6).
            buffer.Writer.TryComplete();
            logger.LogInformation("Polling stopped");
        }
    }

    private void LogRejected(ParseResult parsed)
    {
        if (parsed.Invalid > 0)
        {
            logger.LogDebug(
                "Dropped {Invalid} of {Received} records: {Reasons}",
                parsed.Invalid,
                parsed.Received,
                string.Join(", ", parsed.Rejected.Select(r => $"{r.Key}={r.Value}")));
        }
    }
}
