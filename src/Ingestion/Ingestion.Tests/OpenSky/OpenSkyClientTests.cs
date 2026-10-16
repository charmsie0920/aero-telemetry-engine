using Ingestion.Tests.Parsing;
using Ingestion.Worker.Configuration;
using Ingestion.Worker.OpenSky;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace Ingestion.Tests.OpenSky;

/// <summary>
/// Failure handling of the OpenSky client (PRD 8.1 items 1-3), against a fake
/// OpenSky served by WireMock. The client is built with the same
/// <see cref="OpenSkyServiceCollectionExtensions.AddOpenSky"/> call as
/// production, so the real token handler and resilience pipeline are under
/// test; only the timings are shortened.
/// </summary>
public sealed class OpenSkyClientTests : IDisposable
{
    private const string TokenPath = "/auth/realms/opensky-network/protocol/openid-connect/token";
    private const string StatesPath = "/api/states/all";

    // Fast retries, but the production 10 s attempt timeout: under a busy
    // parallel test run the first request (JIT, connection setup) can take
    // over a second, and a tight timeout would turn that into false retries.
    private static readonly OpenSkyResilienceSettings FastRetries = OpenSkyResilienceSettings.Default with
    {
        RetryBaseDelay = TimeSpan.FromMilliseconds(10),
        BreakDuration = TimeSpan.FromSeconds(5),
    };

    private readonly WireMockServer _server = WireMockServer.Start();
    private ServiceProvider? _services;

    private OpenSkyClient Client => CreateClient(FastRetries);

    /// <summary>
    /// Builds the client the way production does. The settings only take
    /// effect on the first call in a test; later calls reuse the same client.
    /// </summary>
    private OpenSkyClient CreateClient(OpenSkyResilienceSettings settings)
    {
        if (_services is null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(settings);
            services.AddSingleton(Options.Create(new OpenSkyOptions
            {
                ClientId = "test-client",
                ClientSecret = "test-secret",
                BboxLamin = 48,
                BboxLomin = 2,
                BboxLamax = 53,
                BboxLomax = 7,
                ApiBaseUrl = new Uri($"{_server.Url}/api/"),
                AuthBaseUrl = new Uri($"{_server.Url}/"),
            }));
            services.AddOpenSky();

            // A fresh container per test, so circuit-breaker state never leaks between tests.
            _services = services.BuildServiceProvider();
        }

        return _services.GetRequiredService<OpenSkyClient>();
    }

    public void Dispose()
    {
        _services?.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    [Fact]
    public async Task Successful_poll_returns_parsed_records_and_credits()
    {
        GivenToken("token-1");
        GivenStates(Response.Create()
            .WithStatusCode(200)
            .WithHeader("X-Rate-Limit-Remaining", "3999")
            .WithBody(OpenSkyStateParserTests.ResponseJson(OpenSkyStateParserTests.State())));

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(3999, result.CreditsRemaining);
        Assert.Equal(0, result.Retries);
        Assert.Single(result.Parsed!.Valid);

        var request = Assert.Single(_server.LogEntries, e => e.RequestMessage?.Path == StatesPath).RequestMessage!;
        Assert.Equal("Bearer token-1", request.Headers!["Authorization"].Single());
        Assert.Contains("lamin=48", request.Url);
    }

    [Fact]
    public async Task Token_is_cached_across_polls()
    {
        GivenToken("token-1");
        GivenStates(Ok());

        await Client.FetchStatesAsync(TestContext.Current.CancellationToken);
        await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Calls(TokenPath));
        Assert.Equal(2, Calls(StatesPath));
    }

