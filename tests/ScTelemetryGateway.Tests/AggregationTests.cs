using Google.Protobuf;
using Sc.Game.Telemetry.V1;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Tests;

public sealed class AggregationTests
{
    private const string Speed = "vehicle.state.flight.instruments.speed";
    private static TelemetryStreamResponse Vehicle(float speed) => new()
    {
        Vehicle = new() { State = new() { Name = "Private name", Flight = new() { Instruments = new() { Speed = speed } } } }
    };

    [Fact]
    public void FiltersBeforeAggregatingAndCountsZero()
    {
        var store = new TelemetryAggregator([Speed], Guid.NewGuid());
        store.Ingest(Vehicle(0));
        store.Ingest(Vehicle(20));
        var batch = Assert.IsType<TelemetryBatch>(store.CloseWindow(0));
        Assert.Single(batch.Snapshot);
        Assert.Equal(2, batch.Statistics[Speed].Count);
        Assert.Equal(10, batch.Statistics[Speed].Mean);
        Assert.Equal(0, batch.Statistics[Speed].Min);
        Assert.Equal(20, batch.Statistics[Speed].Max);
        Assert.DoesNotContain("Private name", System.Text.Encoding.UTF8.GetString(WireJson.Serialize(batch)));
        Assert.Null(store.CloseWindow(0));
    }

    [Fact]
    public void ExcludesSentinelsAndPreservesInt64AndFalse()
    {
        var fields = new[] { Speed, "actor.state.uec_balance", "vehicle.state.lights_enabled" };
        var store = new TelemetryAggregator(fields, Guid.NewGuid());
        store.Ingest(Vehicle(float.MaxValue));
        store.Ingest(Vehicle(float.NaN));
        store.Ingest(new() { Actor = new() { State = new() { UecBalance = long.MaxValue } } });
        var batch = Assert.IsType<TelemetryBatch>(store.CloseWindow(0));
        Assert.Equal(2, batch.Statistics[Speed].InvalidCount);
        Assert.Equal(0, batch.Statistics[Speed].Count);
        Assert.Equal(long.MaxValue.ToString(), batch.Snapshot["actor.state.uec_balance"].Value!.GetValue<string>());
        Assert.False(batch.Snapshot["vehicle.state.lights_enabled"].Value!.GetValue<bool>());
    }

    [Fact]
    public void OptionalAbsenceReplacesSectionAndReconnectClearsState()
    {
        const string gear = "vehicle.state.flight.state.gear";
        var store = new TelemetryAggregator([gear], Guid.NewGuid());
        var frame = new TelemetryStreamResponse
        {
            Vehicle = new() { State = new() { Flight = new() { State = new() { Gear = VehicleComponentDeploymentState.Deployed } } } }
        };
        store.Ingest(frame);
        Assert.Single(store.CloseWindow(0)!.Snapshot);
        frame.Vehicle.State.Flight.State.ClearGear();
        store.Ingest(frame);
        Assert.Null(store.CloseWindow(0));
        store.StartStream();
        store.Ingest(new() { Actor = new() });
        Assert.Null(store.CloseWindow(0));
    }

    [Fact]
    public void RetainsDuplicateEventsAndReportsOverflow()
    {
        var store = new TelemetryAggregator(["events.events.type"], Guid.NewGuid());
        var frame = new TelemetryStreamResponse { Events = new() };
        for (var i = 0; i < TelemetryAggregator.MaxEvents + 1; i++)
            frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorLeaveSeat });
        store.Ingest(frame);
        var batch = store.CloseWindow(0)!;
        Assert.Equal(TelemetryAggregator.MaxEvents, batch.Events.Count);
        Assert.Equal(1, batch.Loss.EventsDropped);
        Assert.Equal(2, batch.Events[0].Sequence);
    }

    [Fact]
    public void RepeatedLeavesDoNotIncludeSiblingNamesAndUnknownWireFieldsAreIgnored()
    {
        var store = new TelemetryAggregator(["weapons.weapons.is_powered"], Guid.NewGuid());
        var frame = new TelemetryStreamResponse { Weapons = new() };
        frame.Weapons.Weapons.Add(new WeaponState { Name = "Secret", IsPowered = true });
        var bytes = frame.ToByteArray().Concat(new byte[] { 0x98, 0x06, 0x01 }).ToArray();
        store.Ingest(TelemetryStreamResponse.Parser.ParseFrom(bytes));
        var batch = store.CloseWindow(0)!;
        Assert.Equal("[true]", batch.Snapshot["weapons.weapons.is_powered"].Value!.ToJsonString());
        Assert.Empty(batch.Statistics);
        Assert.DoesNotContain("Secret", System.Text.Encoding.UTF8.GetString(WireJson.Serialize(batch)));
    }
}
