using Ingestion.Migrations;
using Ingestion.Worker.Configuration;
using Npgsql;

// One-shot schema migrator: picks the target database, then hands over to
// MigrationRunner. Exit code 0 = schema is up to date, 1 = something failed.

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

var result = await MigrationRunner.RunAsync(connectionString);

if (!result.Successful)
{
    Console.Error.WriteLine(result.Error);
    return 1;
}

Console.WriteLine(result.ScriptsApplied > 0
    ? $"Applied {result.ScriptsApplied} script(s)."
    : "Schema already up to date.");
return 0;
