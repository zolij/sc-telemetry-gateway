using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public static class MockConsole
{
    public const string Help = "Commands: seat, foot, event <spawn|dead|enter-seat|leave-seat|respawn|downed>, help";

    private static readonly IReadOnlyDictionary<string, GameEvent.Types.GameEventType> EventTypes =
        new Dictionary<string, GameEvent.Types.GameEventType>(StringComparer.OrdinalIgnoreCase)
        {
            ["spawn"] = GameEvent.Types.GameEventType.ActorSpawn,
            ["dead"] = GameEvent.Types.GameEventType.ActorDead,
            ["enter-seat"] = GameEvent.Types.GameEventType.ActorEnterSeat,
            ["leave-seat"] = GameEvent.Types.GameEventType.ActorLeaveSeat,
            ["respawn"] = GameEvent.Types.GameEventType.ActorRespawn,
            ["downed"] = GameEvent.Types.GameEventType.ActorDowned
        };

    public static string Execute(MockState state, string input)
    {
        var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Equals("help", StringComparison.OrdinalIgnoreCase)) return Help;
        if (parts.Length == 1 && parts[0].Equals("seat", StringComparison.OrdinalIgnoreCase))
        {
            state.SetSeated(true);
            return "Simulated actor seated.";
        }
        if (parts.Length == 1 && parts[0].Equals("foot", StringComparison.OrdinalIgnoreCase))
        {
            state.SetSeated(false);
            return "Simulated actor on foot.";
        }
        if (parts.Length == 2 && parts[0].Equals("event", StringComparison.OrdinalIgnoreCase) &&
            EventTypes.TryGetValue(parts[1], out var type))
        {
            state.Emit(type);
            return $"Emitted simulated event: {parts[1]}.";
        }
        return "Invalid command. " + Help;
    }

    public static async Task RunAsync(MockState state, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await Console.In.ReadLineAsync(cancellationToken);
                if (line is null) return;
                Console.WriteLine(Execute(state, line));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException ex) { Console.Error.WriteLine($"Mock console input failed: {ex.Message}"); }
    }
}
