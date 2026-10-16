using System.Diagnostics;
using System.Threading.Channels;
using Ingestion.Worker.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Ingestion.Tests.Database;

/// <summary>Shutdown behaviour of the writer loop (PRD 8.1 item 6).</summary>
[Collection(PostgresCollection.Name)]
public sealed class WriterServiceTests(PostgresFixture db) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await db.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Flushes_buffered_batches_on_shutdown()
    {
        var ct = TestContext.Current.CancellationToken;
        var buffer = Channel.CreateUnbounded<PollBatch>();
        for (var i = 0; i < 5; i++)
        {
            buffer.Writer.TryWrite(TestData.Batch(TestData.Vector($"fffff{i}")));
        }

        // What the poller does on shutdown: no more batches are coming.
        buffer.Writer.Complete();

        // Hold a lock that blocks inserts into ingestion_runs, so the writer
        // gets stuck on its first batch with four more still in the buffer.
        await using var blocker = await db.DataSource.OpenConnectionAsync(ct);
        await using var lockTransaction = await blocker.BeginTransactionAsync(ct);
        await using (var lockTable = new NpgsqlCommand("LOCK TABLE telemetry.ingestion_runs IN SHARE MODE", blocker, lockTransaction))
        {
            await lockTable.ExecuteNonQueryAsync(ct);
        }

        var service = new WriterService(new BatchWriter(db.DataSource), buffer, NullLogger<WriterService>.Instance);
        await service.StartAsync(ct);
        await WaitUntilAsync(() => buffer.Reader.Count == 4, ct);

        // Request shutdown (like Ctrl+C or `docker stop`) while batches are pending.
        var stopping = service.StopAsync(ct);
        await Task.Delay(300, ct);
        Assert.False(stopping.IsCompleted, "The writer must wait for the pending batches, not exit");

        await lockTransaction.RollbackAsync(ct);
        await stopping;

        Assert.Equal(5, await db.ScalarAsync<long>("SELECT count(*) FROM telemetry.ingestion_runs"));
    }

    [Fact]
    public async Task Does_not_hang_shutdown_when_the_database_is_down()
    {
        var ct = TestContext.Current.CancellationToken;
        // Nothing listens on port 1, so every connection attempt fails at once.
        await using var unreachable = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Username=x;Password=x;Timeout=2");
        var buffer = Channel.CreateUnbounded<PollBatch>();
        buffer.Writer.TryWrite(TestData.Batch(TestData.Vector("aaaaaa")));
        buffer.Writer.Complete();

        var service = new WriterService(new BatchWriter(unreachable), buffer, NullLogger<WriterService>.Instance);
        await service.StartAsync(ct);
        // The writer has taken the batch and is now in its retry loop.
        await WaitUntilAsync(() => buffer.Reader.Count == 0, ct);

        var stopwatch = Stopwatch.StartNew();
        await service.StopAsync(ct);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Shutdown took {stopwatch.Elapsed}");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "Timed out waiting for the writer to start");
            await Task.Delay(20, ct);
        }
    }
}
