namespace Ingestion.Worker;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    private static readonly HttpClient Http = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clientId = Environment.GetEnvironmentVariable("OPENSKY_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("OPENSKY_CLIENT_SECRET");

        //hardcode token req for now
        var tokenResponse = await Http.PostAsync(
            "https://auth.opensky-network.org/auth/realms/opensky-network/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId!,
                ["client_secret"] = clientSecret!
            }),
            stoppingToken);
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stoppingToken);
        var accessToken = tokenJson.GetProperty("access_token").GetString();

        // 2. Fetch one bounding box
        var lamin = Environment.GetEnvironmentVariable("OPENSKY_BBOX_LAMIN");
        var lomin = Environment.GetEnvironmentVariable("OPENSKY_BBOX_LOMIN");
        var lamax = Environment.GetEnvironmentVariable("OPENSKY_BBOX_LAMAX");
        var lomax = Environment.GetEnvironmentVariable("OPENSKY_BBOX_LOMAX");

        var url = $"https://opensky-network.org/api/states/all?lamin={lamin}&lomin={lomin}&lamax={lamax}&lomax={lomax}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var statesResponse = await Http.SendAsync(request, stoppingToken);
        statesResponse.EnsureSuccessStatusCode();
        var statesJson = await statesResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: stoppingToken);

        var count = statesJson.TryGetProperty("states", out var states) && states.ValueKind == JsonValueKind.Array
            ? states.GetArrayLength()
            : 0;

        logger.LogInformation("Fetched {Count} aircraft in bounding box", count);
        
    }
}
