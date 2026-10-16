using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Ingestion.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace Ingestion.Worker.OpenSky;

/// <summary>
/// Gets OAuth2 access tokens from OpenSky and caches them (PRD 8.1 item 1).
/// </summary>
/// <remarks>
/// Tokens last about 30 minutes. Asking for a new one on every poll would
/// double the HTTP calls, so the token is reused until 2 minutes before it
/// expires. Registered as a singleton so the cache is shared.
/// </remarks>
public sealed class OpenSkyTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenSkyOptions> options,
    TimeProvider timeProvider,
    ILogger<OpenSkyTokenProvider> logger)
{
    private const string TokenPath = "auth/realms/opensky-network/protocol/openid-connect/token";
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);

    // Only one caller refreshes at a time; the others wait and reuse its token.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _refreshAt;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
        {
            return _token;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_token is not null && timeProvider.GetUtcNow() < _refreshAt)
            {
                return _token;
            }

            var response = await RequestTokenAsync(cancellationToken);
            _token = response.AccessToken;
            _refreshAt = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(response.ExpiresIn) - RefreshMargin;

            logger.LogInformation("Obtained OpenSky access token, valid for {ExpiresInSeconds} s", response.ExpiresIn);
            return _token;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Forgets the cached token so the next call fetches a new one. Called
    /// when OpenSky answers 401, which means the token was revoked or expired early.
    /// </summary>
    public void Invalidate(string rejectedToken)
    {
        // Compare first: if a newer token already replaced the rejected one, keep it.
        if (_token == rejectedToken)
        {
            _token = null;
        }
    }

    private async Task<TokenResponse> RequestTokenAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(OpenSkyClientNames.Auth);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = options.Value.ClientId,
            ["client_secret"] = options.Value.ClientSecret,
        });

        using var response = await client.PostAsync(TokenPath, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("OpenSky token response was empty.");
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
