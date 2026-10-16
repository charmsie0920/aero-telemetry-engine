using System.Threading.Channels;
using Npgsql;

namespace Ingestion.Worker.Pipeline;

/// <summary>
/// Takes batches off the buffer and hands them to <see cref="BatchWriter"/>,
/// retrying while the database is unreachable and draining the buffer on
/// shutdown (PRD 8.1 items 4 and 6).
/// </summary>
public sealed class WriterService(
    BatchWriter writer,
    Channel<PollBatch> buffer,
    ILogger<WriterService> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reads WITHOUT the stopping token: on shutdown the poller completes
        // the channel, and this loop keeps going until the buffer is empty.
        await foreach (var batch in buffer.Reader.ReadAllAsync(CancellationToken.None))
        {
            if (!await WriteWithRetryAsync(batch, stoppingToken))
            {
                break;
            }
        }

        var unflushed = buffer.Reader.Count;
        if (unflushed > 0)
        {
            logger.LogError("Shut down with {Unflushed} batches still in the buffer", unflushed);
        }

        logger.LogInformation("Database writer stopped");
    }

    /// <summary>
    /// Retries the same batch until it is written. Returns false only if the
    /// host is shutting down while the database is still unreachable.
    /// </summary>
    private async Task<bool> WriteWithRetryAsync(PollBatch batch, CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            try
            {
                var inserted = await writer.WriteAsync(batch);
                LogCycle(batch, inserted);
                return true;
            }
            catch (NpgsqlException ex) when (ex.IsTransient)
            {
                // Database down or restarting. The batch stays in hand and the
                // poller keeps filling the buffer behind it.
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Database still unreachable at shutdown; batch {RunId} not written", batch.RunId);
                    return false;
                }

                logger.LogWarning(
                    "Database unreachable ({Message}); retrying batch {RunId} in {DelaySeconds} s. {Buffered} batches buffered",
                    ex.Message,
                    batch.RunId,
                    delay.TotalSeconds,
                    buffer.Reader.Count);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutdown requested while waiting: try once more, then give up.
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
            catch (NpgsqlException ex)
            {
                // A permanent error (bad SQL, missing table, bad data). Retrying
                // would block every batch behind this one, so skip it loudly.
                logger.LogError(ex, "Batch {RunId} rejected by the database and skipped", batch.RunId);
                return true;
            }
        }
    }

    private void LogCycle(PollBatch batch, int inserted)
    {
        if (batch.Error is not null)
        {
            logger.LogWarning("Poll {RunId} failed: {Error}", batch.RunId, batch.Error);
            return;
        }

        logger.LogInformation(
            "Poll {RunId}: {Received} received, {Valid} valid, {Inserted} new, {Duplicates} duplicates, {DurationMs} ms, {Credits} credits left",
            batch.RunId,
            batch.RecordsReceived,
            batch.Records.Count,
            inserted,
            batch.Records.Count - inserted,
            batch.DurationMs,
            batch.CreditsRemaining);
    }
}
