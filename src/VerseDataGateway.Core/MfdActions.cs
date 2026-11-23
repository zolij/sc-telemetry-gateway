using Grpc.Core;
using Grpc.Net.Client;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.Core;

internal sealed class MfdActions(int port, MfdSnapshotStore snapshot) : IDisposable
{
    public const uint MaxHoldMilliseconds = 5000;
    private readonly GrpcChannel channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
        new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024 });
    private readonly SemaphoreSlim triggerLock = new(1, 1);
    private long lastTrigger;

    private static readonly IReadOnlyDictionary<string, string> FieldByAction =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v_lights"] = "vehicle.state.lights_enabled",
            ["v_lights_on"] = "vehicle.state.lights_enabled",
            ["v_lights_off"] = "vehicle.state.lights_enabled"
        };

    public async Task<IReadOnlyList<MfdAction>> ListAsync(CancellationToken cancellationToken)
    {
        var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
        var response = await client.ListTriggerableActionsAsync(new(),
            deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken);
        if (response.Actions.Count > 512)
            throw new InvalidDataException("The game returned too many actions.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MfdAction>();
        foreach (var action in response.Actions)
        {
            if (!ValidName(action.ActionName) || !ValidName(action.Category) || !seen.Add(action.ActionName))
                throw new InvalidDataException("The game returned an invalid action list.");
            FieldByAction.TryGetValue(action.ActionName, out var field);
            result.Add(new(action.ActionName, action.Category, action.Holdable, field,
                field is null || snapshot.SelectedFields.Contains(field, StringComparer.Ordinal)));
        }
        return result;
    }

    public async Task<TriggerGameActionResponse> TriggerAsync(string name, uint? holdMs,
        Func<Action, bool> dispatchIfAuthorized, CancellationToken cancellationToken)
    {
        if (!ValidName(name)) throw new ArgumentException("Invalid action name.");
        if (holdMs is 0 or > MaxHoldMilliseconds)
            throw new ArgumentException($"Hold duration must be between 1 and {MaxHoldMilliseconds} milliseconds.");
        if (!await triggerLock.WaitAsync(0, cancellationToken))
            throw new MfdActionBusyException();
        try
        {
            var actions = await ListAsync(cancellationToken);
            var action = actions.SingleOrDefault(item => item.ActionName == name)
                ?? throw new ArgumentException("This action is not currently listed by the game.");
            if (!action.Enabled) throw new MfdActionForbiddenException();
            if (holdMs.HasValue && !action.Holdable)
                throw new ArgumentException("This action does not support a hold duration.");
            if (snapshot.Current().ConnectionStatus != "connected")
                throw new MfdActionForbiddenException();
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (lastTrigger != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastTrigger, now) < TimeSpan.FromMilliseconds(250))
                throw new MfdActionBusyException();
            var request = new TriggerGameActionRequest { ActionName = name };
            if (holdMs.HasValue) request.HoldMs = holdMs.Value;
            var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
            AsyncUnaryCall<TriggerGameActionResponse>? call = null;
            if (!dispatchIfAuthorized(() => call = client.TriggerGameActionAsync(request,
                deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: cancellationToken)))
                throw new MfdActionForbiddenException();
            lastTrigger = now;
            using (call!)
                return await call!.ResponseAsync;
        }
        finally { triggerLock.Release(); }
    }

    private static bool ValidName(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    public void Dispose()
    {
        channel.Dispose();
        triggerLock.Dispose();
    }
}

internal sealed record MfdAction(string ActionName, string Category, bool Holdable, string? Field, bool Enabled);
internal sealed class MfdActionBusyException : Exception;
internal sealed class MfdActionForbiddenException : Exception;
