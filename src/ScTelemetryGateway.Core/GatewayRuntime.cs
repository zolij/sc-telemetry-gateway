namespace ScTelemetryGateway.Core;

public sealed record GatewayStatus(string Game, string Delivery, string Mfd, int Queued, int QueueBytes,
    long BatchesDropped, long EventsDropped, DateTimeOffset? LastAcknowledged);

public sealed class GatewayRuntime
{
    private readonly object gate = new();
    private string game = "Stopped", delivery = "Stopped", mfd = "Stopped";
    private DateTimeOffset? lastAck;
    private long eventsDropped;
    private readonly BatchQueue queue = new();
    private MfdServer? mfdServer;
    public MfdServer? MfdServer => mfdServer;

    public GatewayStatus Status
    {
        get { lock (gate) return new(game, delivery, mfd, queue.Count, queue.Bytes, queue.Dropped, eventsDropped, lastAck); }
    }

    private async Task ObserveRemote(Task senderTask, CancellationTokenSource remoteStop)
    {
        try { await senderTask; }
        catch (ReceiverRejectedException ex)
        {
            lock (gate) delivery = ex.Message;
            queue.Clear();
            await remoteStop.CancelAsync();
        }
        catch (OperationCanceledException) { }
    }

    public async Task RunAsync(GatewayOptions options, string key, CancellationToken cancellationToken,
        string? mfdAssetRoot = null)
    {
        options.Validate(requireDestination: true);
        var remoteEnabled = options.RemoteConfigured;
        if (remoteEnabled && (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)))
            throw new ArgumentException("A valid API key is required.");
        var remoteAggregator = remoteEnabled ? new TelemetryAggregator(options.Fields, Guid.NewGuid()) : null;
        var mfdAggregator = options.MfdEnabled ? new TelemetryAggregator(options.MfdFields, Guid.NewGuid()) : null;
        var mfdSnapshot = options.MfdEnabled ? new MfdSnapshotStore(options.MfdFields) : null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var remoteStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        void Flush()
        {
            if (remoteAggregator is null) return;
            var batch = remoteAggregator.CloseWindow(queue.Dropped);
            if (batch is null) return;
            queue.Enqueue(batch);
            lock (gate) eventsDropped = batch.Loss.EventsDropped;
        }
        var reader = new GameTelemetryReader(options.GamePort);
        var sender = remoteEnabled ? new WebSocketDelivery(new Uri(options.Endpoint), key, queue,
            TimeSpan.FromSeconds(options.IntervalSeconds),
            value => { lock (gate) delivery = value; },
            () => { lock (gate) lastAck = DateTimeOffset.UtcNow; }) : null;
        async Task Aggregate()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
            while (await timer.WaitForNextTickAsync(remoteStop.Token)) Flush();
        }
        var tasks = new List<Task>();
        var sinks = new List<Action<Sc.Game.Telemetry.V1.TelemetryStreamResponse>>();
        if (remoteAggregator is not null)
            sinks.Add(remoteAggregator.Ingest);
        if (mfdAggregator is not null)
            sinks.Add(mfdAggregator.Ingest);
        if (mfdSnapshot is not null)
            sinks.Add(mfdSnapshot.Ingest);
        var readerTask = reader.RunAsync(sinks, value => { lock (gate) game = value; }, stop.Token, () =>
        {
            remoteAggregator?.Disconnected();
            mfdAggregator?.Disconnected();
            mfdSnapshot?.Disconnected();
            Flush();
        }, mfdSnapshot is null ? null : mfdSnapshot.StartStream);
        tasks.Add(readerTask);
        if (remoteEnabled)
        {
            var senderTask = sender!.RunAsync(remoteStop.Token);
            if (options.MfdEnabled)
            {
                var aggregateTask = Aggregate();
                _ = ObserveRemote(senderTask, remoteStop);
            }
            else
            {
                tasks.Add(Aggregate());
                tasks.Add(senderTask);
            }
        }
        if (options.MfdEnabled)
        {
            try
            {
                var address = System.Net.IPAddress.Parse(options.MfdAddress);
                var assetRoot = mfdAssetRoot ?? Path.Combine(AppContext.BaseDirectory, "mfd");
                mfdServer = new MfdServer(address, options.MfdPort, mfdSnapshot!, assetRoot);
                await mfdServer.StartAsync(stop.Token);
                lock (gate) mfd = "Listener active";
            }
            catch (Exception ex)
            {
                lock (gate) mfd = $"Listener failed: {ex.GetType().Name}";
            }
        }
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
                mfdSnapshot?.SetPaused();
                if (mfdServer is not null)
                    mfdServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
                lock (gate) { game = "Stopped"; delivery = "Stopped"; mfd = "Stopped"; }
            }
        }
    }
}
