using VerseDataGateway.Core;

namespace VerseDataGateway.Tests;

public sealed class QueueTests
{
    [Fact]
    public void EnforcesCountLimitWithoutEvictingInFlightBatch()
    {
        var queue = new BatchQueue();
        var first = new PendingBatch(Guid.NewGuid(), new byte[1]);
        queue.Enqueue(first);
        queue.Reserve();
        for (var i = 0; i < BatchQueue.MaxCount; i++)
            queue.Enqueue(new PendingBatch(Guid.NewGuid(), new byte[1]));
        Assert.Equal(60, queue.Count);
        Assert.Equal(1, queue.Dropped);
        Assert.Same(first, queue.Reserve());
        Assert.Throws<InvalidDataException>(() => queue.Acknowledge(Guid.NewGuid()));
        queue.Acknowledge(first.Id);
        Assert.Equal(59, queue.Count);
    }

    [Fact]
    public void EnforcesByteAndPerBatchLimitsAtBoundary()
    {
        var queue = new BatchQueue();
        for (var i = 0; i < 10; i++)
            Assert.True(queue.Enqueue(new PendingBatch(Guid.NewGuid(), new byte[1024 * 1024])));
        Assert.Equal(10 * 1024 * 1024, queue.Bytes);
        Assert.True(queue.Enqueue(new PendingBatch(Guid.NewGuid(), new byte[1])));
        Assert.Equal(1, queue.Dropped);
        Assert.Equal(9 * 1024 * 1024 + 1, queue.Bytes);
        Assert.False(queue.Enqueue(new PendingBatch(Guid.NewGuid(), new byte[1024 * 1024 + 1])));
        Assert.Equal(2, queue.Dropped);
        queue.Clear();
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.Bytes);
    }
}
