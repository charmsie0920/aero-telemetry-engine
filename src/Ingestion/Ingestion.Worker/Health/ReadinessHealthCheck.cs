using Ingestion.Worker.Pipeline;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ingestion.Worker.Health;

/// <summary>
/// Backs /health/ready (PRD 8.1 item 7): ready means the database answers and
/// OpenSky gave a good response in the last 2 minutes. /health/live only
/// says the process is running; Docker restarts the container when it fails,
/// while a deploy waits for "ready" before calling a release good.
/// </summary>
public sealed class ReadinessHealthCheck(
    NpgsqlDataSource dataSource,
    PipelineState state,
    TimeProvider timeProvider) : IHealthCheck
{
    private static readonly TimeSpan MaxPollAge = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(3);

    // Grace period after startup, before the first poll has had a chance.
    private readonly DateTimeOffset _startedAt = timeProvider.GetUtcNow();

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["lastSuccessfulPollAt"] = state.LastSuccessfulPollAt?.ToString("O") ?? "never",
            ["droppedBatches"] = state.DroppedBatches,
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DatabaseTimeout);

            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException)
        {
            // No exception object: the health check logs it, and a full stack
            // trace every few seconds during an outage would bury the real logs.
            var reason = ex is OperationCanceledException ? "timed out" : ex.Message;
            return HealthCheckResult.Unhealthy($"Database unreachable ({reason})", data: data);
        }

        var lastPoll = state.LastSuccessfulPollAt ?? _startedAt;
        if (timeProvider.GetUtcNow() - lastPoll > MaxPollAge)
        {
            return HealthCheckResult.Unhealthy($"No successful OpenSky poll in the last {MaxPollAge.TotalMinutes} minutes", data: data);
        }

        return HealthCheckResult.Healthy(data: data);
    }
}
