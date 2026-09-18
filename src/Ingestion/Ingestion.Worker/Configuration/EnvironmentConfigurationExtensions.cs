namespace Ingestion.Worker.Configuration;

/// <summary>
/// Maps the documented flat environment variable names (see .env.example) onto
/// the configuration keys that <see cref="OpenSkyOptions"/> and
/// <see cref="DatabaseOptions"/> bind to.
/// </summary>
/// <remarks>
/// The default environment-variable provider would require names like
/// OPENSKY__CLIENTID. Keeping the single-underscore names means .env stays
/// readable and matches what goes into SSM Parameter Store later, so the
/// mapping is done explicitly here instead.
/// </remarks>
public static class EnvironmentConfigurationExtensions
{
    public static IConfigurationBuilder AddAeroEnvironmentVariables(
        this IConfigurationBuilder configuration,
        bool isDevelopment)
    {
        // Locally the worker talks to the docker-compose Postgres; everywhere
        // else it talks to Supabase. Keeping these in separate variables means
        // a production connection string can never be picked up by accident.
        var connectionStringVariable = isDevelopment
            ? "LOCAL_POSTGRES_CONNECTION_STRING"
            : "POSTGRES_CONNECTION_STRING";

        var map = new Dictionary<string, string>
        {
            ["OPENSKY_CLIENT_ID"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.ClientId)}",
            ["OPENSKY_CLIENT_SECRET"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.ClientSecret)}",
            ["OPENSKY_BBOX_LAMIN"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.BboxLamin)}",
            ["OPENSKY_BBOX_LOMIN"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.BboxLomin)}",
            ["OPENSKY_BBOX_LAMAX"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.BboxLamax)}",
            ["OPENSKY_BBOX_LOMAX"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.BboxLomax)}",
            ["OPENSKY_POLL_INTERVAL_SECONDS"] = $"{OpenSkyOptions.SectionName}:{nameof(OpenSkyOptions.PollIntervalSeconds)}",
            [connectionStringVariable] = $"{DatabaseOptions.SectionName}:{nameof(DatabaseOptions.ConnectionString)}",
        };

        var values = new Dictionary<string, string?>();

        foreach (var (variable, configurationKey) in map)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                values[configurationKey] = value;
            }
        }

        return configuration.AddInMemoryCollection(values);
    }
}
