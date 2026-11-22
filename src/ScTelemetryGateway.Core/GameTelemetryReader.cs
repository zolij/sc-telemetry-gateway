using Grpc.Core;
using Grpc.Net.Client;
using Google.Protobuf;
using Sc.Game.Telemetry.V1;

namespace ScTelemetryGateway.Core;

public sealed class GameTelemetryReader(int port)
{
    private sealed record ReceivedFrame(TelemetryStreamResponse Message, byte[] Payload);

    private static readonly Method<TelemetryStreamRequest, ReceivedFrame> StreamMethod = new(
        MethodType.ServerStreaming, TelemetryStreamResponse.Descriptor.File.Services[0].FullName, "TelemetryStream",
        Marshallers.Create(request => request.ToByteArray(), payload => TelemetryStreamRequest.Parser.ParseFrom(payload)),
        Marshallers.Create(frame => frame.Payload,
            payload => new ReceivedFrame(TelemetryStreamResponse.Parser.ParseFrom(payload), payload)));

    public async Task RunAsync(TelemetryAggregator aggregator, Action<string> status,
        CancellationToken cancellationToken, Action? flushDisconnected = null)
        => await RunAsync([aggregator.Ingest],
            status, cancellationToken, () =>
            {
                aggregator.Disconnected();
                flushDisconnected?.Invoke();
            });

    public async Task RunAsync(IReadOnlyList<Action<Sc.Game.Telemetry.V1.TelemetryStreamResponse>> sinks,
        Action<string> status, CancellationToken cancellationToken, Action? disconnected = null,
        Action? streamStarted = null,
        Action<TelemetryStreamResponse, ReadOnlyMemory<byte>>? rawSink = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var delay = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
                    new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024 });
                using var call = channel.CreateCallInvoker().AsyncServerStreamingCall(StreamMethod, null,
                    new CallOptions(cancellationToken: cancellationToken), new TelemetryStreamRequest());
                streamStarted?.Invoke();
                foreach (var sink in sinks)
                    if (sink.Target is TelemetryAggregator target) target.StartStream();
                status("Waiting for game telemetry");
                await foreach (var frame in call.ResponseStream.ReadAllAsync(cancellationToken))
                {
                    foreach (var sink in sinks) sink(frame.Message);
                    rawSink?.Invoke(frame.Message, frame.Payload);
                    status("Game connected");
                    delay = 1;
                }
                status("Game stream ended; reconnecting");
            }
            catch (RpcException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (RpcException ex)
            {
                status($"Game unavailable ({ex.StatusCode}); reconnecting");
            }
            disconnected?.Invoke();
            await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble()), cancellationToken);
            delay = Math.Min(delay * 2, 30);
        }
    }
}
