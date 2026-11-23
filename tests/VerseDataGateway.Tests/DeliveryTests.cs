using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sc.Game.Telemetry.V1;
using VerseDataGateway.Core;

namespace VerseDataGateway.Tests;

public sealed class DeliveryTests
{
    [Fact]
    public Task ResendsIdenticalBatchAfterLostAckAndAcceptsFragmentedAck() => VerifyDelivery(false);

    [ProxyFact]
    public Task DeliversThroughNginxReverseProxy() => VerifyDelivery(true);

    private static async Task VerifyDelivery(bool proxy)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var bodies = new List<string>();
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(proxy ? IPAddress.Any : IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/custom/ingest", async context =>
        {
            Assert.Equal("Bearer synthetic-test-key", context.Request.Headers.Authorization.ToString());
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var body = await Read(socket, timeout.Token);
            lock (bodies) bodies.Add(body);
            if (Interlocked.Increment(ref connections) == 1)
            {
                socket.Abort();
                return;
            }
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("batch_id").GetGuid();
            var ack = Encoding.UTF8.GetBytes($"{{\"type\":\"telemetry.ack\",\"schema_version\":1,\"batch_id\":\"{id}\"}}");
            await socket.SendAsync(ack.AsMemory(0, 10), WebSocketMessageType.Text, false, timeout.Token);
            await socket.SendAsync(ack.AsMemory(10), WebSocketMessageType.Text, true, timeout.Token);
            try { await Read(socket, timeout.Token); }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        await app.StartAsync(timeout.Token);
        var origin = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        ProxyContainer? container = null;
        try
        {
            var endpoint = new UriBuilder(origin) { Scheme = "ws", Path = "/custom/ingest" }.Uri;
            if (proxy)
            {
                container = await ProxyContainer.Start(origin.Port, timeout.Token);
                endpoint = new Uri($"ws://127.0.0.1:{container.Port}/custom/ingest");
            }
            await using var game = await StartGame(timeout.Token);
            var gameAddress = new Uri(game.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            var store = new TelemetryAggregator(["actor.vitals.heart_rate"], Guid.NewGuid());
            using var readerStop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var frameReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readerTask = new GameTelemetryReader(gameAddress.Port).RunAsync(store,
                value => { if (value == "Game connected") frameReceived.TrySetResult(); }, readerStop.Token);
            await frameReceived.Task.WaitAsync(timeout.Token);
            var queue = new BatchQueue();
            queue.Enqueue(store.CloseWindow(0)!);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var sender = new WebSocketDelivery(endpoint, "synthetic-test-key", queue,
                TimeSpan.FromMilliseconds(20), _ => { }, () => acknowledged.TrySetResult());
            var task = sender.RunAsync(stop.Token);
            await acknowledged.Task.WaitAsync(timeout.Token);
            Assert.Equal(0, queue.Count);
            lock (bodies)
            {
                Assert.Equal(2, bodies.Count);
                Assert.Equal(bodies[0], bodies[1]);
            }
            await stop.CancelAsync();
            await Stop(task, stop.Token);
            await readerStop.CancelAsync();
            await Stop(readerTask, readerStop.Token);
            await game.StopAsync(CancellationToken.None);
        }
        finally
        {
            await timeout.CancelAsync();
            if (container is not null) await container.DisposeAsync();
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("invalid")]
    [InlineData("oversized")]
    [InlineData("unauthorized")]
    [InlineData("redirect")]
    public async Task RejectsUnsafeReceiverResponsesWithoutAcknowledging(string mode)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            if (mode == "unauthorized") { context.Response.StatusCode = 401; return; }
            if (mode == "redirect")
            {
                context.Response.StatusCode = 302;
                context.Response.Headers.Location = "/must-not-follow";
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await Read(socket, timeout.Token);
            var reply = mode switch
            {
                "foreign" => $"{{\"type\":\"telemetry.ack\",\"schema_version\":1,\"batch_id\":\"{Guid.NewGuid()}\"}}",
                "oversized" => new string('x', 17000),
                _ => "{\"type\":\"telemetry.ack\",\"schema_version\":1,\"surprise\":true}"
            };
            await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, timeout.Token);
        });
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var queue = new BatchQueue();
        queue.Enqueue(new PendingBatch(Guid.NewGuid(), "{}"u8.ToArray()));
        var sender = new WebSocketDelivery(new Uri(address.Replace("http:", "ws:")), "synthetic", queue,
            TimeSpan.FromMilliseconds(10), _ => { }, () => Assert.Fail("Must not acknowledge"));
        await Assert.ThrowsAsync<ReceiverRejectedException>(() => sender.RunAsync(timeout.Token));
        Assert.Equal(1, queue.Count);
        await app.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RespectsHandshakeAndApplicationBackoff(bool applicationError)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var attempts = new List<DateTimeOffset>();
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            int attempt;
            lock (attempts) { attempts.Add(DateTimeOffset.UtcNow); attempt = attempts.Count; }
            if (attempt == 1 && !applicationError)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers.RetryAfter = "2";
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var body = await Read(socket, timeout.Token);
            using var doc = JsonDocument.Parse(body);
            var id = doc.RootElement.GetProperty("batch_id").GetGuid();
            var reply = attempt == 1
                ? """{"type":"telemetry.error","schema_version":1,"code":"rate_limited","retry_after_seconds":2}"""
                : $$"""{"type":"telemetry.ack","schema_version":1,"batch_id":"{{id}}"}""";
            await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, timeout.Token);
            try { await Read(socket, timeout.Token); }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var store = new TelemetryAggregator(["turret.light_amplification"], Guid.NewGuid());
        store.Ingest(new() { Turret = new() });
        var queue = new BatchQueue();
        queue.Enqueue(store.CloseWindow(0)!);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = new WebSocketDelivery(new Uri(address.Replace("http:", "ws:")), "synthetic", queue,
            TimeSpan.FromMilliseconds(20), _ => { }, () => accepted.TrySetResult()).RunAsync(stop.Token);
        await accepted.Task.WaitAsync(timeout.Token);
        lock (attempts)
        {
            Assert.Equal(2, attempts.Count);
            Assert.True(attempts[1] - attempts[0] >= TimeSpan.FromSeconds(2),
                $"Observed retry interval: {(attempts[1] - attempts[0]).TotalMilliseconds} ms.");
        }
        await stop.CancelAsync();
        await Stop(task, stop.Token);
        Assert.Equal(0, queue.Count);
        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DoesNotBurstQueuedBatchesOrSendAfterCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = 0;
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    var body = await Read(socket, timeout.Token);
                    if (body.Length == 0) break;
                    Interlocked.Increment(ref received);
                    using var doc = JsonDocument.Parse(body);
                    var id = doc.RootElement.GetProperty("batch_id").GetGuid();
                    var ack = Encoding.UTF8.GetBytes($$"""{"type":"telemetry.ack","schema_version":1,"batch_id":"{{id}}"}""");
                    await socket.SendAsync(ack, WebSocketMessageType.Text, true, timeout.Token);
                }
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var store = new TelemetryAggregator(["turret.light_amplification"], Guid.NewGuid());
        var queue = new BatchQueue();
        for (var i = 0; i < 3; i++)
        {
            store.Ingest(new() { Turret = new() });
            queue.Enqueue(store.CloseWindow(0)!);
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = new WebSocketDelivery(new Uri(address.Replace("http:", "ws:")), "synthetic", queue,
            TimeSpan.FromSeconds(2), _ => { }, () => accepted.TrySetResult()).RunAsync(stop.Token);
        await accepted.Task.WaitAsync(timeout.Token);
        await Task.Delay(250, timeout.Token);
        Assert.Equal(1, Volatile.Read(ref received));
        await stop.CancelAsync();
        await Stop(task, stop.Token);
        await Task.Delay(250, timeout.Token);
        Assert.Equal(1, Volatile.Read(ref received));
        Assert.Equal(2, queue.Count);
        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KeepsBatchQueuedWhenAcknowledgementTimesOut()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Map("/", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                await Read(socket, timeout.Token);
                await Read(socket, timeout.Token);
            }
            catch (WebSocketException) { }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        });
        await app.StartAsync(timeout.Token);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var queue = new BatchQueue();
        queue.Enqueue(new PendingBatch(Guid.NewGuid(), "{}"u8.ToArray()));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var sender = new WebSocketDelivery(new Uri(address.Replace("http:", "ws:")), "synthetic", queue,
            TimeSpan.FromSeconds(1),
            value => { if (value == "Receiver connect/send/ACK timeout; reconnecting") timedOut.TrySetResult(); },
            () => Assert.Fail("Unacknowledged data must not be accepted"));
        var task = sender.RunAsync(stop.Token);
        await timedOut.Task.WaitAsync(timeout.Token);
        Assert.Equal(1, queue.Count);
        await stop.CancelAsync();
        await Stop(task, stop.Token);
        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReadsSyntheticGrpcStreamIntoSelectedAggregates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var app = await StartGame(timeout.Token);
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var store = new TelemetryAggregator(["actor.vitals.heart_rate"], Guid.NewGuid());
        var mfd = new MfdSnapshotStore(["actor.vitals.heart_rate", "actor.vitals.gravity"]);
        var reader = new GameTelemetryReader(address.Port);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = reader.RunAsync([store.Ingest], state =>
        {
            if (state == "Game connected") received.TrySetResult();
        }, stop.Token, rawSink: mfd.Ingest);
        await received.Task.WaitAsync(timeout.Token);
        var batch = store.CloseWindow(0)!;
        Assert.Equal(72, batch.Statistics["actor.vitals.heart_rate"].Last);
        Assert.Equal(72, mfd.Current().Fields["actor.vitals.heart_rate"].Value!.GetValue<float>());
        Assert.DoesNotContain("actor.vitals.gravity", mfd.Current().Fields.Keys);
        await stop.CancelAsync();
        await Stop(task, stop.Token);
        await app.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AggregatorOverloadResetsStateOnReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var state = new ReconnectingGameState();
        await using var app = await StartReconnectingGame(state, timeout.Token);
        var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var store = new TelemetryAggregator(["actor.vitals.heart_rate", "vehicle.state.flight.instruments.speed"], Guid.NewGuid());
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = new GameTelemetryReader(address.Port).RunAsync(store, status =>
        {
            if (status != "Game connected") return;
            if (Interlocked.Increment(ref frames) == 1) first.TrySetResult();
            else second.TrySetResult();
        }, stop.Token);
        try
        {
            await first.Task.WaitAsync(timeout.Token);
            var before = store.CloseWindow(0)!;
            state.FirstRelease.TrySetResult();
            await second.Task.WaitAsync(timeout.Token);
            var after = store.CloseWindow(0)!;
            Assert.NotEqual(before.StreamId, after.StreamId);
            Assert.DoesNotContain("actor.vitals.heart_rate", after.Snapshot.Keys);
            Assert.Contains("vehicle.state.flight.instruments.speed", after.Snapshot.Keys);
        }
        finally
        {
            await stop.CancelAsync();
            await Stop(task, stop.Token);
        }
    }

    [Fact]
    public async Task ReceiverRejectionDisablesRemoteAcrossGameReconnectsButKeepsMfdRunning()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var state = new ReconnectingGameState();
        await using var game = await StartReconnectingGame(state, timeout.Token);
        var gameAddress = new Uri(game.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var sender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new GatewayRuntime((_, token) => sender.Task.WaitAsync(token));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = runtime.RunAsync(RuntimeOptions(gameAddress.Port), "synthetic", stop.Token, Directory.GetCurrentDirectory());
        try
        {
            await WaitUntil(() => runtime.Status.Mfd == "Listener active" && runtime.Status.Queued > 0, timeout.Token);
            sender.SetException(new ReceiverRejectedException("Rejected for test"));
            await WaitUntil(() => runtime.Status.Delivery == "Rejected for test", timeout.Token);
            Assert.Equal(0, runtime.Status.Queued);
            state.FirstRelease.TrySetResult();
            await state.SecondStarted.Task.WaitAsync(timeout.Token);
            await WaitUntil(() => runtime.Status.Game == "Game connected", timeout.Token);
            state.SecondRelease.TrySetResult();
            await state.ThirdStarted.Task.WaitAsync(timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
            Assert.False(task.IsCompleted);
            Assert.Equal("Listener active", runtime.Status.Mfd);
            Assert.Equal("Rejected for test", runtime.Status.Delivery);
            Assert.Equal(0, runtime.Status.Queued);
        }
        finally
        {
            await stop.CancelAsync();
            await Stop(task, stop.Token);
        }
        Assert.Equal("Stopped", runtime.Status.Mfd);
        Assert.Equal(0, runtime.Status.Queued);
    }

    [Fact]
    public async Task CombinedRuntimeObservesUnexpectedSenderFaultAndCleansUp()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = new ReconnectingGameState();
        await using var game = await StartReconnectingGame(state, timeout.Token);
        var gameAddress = new Uri(game.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var sender = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new GatewayRuntime((_, token) => sender.Task.WaitAsync(token));
        var task = runtime.RunAsync(RuntimeOptions(gameAddress.Port), "synthetic", timeout.Token, Directory.GetCurrentDirectory());
        await WaitUntil(() => runtime.Status.Mfd == "Listener active", timeout.Token);
        sender.SetException(new InvalidOperationException("Unexpected sender failure"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => task.WaitAsync(timeout.Token));
        Assert.Equal("Unexpected sender failure", error.Message);
        Assert.Equal("Stopped", runtime.Status.Mfd);
        Assert.Equal(0, runtime.Status.Queued);
    }

    [Fact]
    public async Task CombinedRuntimeJoinsSenderBeforeCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = new ReconnectingGameState();
        await using var game = await StartReconnectingGame(state, timeout.Token);
        var gameAddress = new Uri(game.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new GatewayRuntime(async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                stopping.TrySetResult();
                await release.Task;
            }
        });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var task = runtime.RunAsync(RuntimeOptions(gameAddress.Port), "synthetic", stop.Token, Directory.GetCurrentDirectory());
        try
        {
            await WaitUntil(() => runtime.Status.Mfd == "Listener active", timeout.Token);
            await stop.CancelAsync();
            await stopping.Task.WaitAsync(timeout.Token);
            Assert.False(task.IsCompleted);
            Assert.Equal("Listener active", runtime.Status.Mfd);
        }
        finally
        {
            release.TrySetResult();
            await stop.CancelAsync();
            await Stop(task, stop.Token);
        }
        Assert.Equal("Stopped", runtime.Status.Mfd);
    }

    private static GatewayOptions RuntimeOptions(int gamePort)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var mfdPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        return new GatewayOptions
        {
            GamePort = gamePort, Endpoint = "wss://receiver.example/", IntervalSeconds = 1,
            Fields = ["actor.vitals.heart_rate", "vehicle.state.flight.instruments.speed"],
            MfdEnabled = true, MfdAddress = "127.0.0.1", MfdPort = mfdPort,
            MfdFields = ["actor.vitals.heart_rate"]
        };
    }

    private static async Task WaitUntil(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(20, token);
    }

    private static async Task<WebApplication> StartReconnectingGame(ReconnectingGameState state, CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(state);
        var app = builder.Build();
        app.MapGrpcService<ReconnectingGame>();
        await app.StartAsync(token);
        return app;
    }

    public sealed class ReconnectingGameState
    {
        public int Calls;
        public TaskCompletionSource FirstRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ReconnectingGame(ReconnectingGameState state) : GameTelemetryService.GameTelemetryServiceBase
    {
        public override async Task TelemetryStream(TelemetryStreamRequest request,
            IServerStreamWriter<TelemetryStreamResponse> responseStream, ServerCallContext context)
        {
            var call = Interlocked.Increment(ref state.Calls);
            if (call == 2) state.SecondStarted.TrySetResult();
            if (call >= 3) state.ThirdStarted.TrySetResult();
            await responseStream.WriteAsync(call == 1
                ? new() { Actor = new() { Vitals = new() { HeartRate = 72 } } }
                : new() { Vehicle = new() { State = new() { Flight = new() { Instruments = new() { Speed = 42 } } } } });
            try
            {
                await (call == 1 ? state.FirstRelease.Task : call == 2 ? state.SecondRelease.Task : Task.Delay(Timeout.Infinite, context.CancellationToken))
                    .WaitAsync(context.CancellationToken);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<WebApplication> StartGame(CancellationToken token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        var app = builder.Build();
        app.MapGrpcService<SyntheticGame>();
        await app.StartAsync(token);
        return app;
    }

    public sealed class SyntheticGame : GameTelemetryService.GameTelemetryServiceBase
    {
        public override async Task TelemetryStream(TelemetryStreamRequest request,
            IServerStreamWriter<TelemetryStreamResponse> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new() { Actor = new() { Vitals = new() { HeartRate = 72 } } });
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        }
    }

    private static async Task<string> Read(WebSocket socket, CancellationToken token)
    {
        using var stream = new MemoryStream();
        var bytes = new byte[4096];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(bytes, token);
            if (result.MessageType == WebSocketMessageType.Close) return "";
            stream.Write(bytes, 0, result.Count);
            if (stream.Length > BatchQueue.MaxBatchBytes) throw new InvalidDataException("Test payload too large.");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task Stop(Task task, CancellationToken token)
    {
        try { await task; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private sealed class ProxyContainer(string name, string directory, int port) : IAsyncDisposable
    {
        public int Port { get; } = port;
        public static async Task<ProxyContainer> Start(int originPort, CancellationToken token)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var name = "vdg-test-" + Guid.NewGuid().ToString("N");
            var directory = Path.Combine(Path.GetTempPath(), name);
            Directory.CreateDirectory(directory);
            var config = Path.Combine(directory, "nginx.conf");
            await File.WriteAllTextAsync(config, $$"""
                events {}
                http {
                  access_log off;
                  server {
                    listen 8080;
                    location / {
                      proxy_pass http://{{Environment.GetEnvironmentVariable("TEST_PROXY_ORIGIN_HOST") ?? "host.docker.internal"}}:{{originPort}};
                      proxy_http_version 1.1;
                      proxy_set_header Upgrade $http_upgrade;
                      proxy_set_header Connection "upgrade";
                      proxy_set_header Authorization $http_authorization;
                      proxy_read_timeout 60s;
                    }
                  }
                }
                """, token);
            var container = new ProxyContainer(name, directory, port);
            try
            {
                await Docker(["run", "-d", "--rm", "--name", name,
                    "--add-host", "host.docker.internal:host-gateway", "-p", $"127.0.0.1:{port}:8080",
                    "-v", config + ":/etc/nginx/nginx.conf:ro", "nginx:stable-alpine"], token);
                using var http = new HttpClient();
                for (var i = 0; i < 50; i++)
                {
                    try
                    {
                        using var response = await http.GetAsync($"http://127.0.0.1:{port}/health", token);
                        return container;
                    }

                    catch (HttpRequestException) { await Task.Delay(100, token); }
                }
                throw new TimeoutException("Test proxy did not start.");
            }
            catch
            {
                await container.DisposeAsync();
                throw;
            }
        }

        private static async Task Docker(string[] args, CancellationToken token)
        {
            var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start Docker.");
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error + await output);
        }

        public async ValueTask DisposeAsync()
        {
            await Docker(["rm", "-f", name], CancellationToken.None);
            File.Delete(Path.Combine(directory, "nginx.conf"));
            Directory.Delete(directory);
        }
    }

    public sealed class ProxyFactAttribute : FactAttribute
    {
        public ProxyFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("RUN_PROXY_TESTS") != "1")
                Skip = "Set RUN_PROXY_TESTS=1 to run the Docker Nginx integration test.";
        }
    }
}
