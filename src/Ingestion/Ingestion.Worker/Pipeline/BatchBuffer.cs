using System.Threading.Channels;

namespace Ingestion.Worker.Pipeline;

/// <summary>
/// The bounded in-memory queue between the poller and the writer (PRD 8.1 item 4).
/// </summary>
/// <remarks>
/// If the database is down, batches wait here instead of being lost. At 30 s
/// polling, 100 batches is about 50 minutes of data. When it is full the
/// OLDEST batch is dropped and counted: the newest data is the most useful,
/// and memory must never grow without limit.
/// </remarks>
public static class BatchBuffer
{
    public const int Capacity = 100;

    public static Channel<PollBatch> Create(PipelineState state, ILogger logger) =>
        Channel.CreateBounded<PollBatch>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            },
            dropped =>
            {
                var total = state.RecordDroppedBatch();
                logger.LogError(
                    "Buffer full: dropped batch {RunId} from {StartedAt} ({Records} records). {TotalDropped} dropped so far",
                    dropped.RunId,
                    dropped.StartedAt,
                    dropped.Records.Count,
                    total);
            });
}
