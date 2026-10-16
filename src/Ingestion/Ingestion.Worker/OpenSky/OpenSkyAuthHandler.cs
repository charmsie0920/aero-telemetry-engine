using System.Net;
using System.Net.Http.Headers;

namespace Ingestion.Worker.OpenSky;

/// <summary>
/// Adds the bearer token to every OpenSky API request and, on HTTP 401,
/// fetches a fresh token and tries once more (PRD 8.1 item 1).
/// </summary>
/// <remarks>
/// A DelegatingHandler sits inside HttpClient's request pipeline, so the
/// calling code never deals with tokens. It runs inside the resilience
/// handler (see Program.cs), so each retry attempt gets a current token too.
/// </remarks>
public sealed class OpenSkyAuthHandler(OpenSkyTokenProvider tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // Once only: if a brand-new token is also rejected, the credentials
        // are wrong and retrying would just burn calls.
        response.Dispose();
        tokenProvider.Invalidate(token);

        token = await tokenProvider.GetTokenAsync(cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
