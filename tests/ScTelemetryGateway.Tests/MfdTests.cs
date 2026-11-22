using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using Sc.Game.Telemetry.V1;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Tests;

public sealed class MfdTests
{
    [Fact]
    public async Task ServerStartsAndServesBundledIndex()
    {
        var root = Path.Combine(Path.GetTempPath(), "sctg-mfd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "synthetic MFD");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            await using var server = new MfdServer(IPAddress.Loopback, port, new MfdSnapshotStore([]), root);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(timeout.Token);
            using var client = new HttpClient();
            Assert.Equal("synthetic MFD", await client.GetStringAsync($"http://127.0.0.1:{port}/", timeout.Token));
        }
        finally
        {
            File.Delete(Path.Combine(root, "index.html"));
            Directory.Delete(root);
        }
    }

    [Fact]
    public void SnapshotUsesIndependentSelectionAndClearsOnStreamStart()
    {
        var store = new MfdSnapshotStore(["vehicle.state.flight.instruments.speed"]);
        store.Ingest(new TelemetryStreamResponse
        {
            Vehicle = new()
            {
                State = new()
                {
                    Flight = new() { Instruments = new() { Speed = 42 } }
                }
            }
        });
        var first = store.Current();
        Assert.Equal("connected", first.ConnectionStatus);
        Assert.Equal(42, first.Fields["vehicle.state.flight.instruments.speed"].Value!.GetValue<float>());

        store.StartStream();
        var reset = store.Current();
        Assert.Empty(reset.Fields);
        Assert.Equal("connected", reset.ConnectionStatus);
        Assert.NotEqual(first.StreamId, reset.StreamId);
    }

    [Fact]
    public void PausedAndDisconnectedStatesClearStaleData()
    {
        var store = new MfdSnapshotStore(["actor.vitals.heart_rate"]);
        store.Ingest(new() { Actor = new() { Vitals = new() { HeartRate = 80 } } });
        store.Disconnected();
        var disconnected = store.Current();
        Assert.Equal("disconnected", disconnected.ConnectionStatus);
        Assert.True(disconnected.Fields["actor.vitals.heart_rate"].LastKnown);

        store.SetPaused();
        var paused = store.Current();
        Assert.Equal("paused", paused.ConnectionStatus);
        Assert.Empty(paused.Fields);
        Assert.Null(paused.LastFrameAt);
    }

    [Fact]
    public void AgesFieldsAndFramesWithMonotonicTimeAcrossClockChanges()
    {
        var clock = new MfdClock();
        const string speed = "vehicle.state.flight.instruments.speed";
        const string heartRate = "actor.vitals.heart_rate";
        var store = new MfdSnapshotStore([speed, heartRate], clock);
        store.Ingest(new() { Vehicle = new() { State = new() { Flight = new() { Instruments = new() { Speed = 12 } } } } });
        clock.Advance(TimeSpan.FromSeconds(3));
        clock.WallTime -= TimeSpan.FromHours(1);
        store.Ingest(new() { Actor = new() { Vitals = new() { HeartRate = 72 } } });
        clock.Advance(TimeSpan.FromMilliseconds(500));

        var snapshot = store.Current();
        Assert.Equal(3500, snapshot.Fields[speed].AgeMs);
        Assert.Equal(500, snapshot.Fields[heartRate].AgeMs);
        Assert.Equal(500, snapshot.LastFrameAgeMs);
        Assert.Contains(speed, snapshot.SelectedFields);
        store.StartStream();
        Assert.Null(store.Current().LastFrameAgeMs);
    }

    private sealed class MfdClock : TimeProvider
    {
        public DateTimeOffset WallTime { get; set; } = DateTimeOffset.UnixEpoch;
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => WallTime;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan by) { timestamp += by.Ticks; WallTime += by; }
    }

    [Fact]
    public void IncompleteRemoteSelectionCanBeEditedButCannotStart()
    {
        var options = JsonSerializer.Deserialize<GatewayOptions>(
            """{"endpoint":"wss://example.org/telemetry","fields":[]}""", WireJson.Options)!;
        options.Validate(requireDestination: false);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void WildcardMfdAddressIsRejected()
    {
        var options = new GatewayOptions
        {
            MfdEnabled = true, MfdAddress = "0.0.0.0", MfdFields = ["actor.state.locomotion"]
        };
        Assert.Throws<ArgumentException>(() => options.Validate(requireDestination: false));
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void SnapshotIncludesSelectedEventTypesAndReplacesTheEventSection()
    {
        const string eventType = "events.events.type";
        var store = new MfdSnapshotStore([eventType]);
        var frame = new TelemetryStreamResponse { Events = new() };
        frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorLeaveSeat });
        store.Ingest(frame);
        Assert.Equal("[\"GAME_EVENT_TYPE_ACTOR_LEAVE_SEAT\"]", store.Current().Fields[eventType].Value!.ToJsonString());
        store.Ingest(new() { Actor = new() });
        Assert.True(store.Current().Fields.ContainsKey(eventType));
        store.Ingest(new() { Events = new() });
        Assert.Equal("[]", store.Current().Fields[eventType].Value!.ToJsonString());
    }

    [Fact]
    public void MigratedOptionsPreserveRemoteAndKeepMfdOff()
    {
        var options = JsonSerializer.Deserialize<GatewayOptions>(
            """{"endpoint":"wss://example.org/telemetry","fields":["actor.state.locomotion"]}""",
            WireJson.Options)!;
        Assert.True(options.RemoteEnabled);
        Assert.False(options.MfdEnabled);
        Assert.True(options.RemoteConfigured);
        Assert.Single(options.Fields);
        Assert.Empty(options.MfdFields);
    }
}
