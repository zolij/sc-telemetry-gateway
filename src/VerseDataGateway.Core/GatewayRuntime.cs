using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("VerseDataGateway.Tests")]

namespace VerseDataGateway.Core;

public sealed record GatewayStatus(string Game, string Delivery, string Mfd, int Queued, int QueueBytes,
    long BatchesDropped, long EventsDropped, DateTimeOffset? LastAcknowledged);

public sealed class GatewayRuntime
{
    private readonly object gate = new();
    private string game = "Stopped", delivery = "Stopped", mfd = "Stopped";
    private DateTimeOffset? lastAck;
    private long eventsDropped;
    private readonly BatchQueue queue = new();
    private readonly Func<BatchQueue, CancellationToken, Task>? runSender;
    private MfdServer? mfdServer;
    public MfdServer? MfdServer => mfdServer;

    public GatewayRuntime() { }

    internal GatewayRuntime(Func<BatchQueue, CancellationToken, Task> runSender) => this.runSender = runSender;

    public GatewayStatus Status
    {
        get { lock (gate) return new(game, delivery, mfd, queue.Count, queue.Bytes, queue.Dropped, eventsDropped, lastAck); }
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
        var remoteActive = remoteEnabled;
        void Flush()
        {
            lock (gate)
            {
                if (!remoteActive || remoteAggregator is null) return;
                var batch = remoteAggregator.CloseWindow(queue.Dropped);
                if (batch is null) return;
                queue.Enqueue(batch);
                eventsDropped = batch.Loss.EventsDropped;
            }
        }
        var reader = new GameTelemetryReader(options.GamePort);
        var sender = remoteEnabled ? new WebSocketDelivery(new Uri(options.Endpoint), key, queue,
            TimeSpan.FromSeconds(options.IntervalSeconds),
            value => { lock (gate) delivery = value; },
            () => { lock (gate) lastAck = DateTimeOffset.UtcNow; }) : null;
        async Task Aggregate()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.IntervalSeconds));
                while (await timer.WaitForNextTickAsync(remoteStop.Token)) Flush();
            }
            catch (OperationCanceledException) when (remoteStop.IsCancellationRequested && options.MfdEnabled)
            {
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
        }
        async Task ObserveRemote(Task senderTask)
        {
            try { await senderTask; }
            catch (ReceiverRejectedException ex)
            {
                lock (gate)
                {
                    remoteActive = false;
                    delivery = ex.Message;
                    queue.Clear();
                }
                await remoteStop.CancelAsync();
                await Task.Delay(Timeout.Infinite, stop.Token);
            }
        }
        var tasks = new List<Task>();
        var sinks = new List<Action<Sc.Game.Telemetry.V1.TelemetryStreamResponse>>();
        if (remoteAggregator is not null)
            sinks.Add(frame => { lock (gate) { if (remoteActive) remoteAggregator.Ingest(frame); } });
        if (mfdAggregator is not null)
            sinks.Add(mfdAggregator.Ingest);
        var readerTask = reader.RunAsync(sinks, value => { lock (gate) game = value; }, stop.Token, () =>
        {
            lock (gate) { if (remoteActive) remoteAggregator?.Disconnected(); }
            mfdAggregator?.Disconnected();
            mfdSnapshot?.Disconnected();
            Flush();
        }, () =>
        {
            lock (gate) { if (remoteActive) remoteAggregator?.StartStream(); }
            mfdSnapshot?.StartStream();
        }, rawSink: (frame, payload) => mfdSnapshot?.Ingest(frame, payload));
        tasks.Add(readerTask);
        if (remoteEnabled)
        {
            var senderTask = runSender is null ? sender!.RunAsync(remoteStop.Token) : runSender(queue, remoteStop.Token);
            if (options.MfdEnabled)
            {
                tasks.Add(Aggregate());
                tasks.Add(ObserveRemote(senderTask));
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
                    await mfdServer.DisposeAsync();
                lock (gate) { game = "Stopped"; delivery = "Stopped"; mfd = "Stopped"; }
            }
        }
    }
}
