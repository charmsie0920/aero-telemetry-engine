using Ingestion.Migrations;
using Xunit;

namespace Ingestion.Tests.Database;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture db)
{
    [Fact]
    public async Task Running_migrations_again_changes_nothing()
    {
        var result = await MigrationRunner.RunAsync(db.ConnectionString, logToConsole: false);

        Assert.True(result.Successful, result.Error);
        Assert.Equal(0, result.ScriptsApplied);
    }

    [Fact]
    public async Task All_tables_live_in_the_telemetry_schema()
    {
        // ADR 0003: nothing in `public`, which Supabase exposes over REST.
        var publicTables = await db.ScalarAsync<long>(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'");

        Assert.Equal(0, publicTables);
    }

    [Fact]
    public async Task Partition_maintenance_is_scheduled()
    {
        var jobs = await db.ScalarAsync<long>(
            "SELECT count(*) FROM cron.job WHERE jobname = 'partman-maintenance'");

        Assert.Equal(1, jobs);
    }
}
