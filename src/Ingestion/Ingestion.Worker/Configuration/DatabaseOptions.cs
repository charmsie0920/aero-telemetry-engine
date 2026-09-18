using System.ComponentModel.DataAnnotations;

namespace Ingestion.Worker.Configuration;

/// <summary>
/// Bound from POSTGRES_CONNECTION_STRING (Supabase) or
/// LOCAL_POSTGRES_CONNECTION_STRING (docker-compose), see .env.example.
/// </summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;
}
