using Grpc.Core;
using Sc.Game.Telemetry.V1;

namespace VerseDataGateway.TelemetryMock;

public sealed class MockTelemetryService(MockState state) : GameTelemetryService.GameTelemetryServiceBase
{
    public override async Task TelemetryStream(TelemetryStreamRequest request,
        IServerStreamWriter<TelemetryStreamResponse> responseStream, ServerCallContext context)
    {
        using var subscription = state.Subscribe();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(52));
        try
        {
            do
            {
                var frame = state.FrameWithEvents(subscription);
                if (subscription.Events.Completion.IsCompleted)
                    await subscription.Events.Completion;
                await responseStream.WriteAsync(frame, context.CancellationToken);
            } while (await timer.WaitForNextTickAsync(context.CancellationToken));
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
    }

    public override Task<ListTriggerableActionsResponse> ListTriggerableActions(
        ListTriggerableActionsRequest request, ServerCallContext context) => Task.FromResult(ActionCatalog.List());

    public override Task<TriggerGameActionResponse> TriggerGameAction(
        TriggerGameActionRequest request, ServerCallContext context) =>
        Task.FromResult(state.Trigger(request));
}
