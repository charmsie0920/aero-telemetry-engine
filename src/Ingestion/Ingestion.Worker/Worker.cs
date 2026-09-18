using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ingestion.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace Ingestion.Worker;

/// <summary>
/// P0.4 scaffolding: fetches one bounding box once and logs the aircraft count.
/// The polling loop, resilience pipeline and database writer arrive in P1.3-P1.7.
/// </summary>
public class Worker(
    ILogger<Worker> logger,
    IHttpClientFactory httpClientFactory,
    IOptions<OpenSkyOptions> openSkyOptions) : BackgroundService
{
    private const string TokenPath = "auth/realms/opensky-network/protocol/openid-connect/token";

    private readonly OpenSkyOptions _openSky = openSkyOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var accessToken = await RequestAccessTokenAsync(stoppingToken);
        var count = await FetchAircraftCountAsync(accessToken, stoppingToken);

        logger.LogInformation(
            "Fetched {Count} aircraft in bounding box ({Area:F1} sq deg)",
            count,
            _openSky.BoundingBoxAreaSquareDegrees);
    }

    private async Task<string> RequestAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(OpenSkyClientNames.Auth);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _openSky.ClientId,
            ["client_secret"] = _openSky.ClientSecret,
        });

        using var response = await client.PostAsync(TokenPath, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return payload.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("OpenSky token response contained no access_token.");
    }

    private async Task<int> FetchAircraftCountAsync(string accessToken, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(OpenSkyClientNames.Api);

        // Invariant culture: a comma decimal separator would silently produce a
        // malformed bounding box on a non-English machine.
        var query = string.Format(
            CultureInfo.InvariantCulture,
            "states/all?lamin={0}&lomin={1}&lamax={2}&lomax={3}",
            _openSky.BboxLamin,
            _openSky.BboxLomin,
            _openSky.BboxLamax,
            _openSky.BboxLomax);

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        LogRateLimitHeaders(response);

        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        return payload.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Array
            ? states.GetArrayLength()
            : 0;
    }

    private void LogRateLimitHeaders(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Rate-Limit-Remaining", out var remaining))
        {
            logger.LogInformation(
                "OpenSky credits remaining today: {CreditsRemaining}",
                string.Join(',', remaining));
        }
    }
}
