namespace ScTelemetryGateway.Core;

public sealed record GatewayStatus(string Game, string Delivery, int Queued, int QueueBytes,
    long BatchesDropped, long EventsDropped, DateTimeOffset? LastAcknowledged);

public sealed class GatewayRuntime
{
    private readonly object gate = new();
    private string game = "Stopped", delivery = "Stopped";
    private DateTimeOffset? lastAck;
    private long eventsDropped;
    private readonly BatchQueue queue = new();

    public GatewayStatus Status
    {
        get { lock (gate) return new(game, delivery, queue.Count, queue.Bytes, queue.Dropped, eventsDropped, lastAck); }
    }

    public async Task RunAsync(GatewayOptions options, string key, CancellationToken cancellationToken)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ArgumentException("A valid API key is required.");
        var aggregator = new TelemetryAggregator(options.Fields, Guid.NewGuid());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void Flush()
        {
            var batch = aggregator.CloseWindow(queue.Dropped);
            if (batch is null) return;
            queue.Enqueue(batch);
            lock (gate) eventsDropped = batch.Loss.EventsDropped;
        }
        var reader = new GameTelemetryReader(options.GamePort);
        var sender = new WebSocketDelivery(new Uri(options.Endpoint), key, queue,
            TimeSpan.FromSeconds(options.IntervalSeconds),
            value => { lock (gate) delivery = value; },
            () => { lock (gate) lastAck = DateTimeOffset.UtcNow; });
        async Task Aggregate()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
            while (await timer.WaitForNextTickAsync(stop.Token)) Flush();
        }
        Task[] tasks =
        [
            reader.RunAsync(aggregator, value => { lock (gate) game = value; }, stop.Token, Flush),
            Aggregate(),
            sender.RunAsync(stop.Token)
        ];
        try
        {
            var completed = await Task.WhenAny(tasks);
            await completed;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            await stop.CancelAsync();
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                queue.Clear();
                lock (gate) { game = "Stopped"; delivery = "Stopped"; }
            }
        }
    }
}
