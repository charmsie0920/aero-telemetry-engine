using DbUp;
using Ingestion.Worker.Configuration;
using Npgsql;

// One-shot schema migrator: applies db/migrations/*.sql in name order and
// records each script in telemetry.schemaversions so it never runs twice.
// Exit code 0 = schema is up to date, 1 = something failed.

const string Schema = "telemetry";

// Mirrors the worker: Development targets the docker-compose Postgres,
// everything else targets Supabase. They are separate variables so a
// production connection string can never be picked up by accident.
var isDevelopment = string.Equals(
    Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
    "Development",
    StringComparison.OrdinalIgnoreCase);

if (isDevelopment)
{
    var dotEnvPath = DotEnvLoader.Load();
    Console.WriteLine(dotEnvPath is null
        ? "No .env file found; relying on the ambient environment."
        : $"Loaded environment from {dotEnvPath}");
}

var connectionStringVariable = isDevelopment
    ? "LOCAL_POSTGRES_CONNECTION_STRING"
    : "POSTGRES_CONNECTION_STRING";

var connectionString = Environment.GetEnvironmentVariable(connectionStringVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine($"{connectionStringVariable} is not set. See .env.example.");
    return 1;
}

// Say where we are about to write before doing it, without the password.
var target = new NpgsqlConnectionStringBuilder(connectionString);
Console.WriteLine($"Migrating {target.Host}:{target.Port}/{target.Database} as {target.Username} ({connectionStringVariable})");

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
    Console.Error.WriteLine($"Could not connect to the database: {ex.Message}");
    return 1;
}

var upgrader = DeployChanges.To
    .PostgresqlDatabase(connectionString)
    .WithScriptsEmbeddedInAssembly(
        typeof(Program).Assembly,
        name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
    .JournalToPostgresqlTable(Schema, "schemaversions")
    .WithTransactionPerScript()
    .LogToConsole()
    .Build();

var result = upgrader.PerformUpgrade();

if (!result.Successful)
{
    Console.Error.WriteLine($"Migration failed in {result.ErrorScript?.Name}: {result.Error.Message}");
    return 1;
}

Console.WriteLine(result.Scripts.Any()
    ? $"Applied {result.Scripts.Count()} script(s)."
    : "Schema already up to date.");
return 0;
