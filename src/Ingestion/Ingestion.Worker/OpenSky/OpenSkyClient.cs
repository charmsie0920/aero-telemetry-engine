using System.Globalization;
using System.Net;
using System.Text.Json;
using Ingestion.Core;
using Ingestion.Worker.Configuration;
using Microsoft.Extensions.Options;

namespace Ingestion.Worker.OpenSky;

/// <summary>Everything one call to /states/all produced, success or not.</summary>
public sealed record FetchResult(
    int? HttpStatus,
    ParseResult? Parsed,
    int? CreditsRemaining,
    int Retries,
    TimeSpan? RetryAfter,
    string? Error)
{
    public bool Succeeded => Parsed is not null;
}

/// <summary>
/// Fetches and parses aircraft states for the configured bounding box.
/// </summary>
/// <remarks>
/// Never throws for an OpenSky or network problem: failures come back as a
/// <see cref="FetchResult"/> with <see cref="FetchResult.Error"/> set, so the
/// poller can record them in telemetry.ingestion_runs and keep going.
/// Tokens, retries and timeouts are handled by the HttpClient's handlers
/// (see Program.cs), not here.
/// </remarks>
public sealed class OpenSkyClient(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenSkyOptions> options,
    ILogger<OpenSkyClient> logger)
{
    public async Task<FetchResult> FetchStatesAsync(CancellationToken cancellationToken)
    {
        var box = options.Value;
        // Invariant culture: a comma decimal separator would silently produce a
        // malformed bounding box on a non-English machine.
        var query = string.Format(
            CultureInfo.InvariantCulture,
            "states/all?lamin={0}&lomin={1}&lamax={2}&lomax={3}",
            box.BboxLamin,
            box.BboxLomin,
            box.BboxLamax,
            box.BboxLomax);

        var retries = new OpenSkyResilience.RetryCounter();
        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        request.Options.Set(OpenSkyResilience.RetryCounterKey, retries);

        try
        {
            var client = httpClientFactory.CreateClient(OpenSkyClientNames.Api);
            using var response = await client.SendAsync(request, cancellationToken);

            var status = (int)response.StatusCode;
            var credits = ReadIntHeader(response, "X-Rate-Limit-Remaining");

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = TimeSpan.FromSeconds(ReadIntHeader(response, "X-Rate-Limit-Retry-After-Seconds") ?? 60);
                logger.LogWarning("OpenSky rate limit hit; next poll in {RetryAfterSeconds} s", retryAfter.TotalSeconds);
                return new FetchResult(status, null, credits, retries.Count, retryAfter, "rate limited (429)");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new FetchResult(status, null, credits, retries.Count, null, $"HTTP {status} {response.ReasonPhrase}");
            }

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            var parsed = OpenSkyStateParser.Parse(document.RootElement);

            return new FetchResult(status, parsed, credits, retries.Count, null, null);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeouts, DNS/socket errors, an open circuit breaker, a token
            // endpoint failure or a body that is not JSON all end up here.
            logger.LogWarning(ex, "OpenSky poll failed after {Retries} retries", retries.Count);
            return new FetchResult(null, null, null, retries.Count, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int? ReadIntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
