namespace Ingestion.Worker;

/// <summary>
/// Named <see cref="HttpClient"/> registrations. Using the factory rather than
/// a static HttpClient gives per-client timeouts and correct DNS refresh, and
/// is where the P1.4 resilience pipeline (retry, circuit breaker, 429 handling)
/// will attach.
/// </summary>
public static class OpenSkyClientNames
{
    public const string Api = "opensky-api";
    public const string Auth = "opensky-auth";
}