    [Fact]
    public async Task Server_error_is_retried_until_it_succeeds()
    {
        GivenToken("token-1");
        _server.Given(Request.Create().WithPath(StatesPath).UsingGet())
            .InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        _server.Given(Request.Create().WithPath(StatesPath).UsingGet())
            .InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Ok());

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Retries);
        Assert.Equal(2, Calls(StatesPath));
    }

    [Fact]
    public async Task Persistent_server_error_gives_up_after_four_attempts()
    {
        GivenToken("token-1");
        GivenStates(Response.Create().WithStatusCode(500));

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(500, result.HttpStatus);
        Assert.Equal(OpenSkyResilience.MaxRetryAttempts, result.Retries);
        Assert.Equal(OpenSkyResilience.MaxRetryAttempts + 1, Calls(StatesPath));
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Timeouts_are_retried_then_reported()
    {
        GivenToken("token-1");
        GivenStates(Ok().WithDelay(TimeSpan.FromSeconds(3)));
        var client = CreateClient(FastRetries with { AttemptTimeout = TimeSpan.FromMilliseconds(500) });

        var result = await client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.HttpStatus);
        Assert.Equal(OpenSkyResilience.MaxRetryAttempts, result.Retries);
        Assert.Contains("Timeout", result.Error);
    }

    [Fact]
    public async Task Open_circuit_fails_fast_without_calling_OpenSky()
    {
        GivenToken("token-1");
        GivenStates(Response.Create().WithStatusCode(500));

        await Client.FetchStatesAsync(TestContext.Current.CancellationToken);
        var callsAfterFirstPoll = Calls(StatesPath);

        var second = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("BrokenCircuit", second.Error);
        Assert.Equal(callsAfterFirstPoll, Calls(StatesPath));
    }

    [Fact]
    public async Task Rate_limit_is_not_retried_and_reports_how_long_to_wait()
    {
        GivenToken("token-1");
        GivenStates(Response.Create()
            .WithStatusCode(429)
            .WithHeader("X-Rate-Limit-Retry-After-Seconds", "120"));

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(429, result.HttpStatus);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Equal(0, result.Retries);
        Assert.Equal(1, Calls(StatesPath));
    }

    [Fact]
    public async Task Rate_limit_without_header_waits_a_minute()
    {
        GivenToken("token-1");
        GivenStates(Response.Create().WithStatusCode(429));

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(60), result.RetryAfter);
    }

    [Fact]
    public async Task Rejected_token_is_refreshed_once_and_the_call_succeeds()
    {
        // The token endpoint hands out token-1 first, then token-2.
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .InScenario("tokens").WillSetStateTo("second")
            .RespondWith(TokenResponse("token-1"));
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .InScenario("tokens").WhenStateIs("second")
            .RespondWith(TokenResponse("token-2"));

        // OpenSky rejects token-1 (e.g. revoked) and accepts token-2.
        _server.Given(Request.Create().WithPath(StatesPath).UsingGet().WithHeader("Authorization", "Bearer token-1"))
            .RespondWith(Response.Create().WithStatusCode(401));
        _server.Given(Request.Create().WithPath(StatesPath).UsingGet().WithHeader("Authorization", "Bearer token-2"))
            .RespondWith(Ok());

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(2, Calls(TokenPath));
        Assert.Equal(2, Calls(StatesPath));
    }

    [Fact]
    public async Task Persistent_401_is_not_retried_more_than_once()
    {
        GivenToken("token-1");
        GivenStates(Response.Create().WithStatusCode(401));

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(401, result.HttpStatus);
        Assert.Equal(2, Calls(StatesPath));
    }

    [Fact]
    public async Task Token_endpoint_failure_is_reported_not_thrown()
    {
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));
        GivenStates(Ok());

        var result = await Client.FetchStatesAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains("HttpRequestException", result.Error);
        Assert.Equal(0, Calls(StatesPath));
    }

    private void GivenToken(string token) =>
        _server.Given(Request.Create().WithPath(TokenPath).UsingPost()).RespondWith(TokenResponse(token));

    private void GivenStates(IResponseBuilder response) =>
        _server.Given(Request.Create().WithPath(StatesPath).UsingGet()).RespondWith(response);

    private static IResponseBuilder TokenResponse(string token) =>
        Response.Create().WithStatusCode(200).WithBody($"{{\"access_token\":\"{token}\",\"expires_in\":1800}}");

    private static IResponseBuilder Ok() =>
        Response.Create().WithStatusCode(200).WithBody(OpenSkyStateParserTests.ResponseJson(OpenSkyStateParserTests.State()));

    private int Calls(string path) => _server.LogEntries.Count(e => e.RequestMessage?.Path == path);
}
