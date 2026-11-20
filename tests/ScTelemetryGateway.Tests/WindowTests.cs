using Sc.Game.Telemetry.V1;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Tests;

public sealed class WindowTests
{
    [Fact]
    public void ClosesIndependentFiveSecondWindowsWithoutResamplingRetainedState()
    {
        var clock = new ManualClock();
        const string speed = "vehicle.state.flight.instruments.speed";
        const string heading = "vehicle.state.flight.instruments.heading";
        var store = new TelemetryAggregator([speed, heading], Guid.NewGuid(), clock);
        store.Ingest(new()
        {
            Vehicle = new() { State = new() { Flight = new() { Instruments = new() { Speed = 12, Heading = 359 } } } }
        });
        clock.Advance(TimeSpan.FromSeconds(5));
        var first = store.CloseWindow(0)!;
        Assert.Equal(TimeSpan.FromSeconds(5), first.WindowEndedAt - first.WindowStartedAt);
        Assert.Equal(12, first.Statistics[speed].Mean);
        Assert.DoesNotContain(heading, first.Statistics.Keys);
        store.Ingest(new() { Actor = new() });
        clock.Advance(TimeSpan.FromSeconds(5));
        var next = store.CloseWindow(0)!;
        Assert.Equal(first.WindowEndedAt, next.WindowStartedAt);
        Assert.Equal(first.Snapshot[speed].UpdatedAt, next.Snapshot[speed].UpdatedAt);
        Assert.Empty(next.Statistics);
        store.Disconnected();
        store.Ingest(new() { Actor = new() });
        Assert.True(store.CloseWindow(0)!.Snapshot[speed].LastKnown);
        store.StartStream();
        store.Ingest(new() { Actor = new() });
        Assert.Null(store.CloseWindow(0));
    }

    [Fact]
    public void DisabledFieldsAndNoSelectedDataDoNotProduceBatches()
    {
        var store = new TelemetryAggregator([], Guid.NewGuid());
        store.Ingest(new() { Actor = new() { State = new() { UecBalance = 100 } } });
        Assert.Null(store.CloseWindow(0));
        var selective = new TelemetryAggregator(["turret.light_amplification"], Guid.NewGuid());
        selective.Ingest(new() { Actor = new() });
        Assert.Null(selective.CloseWindow(0));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
