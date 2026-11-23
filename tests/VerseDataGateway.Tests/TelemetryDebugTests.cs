using Google.Protobuf;
using Sc.Game.Telemetry.V1;
using System.Text.Json;
using VerseDataGateway.TelemetryDebug;

namespace VerseDataGateway.Tests;

public sealed class TelemetryDebugTests
{
    [Fact]
    public void DisplaysWirePresenceAndExplicitDefaultValues()
    {
        byte[] payload = [0x32, 0x04, 0x12, 0x02, 0x08, 0x00];
        var view = new TelemetryDebugView();
        view.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        var output = view.Render(DateTimeOffset.UtcNow);

        Assert.Contains("locomotion=ACTOR_LOCOMOTION_STATE_UNSPECIFIED (0)", output);
        Assert.Contains("crew_role=<omitted>", output);
        Assert.Contains("vitals=<omitted>", output);
        Assert.Contains("[vehicle] waiting", output);
    }

    [Fact]
    public void PreservesRawNumbersListsAndStrings()
    {
        var frame = new TelemetryStreamResponse
        {
            Vehicle = new() { State = new()
            {
                Name = "Ship \"A\"",
                Flight = new() { Instruments = new() { Speed = float.MaxValue } },
                Shields = new() { Faces = { 0.25f, 0.75f } }
            } },
            Actor = new() { State = new() { UecBalance = 9007199254740993 } }
        };
        var payload = frame.ToByteArray();
        var view = new TelemetryDebugView();
        view.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        var output = view.Render(DateTimeOffset.UtcNow);

        Assert.Contains("name=" + JsonSerializer.Serialize("Ship \"A\""), output);
        Assert.Contains("speed=3.4028235E+38", output);
        Assert.Contains("faces=[0.25, 0.75]", output);
        Assert.Contains("uec_balance=9007199254740993", output);
    }

    [Fact]
    public void RetainsOtherSectionsUntilStreamIsCleared()
    {
        var view = new TelemetryDebugView();
        var actor = new TelemetryStreamResponse { Actor = new() { Vitals = new() { HeartRate = 72 } } };
        var vehicle = new TelemetryStreamResponse { Vehicle = new() { State = new() { Name = "Ship" } } };
        view.Ingest(actor, actor.ToByteArray());
        view.Ingest(vehicle, vehicle.ToByteArray());
        var output = view.Render(DateTimeOffset.UtcNow);
        Assert.Contains("heart_rate=72", output);
        Assert.Contains("name=\"Ship\"", output);

        view.Clear();
        output = view.Render(DateTimeOffset.UtcNow);
        Assert.DoesNotContain("heart_rate=72", output);
        Assert.Contains("[actor] waiting", output);
        Assert.Contains("[vehicle] waiting", output);
    }
}
