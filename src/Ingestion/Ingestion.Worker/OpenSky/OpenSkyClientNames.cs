namespace Ingestion.Worker.OpenSky;

/// <summary>
/// Named <see cref="HttpClient"/> registrations. Using the factory rather than
/// a static HttpClient gives per-client handlers and correct DNS refresh. The
/// API client carries the resilience pipeline and the auth handler; the auth
/// client is plain, so a token request is never wrapped in its own token logic.
/// </summary>
public static class OpenSkyClientNames
{
    public const string Api = "opensky-api";
    public const string Auth = "opensky-auth";
}
