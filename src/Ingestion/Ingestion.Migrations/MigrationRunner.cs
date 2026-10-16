using DbUp;
using Npgsql;

namespace Ingestion.Migrations;

/// <summary>Outcome of one migration run.</summary>
public sealed record MigrationResult(bool Successful, int ScriptsApplied, string? Error);

/// <summary>
/// Applies db/migrations/*.sql in name order and records each script in
/// telemetry.schemaversions so it never runs twice. Separate from Program.cs
/// so the integration tests build their database exactly the way production does.
/// </summary>
public static class MigrationRunner
{
    public const string Schema = "telemetry";

    public static async Task<MigrationResult> RunAsync(string connectionString, bool logToConsole = true)
    {
        try
        {
            // DbUp creates its journal table before running the first script, so the
            // schema that holds the journal has to exist already.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {Schema}", connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException ex)
        {
            return new MigrationResult(false, 0, $"Could not connect to the database: {ex.Message}");
        }

        var builder = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(MigrationRunner).Assembly,
                name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .JournalToPostgresqlTable(Schema, "schemaversions")
            .WithTransactionPerScript();

        var upgrader = (logToConsole ? builder.LogToConsole() : builder.LogToNowhere()).Build();
        var result = upgrader.PerformUpgrade();

        return result.Successful
            ? new MigrationResult(true, result.Scripts.Count(), null)
            : new MigrationResult(false, 0, $"Migration failed in {result.ErrorScript?.Name}: {result.Error.Message}");
    }
}
