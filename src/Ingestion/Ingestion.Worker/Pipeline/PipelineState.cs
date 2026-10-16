namespace Ingestion.Worker.Pipeline;

/// <summary>
/// Small shared counters the readiness check and logs read. A singleton
/// written by the poller and the buffer, read by the health check.
/// </summary>
public sealed class PipelineState
{
    private long _lastSuccessfulPollTicks;
    private long _droppedBatches;

    public DateTimeOffset? LastSuccessfulPollAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessfulPollTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public long DroppedBatches => Interlocked.Read(ref _droppedBatches);

    public void RecordSuccessfulPoll(DateTimeOffset at) =>
        Interlocked.Exchange(ref _lastSuccessfulPollTicks, at.UtcTicks);

    public long RecordDroppedBatch() => Interlocked.Increment(ref _droppedBatches);
}
