using Grpc.Core;
using Grpc.Net.Client;
using Sc.Game.Telemetry.V1;

namespace ScTelemetryGateway.Core;

public sealed class GameTelemetryReader(int port)
{
    public async Task RunAsync(TelemetryAggregator aggregator, Action<string> status,
        CancellationToken cancellationToken, Action? flushDisconnected = null)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var delay = 1;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}",
                    new GrpcChannelOptions { MaxReceiveMessageSize = 1024 * 1024 });
                var client = new GameTelemetryService.GameTelemetryServiceClient(channel);
                using var call = client.TelemetryStream(new(), cancellationToken: cancellationToken);
                aggregator.StartStream();
                status("Waiting for game telemetry");
                await foreach (var frame in call.ResponseStream.ReadAllAsync(cancellationToken))
                {
                    aggregator.Ingest(frame);
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
            aggregator.Disconnected();
            flushDisconnected?.Invoke();
            await Task.Delay(TimeSpan.FromSeconds(delay + Random.Shared.NextDouble()), cancellationToken);
            delay = Math.Min(delay * 2, 30);
        }
    }
}
