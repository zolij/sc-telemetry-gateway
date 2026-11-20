using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace ScTelemetryGateway.Core;

public sealed class ReceiverRejectedException(string message) : Exception(message);

public sealed class WebSocketDelivery(Uri endpoint, string apiKey, BatchQueue queue,
    TimeSpan interval, Action<string> status, Action acknowledged, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private const int MaxReplyBytes = 16 * 1024;
    private sealed record ServerMessage(string Type, int SchemaVersion, Guid? BatchId, string? Code, int? RetryAfterSeconds);
    private sealed class RetryRequestedException(TimeSpan delay) : Exception
    {
        public TimeSpan Delay { get; } = delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var retry = 1;
        long? lastSent = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            socket.Options.CollectHttpResponseDetails = true;
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxResponseHeadersLength = 32
            };
            using var invoker = new HttpMessageInvoker(handler);
            var wait = TimeSpan.FromSeconds(retry + Random.Shared.NextDouble());
            try
            {
                status("Connecting to receiver");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(endpoint, invoker, connect.Token);
                }
                status("Receiver connected");
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 500)), clock, cancellationToken);
                while (socket.State == WebSocketState.Open)
                {
                    var batch = queue.Reserve();
                    if (batch is null)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(200), clock, cancellationToken);
                        continue;
                    }
                    if (lastSent is { } previous)
                    {
                        var pace = interval - clock.GetElapsedTime(previous);
                        if (pace > TimeSpan.Zero) await DelayAtLeast(pace, cancellationToken);
                    }
                    var sentAt = clock.GetTimestamp();
                    lastSent = sentAt;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    await socket.SendAsync(batch.Payload.AsMemory(), WebSocketMessageType.Text, true, timeout.Token);
                    await ReceiveAcknowledgement(socket, batch.Id, timeout.Token);
                    queue.Acknowledge(batch.Id);
                    acknowledged();
                    retry = 1;
                    status("Receiver connected; batch acknowledged");
                    var remaining = interval - clock.GetElapsedTime(sentAt);
                    if (remaining > TimeSpan.Zero)
                        await DelayAtLeast(remaining, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (ReceiverRejectedException) { throw; }
            catch (RetryRequestedException ex)
            {
                wait = ex.Delay > wait ? ex.Delay : wait;
                status("Receiver requested backoff");
            }
            catch (WebSocketException)
            {
                var code = (int)socket.HttpStatusCode;
                if (code is >= 300 and < 500 && code is not 408 and not 429)
                    throw new ReceiverRejectedException($"Receiver handshake rejected (HTTP {code}). Check credentials, endpoint and proxy policy.");
                if (socket.HttpStatusCode == HttpStatusCode.TooManyRequests)
                    wait = RetryAfter(socket.HttpResponseHeaders, clock.GetUtcNow(), wait);
                status(code > 0 ? $"Receiver unavailable (HTTP {code}); reconnecting" : "Receiver disconnected; reconnecting");
            }
            catch (HttpRequestException)
            {
                status("Receiver TLS or network error; reconnecting");
            }
            catch (OperationCanceledException)
            {
                status("Receiver connect/send/ACK timeout; reconnecting");
            }
            socket.Abort();
            await DelayAtLeast(wait, cancellationToken);
            retry = Math.Min(retry * 2, 60);
        }
    }

    private async Task DelayAtLeast(TimeSpan delay, CancellationToken token)
    {
        var started = clock.GetTimestamp();
        while (delay - clock.GetElapsedTime(started) is var remaining && remaining > TimeSpan.Zero)
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining, clock, token);
    }

    private static TimeSpan RetryAfter(IReadOnlyDictionary<string, IEnumerable<string>>? headers,
        DateTimeOffset now, TimeSpan fallback)
    {
        var raw = headers?.FirstOrDefault(h => h.Key.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        if (long.TryParse(raw, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 86400));
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date > now)
            return date - now > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : date - now;
        return fallback;
    }

    private static async Task ReceiveAcknowledgement(ClientWebSocket socket, Guid expected, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(chunk.AsMemory(), token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if ((int?)socket.CloseStatus is 1002 or 1008 or 1003 or 1007 or 1009)
                    throw new ReceiverRejectedException("Receiver closed the connection for a policy or protocol error.");
                throw new WebSocketException("Receiver closed before acknowledgement.");
            }
            if (result.MessageType != WebSocketMessageType.Text || buffer.Length + result.Count > MaxReplyBytes)
                throw new ReceiverRejectedException("Receiver sent an invalid or oversized acknowledgement.");
            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        ServerMessage message;
        try
        {
            message = JsonSerializer.Deserialize<ServerMessage>(buffer.ToArray(), WireJson.Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ReceiverRejectedException("Receiver sent an invalid acknowledgement schema.");
        }
        if (message.SchemaVersion != 1)
            throw new ReceiverRejectedException("Receiver protocol version is unsupported.");
        if (message.Type == "telemetry.error")
        {
            if (message.Code == "rate_limited" && message.RetryAfterSeconds is >= 1 and <= 86400)
                throw new RetryRequestedException(TimeSpan.FromSeconds(message.RetryAfterSeconds.Value));
            throw new ReceiverRejectedException("Receiver rejected the batch; check protocol and authorization.");
        }
        if (message.Type != "telemetry.ack" || message.BatchId != expected ||
            message.Code is not null || message.RetryAfterSeconds is not null)
            throw new ReceiverRejectedException("Receiver acknowledged an unexpected batch.");
    }
}
