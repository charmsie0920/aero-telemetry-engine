using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;
using Ingestion.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Ingestion.Tests.Database;

/// <summary>
/// Starts a throwaway Postgres in Docker for the database tests, built from
/// the same db/postgres/Dockerfile as local development (Postgres 17 +
/// pg_partman + pg_cron) and migrated with the same MigrationRunner. Shared
/// by every test in <see cref="PostgresCollection"/>, then deleted.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private const string Database = "aero";

    private IFutureDockerImage? _image;
    private PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), "db/postgres")
            .WithName("aero-postgres-tests:17")
            // Keep the image between runs so Docker's layer cache makes the
            // next build near-instant instead of re-running apt-get.
            .WithCleanUp(false)
            .Build();
        await _image.CreateAsync();

        _container = new PostgreSqlBuilder(_image)
            .WithDatabase(Database)
            // Same server settings as docker-compose.yml; pg_cron refuses to
            // install without them.
            .WithCommand("-c", "shared_preload_libraries=pg_cron", "-c", $"cron.database_name={Database}")
            .Build();
        await _container.StartAsync();

        ConnectionString = _container.GetConnectionString();

        var result = await MigrationRunner.RunAsync(ConnectionString, logToConsole: false);
        if (!result.Successful)
        {
            throw new InvalidOperationException(result.Error);
        }

        DataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Empties the data tables so each test starts clean.</summary>
    public async Task ResetAsync()
    {
        await using var command = DataSource.CreateCommand(
            "TRUNCATE telemetry.state_vectors, telemetry.ingestion_runs");
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = DataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
