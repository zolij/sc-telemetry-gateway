using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public sealed class MockState
{
    public TelemetryStreamResponse Frame() => new()
    {
        Actor = new()
        {
            Vitals = new() { HeartRate = 72 },
            State = new() { Locomotion = ActorLocomotionState.Seated }
        }
    };
}
