using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace Ingestion.Worker.OpenSky;

/// <summary>
/// Timings for the resilience pipeline. Production uses <see cref="Default"/>;
/// tests register shorter ones so a "4 attempts that all time out" test takes
/// milliseconds instead of a minute.
/// </summary>
public sealed record OpenSkyResilienceSettings(
    TimeSpan AttemptTimeout,
    TimeSpan RetryBaseDelay,
    TimeSpan BreakDuration)
{
    public static OpenSkyResilienceSettings Default { get; } = new(
        AttemptTimeout: TimeSpan.FromSeconds(10),
        RetryBaseDelay: TimeSpan.FromSeconds(2),
        BreakDuration: TimeSpan.FromSeconds(60));
}

/// <summary>
/// The resilience pipeline wrapped around every OpenSky API call (PRD 8.1 item 2).
/// </summary>
/// <remarks>
/// Strategies run outermost first:
///   retry -> circuit breaker -> per-attempt timeout -> the HTTP call
/// so one poll makes at most 4 attempts, each capped at 10 s, and the breaker
/// counts every attempt.
///
/// 429 is deliberately NOT retried here. Retrying a rate-limit response
/// spends more credits; the poller reads X-Rate-Limit-Retry-After-Seconds and
/// waits instead (item 3).
/// </remarks>
public static class OpenSkyResilience
{
    public const string PipelineName = "opensky";
    public const int MaxRetryAttempts = 3;

    /// <summary>
    /// Request option the retry callback increments, so the poller can store
    /// the retry count in telemetry.ingestion_runs.
    /// </summary>
    public static readonly HttpRequestOptionsKey<RetryCounter> RetryCounterKey = new("Aero.RetryCounter");

    public static void Configure(
        ResiliencePipelineBuilder<HttpResponseMessage> pipeline,
        OpenSkyResilienceSettings settings)
    {
        pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = MaxRetryAttempts,
            BackoffType = DelayBackoffType.Exponential,
            Delay = settings.RetryBaseDelay,
            // Jitter spreads retries out so many clients don't retry in lockstep.
            UseJitter = true,
            ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
            OnRetry = args =>
            {
                var request = args.Context.GetRequestMessage();
                if (request is not null && request.Options.TryGetValue(RetryCounterKey, out var counter))
                {
                    counter.Count++;
                }

                return ValueTask.CompletedTask;
            },
        });

        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            // Open when at least half of the last 4+ attempts in 2 minutes
            // failed, i.e. one poll that used up all its retries. While open,
            // calls fail immediately instead of hammering a struggling API.
            FailureRatio = 0.5,
            MinimumThroughput = MaxRetryAttempts + 1,
            SamplingDuration = TimeSpan.FromMinutes(2),
            // After the break one trial call is let through ("half-open").
            BreakDuration = settings.BreakDuration,
            ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
        });

        pipeline.AddTimeout(settings.AttemptTimeout);
    }

    private static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome) => outcome switch
    {
        { Exception: HttpRequestException or TimeoutRejectedException } => true,
        { Result.StatusCode: HttpStatusCode.RequestTimeout } => true,
        { Result: { } response } => (int)response.StatusCode >= 500,
        _ => false,
    };

    public sealed class RetryCounter
    {
        public int Count { get; set; }
    }
}
