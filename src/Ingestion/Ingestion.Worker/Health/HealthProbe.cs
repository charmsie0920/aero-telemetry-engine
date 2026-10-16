namespace Ingestion.Worker.Health;

/// <summary>
/// `dotnet Ingestion.Worker.dll healthcheck`: asks the already-running worker
/// in the same container whether it is ready, and exits 0 (healthy) or 1.
/// </summary>
/// <remarks>
/// Docker's HEALTHCHECK runs a command inside the container. The .NET runtime
/// image has no curl or wget, and installing one only for this adds packages
/// to patch and scan, so the worker binary does the HTTP call itself.
/// </remarks>
public static class HealthProbe
{
    public const string Argument = "healthcheck";

    public static async Task<int> RunAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await client.GetAsync("http://localhost:8080/health/ready");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
