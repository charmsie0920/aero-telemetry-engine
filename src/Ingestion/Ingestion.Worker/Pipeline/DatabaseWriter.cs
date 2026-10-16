using System.Threading.Channels;
using Ingestion.Core;
using Npgsql;
using NpgsqlTypes;

namespace Ingestion.Worker.Pipeline;

/// <summary>
/// Takes batches off the buffer and writes them to Postgres (PRD 7.1 insert
/// path). Each batch is one transaction:
///   TRUNCATE staging -> binary COPY into staging ->
///   INSERT INTO state_vectors ... ON CONFLICT DO NOTHING ->
///   INSERT one ingestion_runs row -> COMMIT
/// </summary>
/// <remarks>
/// COPY is the fastest way to load rows into Postgres, but it cannot skip
/// duplicates, so rows land in the unlogged staging table first and the
/// INSERT ... ON CONFLICT moves them across. Because everything commits
/// together, a batch is either fully written (data + run row) or not at all,
/// and writing the same batch twice changes nothing.
/// </remarks>
public sealed class DatabaseWriter(
    NpgsqlDataSource dataSource,
    Channel<PollBatch> buffer,
    ILogger<DatabaseWriter> logger) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private const string CopySql = """
        COPY telemetry.state_vectors_staging (
          icao24, observed_at, last_contact, callsign, origin_country,
          latitude, longitude, baro_altitude_m, geo_altitude_m, velocity_ms,
          true_track_deg, vertical_rate_ms, on_ground, squawk, position_source
        ) FROM STDIN (FORMAT BINARY)
        """;

    private const string MoveSql = """
        INSERT INTO telemetry.state_vectors (
          icao24, observed_at, last_contact, callsign, origin_country,
          latitude, longitude, baro_altitude_m, geo_altitude_m, velocity_ms,
          true_track_deg, vertical_rate_ms, on_ground, squawk, position_source
        )
        SELECT
          icao24, observed_at, last_contact, callsign, origin_country,
          latitude, longitude, baro_altitude_m, geo_altitude_m, velocity_ms,
          true_track_deg, vertical_rate_ms, on_ground, squawk, position_source
        FROM telemetry.state_vectors_staging
        ON CONFLICT (icao24, observed_at) DO NOTHING
        """;

    private const string RunSql = """
        INSERT INTO telemetry.ingestion_runs (
          run_id, started_at, duration_ms, http_status, records_received,
          records_valid, records_inserted, records_duplicate, retries,
          credits_remaining, error
        ) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
        ON CONFLICT (run_id) DO NOTHING
        """;

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
                var inserted = await WriteAsync(batch);
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

    private async Task<int> WriteAsync(PollBatch batch)
    {
        // No cancellation token: once started, a write should finish so the
        // transaction never ends half-done on shutdown.
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var inserted = 0;
        if (batch.Records.Count > 0)
        {
            await using (var truncate = new NpgsqlCommand("TRUNCATE telemetry.state_vectors_staging", connection, transaction))
            {
                await truncate.ExecuteNonQueryAsync();
            }

            await using (var importer = await connection.BeginBinaryImportAsync(CopySql))
            {
                foreach (var row in batch.Records)
                {
                    await WriteRowAsync(importer, row);
                }

                await importer.CompleteAsync();
            }

            await using var move = new NpgsqlCommand(MoveSql, connection, transaction);
            inserted = await move.ExecuteNonQueryAsync();
        }

        await using (var run = new NpgsqlCommand(RunSql, connection, transaction))
        {
            run.Parameters.Add(new NpgsqlParameter { Value = batch.RunId });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.StartedAt });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.DurationMs });
            run.Parameters.Add(new NpgsqlParameter { Value = DbValue(batch.HttpStatus is { } status ? (short)status : null), NpgsqlDbType = NpgsqlDbType.Smallint });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.RecordsReceived });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.Records.Count });
            run.Parameters.Add(new NpgsqlParameter { Value = inserted });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.Records.Count - inserted });
            run.Parameters.Add(new NpgsqlParameter { Value = batch.Retries });
            run.Parameters.Add(new NpgsqlParameter { Value = DbValue(batch.CreditsRemaining), NpgsqlDbType = NpgsqlDbType.Integer });
            run.Parameters.Add(new NpgsqlParameter { Value = DbValue(batch.Error), NpgsqlDbType = NpgsqlDbType.Text });
            await run.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return inserted;
    }

    private static async Task WriteRowAsync(NpgsqlBinaryImporter importer, StateVector row)
    {
        await importer.StartRowAsync();
        await importer.WriteAsync(row.Icao24, NpgsqlDbType.Text);
        await importer.WriteAsync(row.ObservedAt, NpgsqlDbType.TimestampTz);
        await WriteNullableAsync(importer, row.LastContact, NpgsqlDbType.TimestampTz);
        await WriteNullableAsync(importer, row.Callsign, NpgsqlDbType.Text);
        await WriteNullableAsync(importer, row.OriginCountry, NpgsqlDbType.Text);
        await importer.WriteAsync(row.Latitude, NpgsqlDbType.Double);
        await importer.WriteAsync(row.Longitude, NpgsqlDbType.Double);
        await WriteNullableAsync(importer, row.BaroAltitudeM, NpgsqlDbType.Real);
        await WriteNullableAsync(importer, row.GeoAltitudeM, NpgsqlDbType.Real);
        await WriteNullableAsync(importer, row.VelocityMs, NpgsqlDbType.Real);
        await WriteNullableAsync(importer, row.TrueTrackDeg, NpgsqlDbType.Real);
        await WriteNullableAsync(importer, row.VerticalRateMs, NpgsqlDbType.Real);
        await importer.WriteAsync(row.OnGround, NpgsqlDbType.Boolean);
        await WriteNullableAsync(importer, row.Squawk, NpgsqlDbType.Text);
        await WriteNullableAsync(importer, row.PositionSource, NpgsqlDbType.Smallint);
    }

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter importer, T? value, NpgsqlDbType type)
        where T : struct =>
        value is { } v ? importer.WriteAsync(v, type) : importer.WriteNullAsync();

    private static Task WriteNullableAsync(NpgsqlBinaryImporter importer, string? value, NpgsqlDbType type) =>
        value is null ? importer.WriteNullAsync() : importer.WriteAsync(value, type);

    // ADO.NET parameters use DBNull, not null, for SQL NULL.
    private static object DbValue(object? value) => value ?? DBNull.Value;

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
