using System.Text.Json;
using Sc.Game.Telemetry.V1;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Tests;

public sealed class MfdTests
{
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
