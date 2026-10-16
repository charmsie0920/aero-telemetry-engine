using Ingestion.Tests.Database;
using Ingestion.Worker.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ingestion.Tests.Pipeline;

/// <summary>The bounded buffer's overflow policy (PRD 8.1 item 4).</summary>
public sealed class BatchBufferTests
{
    [Fact]
    public async Task When_full_the_oldest_batch_is_dropped_and_counted()
    {
        var state = new PipelineState();
        var buffer = BatchBuffer.Create(state, NullLogger.Instance);
        var batches = Enumerable.Range(0, BatchBuffer.Capacity + 1).Select(_ => TestData.Batch()).ToList();

        foreach (var batch in batches)
        {
            Assert.True(buffer.Writer.TryWrite(batch));
        }

        Assert.Equal(1, state.DroppedBatches);
        Assert.Equal(BatchBuffer.Capacity, buffer.Reader.Count);
        Assert.Equal(batches[1].RunId, (await buffer.Reader.ReadAsync(TestContext.Current.CancellationToken)).RunId);
    }
}
