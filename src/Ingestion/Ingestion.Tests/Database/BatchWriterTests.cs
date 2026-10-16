using Ingestion.Worker.Pipeline;
using Xunit;

namespace Ingestion.Tests.Database;

/// <summary>
/// The insert path against a real Postgres (PRD 7.1): COPY -> staging ->
/// INSERT ON CONFLICT, plus the ingestion_runs row, in one transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BatchWriterTests(PostgresFixture db) : IAsyncLifetime
{
    private readonly BatchWriter _writer = new(db.DataSource);

    public async ValueTask InitializeAsync() => await db.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Writes_rows_and_one_run_record()
    {
        var batch = TestData.Batch(TestData.Vector("aaaaa1"), TestData.Vector("aaaaa2"), TestData.Vector("aaaaa3"));

        var inserted = await _writer.WriteAsync(batch);

        Assert.Equal(3, inserted);
        Assert.Equal(3, await db.ScalarAsync<long>("SELECT count(*) FROM telemetry.state_vectors"));
        Assert.Equal(3, await db.ScalarAsync<int>(
            $"SELECT records_inserted FROM telemetry.ingestion_runs WHERE run_id = '{batch.RunId}'"));
        Assert.Equal(3999, await db.ScalarAsync<int>(
            $"SELECT credits_remaining FROM telemetry.ingestion_runs WHERE run_id = '{batch.RunId}'"));
    }

    [Fact]
    public async Task Replaying_the_same_batch_changes_nothing()
    {
        // What happens if the worker crashes after COMMIT but before it
        // forgets the batch, and writes it again on restart.
        var batch = TestData.Batch(TestData.Vector("bbbbb1"), TestData.Vector("bbbbb2"));

        await _writer.WriteAsync(batch);
        var second = await _writer.WriteAsync(batch);

        Assert.Equal(0, second);
        Assert.Equal(2, await db.ScalarAsync<long>("SELECT count(*) FROM telemetry.state_vectors"));
        Assert.Equal(1, await db.ScalarAsync<long>("SELECT count(*) FROM telemetry.ingestion_runs"));
    }

    [Fact]
    public async Task Stale_positions_from_the_previous_poll_are_counted_as_duplicates()
    {
        // An aircraft whose position did not update is returned again with the
        // same time_position, so the same (icao24, observed_at) key.
        await _writer.WriteAsync(TestData.Batch(TestData.Vector("ccccc1"), TestData.Vector("ccccc2")));

        var next = TestData.Batch(
            TestData.Vector("ccccc1"),
            TestData.Vector("ccccc2"),
            TestData.Vector("ccccc1", TestData.Now.AddSeconds(30)));
        var inserted = await _writer.WriteAsync(next);

        Assert.Equal(1, inserted);
        Assert.Equal(2, await db.ScalarAsync<int>(
            $"SELECT records_duplicate FROM telemetry.ingestion_runs WHERE run_id = '{next.RunId}'"));
    }

    [Fact]
    public async Task Duplicates_inside_one_batch_are_inserted_once()
    {
        var inserted = await _writer.WriteAsync(TestData.Batch(TestData.Vector("ddddd1"), TestData.Vector("ddddd1")));

        Assert.Equal(1, inserted);
    }

    [Fact]
    public async Task Failed_poll_is_recorded_with_its_error()
    {
        var batch = TestData.FailedBatch("TimeoutRejectedException: timed out");

        var inserted = await _writer.WriteAsync(batch);

        Assert.Equal(0, inserted);
        Assert.Equal("TimeoutRejectedException: timed out", await db.ScalarAsync<string>(
            $"SELECT error FROM telemetry.ingestion_runs WHERE run_id = '{batch.RunId}'"));
        Assert.True(await db.ScalarAsync<bool>(
            $"SELECT http_status IS NULL FROM telemetry.ingestion_runs WHERE run_id = '{batch.RunId}'"));
    }

    [Fact]
    public async Task Current_rows_land_in_a_daily_partition_not_the_default()
    {
        await _writer.WriteAsync(TestData.Batch(TestData.Vector("eeeee1")));

        var partition = await db.ScalarAsync<string>("SELECT tableoid::regclass::text FROM telemetry.state_vectors");

        Assert.StartsWith("telemetry.state_vectors_p", partition);
    }
}
