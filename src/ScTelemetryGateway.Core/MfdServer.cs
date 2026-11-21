using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ScTelemetryGateway.Core;

public sealed record MfdPairRequest(Guid Id, string Label, DateTimeOffset ExpiresAt);
public sealed record MfdApprovedDevice(Guid Id, string Label, DateTimeOffset ApprovedAt);
public sealed record MfdServerStatus(bool Listening, string? Url, string? Error,
    IReadOnlyList<MfdPairRequest> PendingRequests, IReadOnlyList<MfdApprovedDevice> Devices);

public sealed class MfdServer : IAsyncDisposable
{
    public const int PairingCodeLifetimeSeconds = 120;
    private const int MaxPairRequests = 16;
    private const int MaxRequestBytes = 4096;
    private const int MaxAuthBytes = 512;
    private const int UnauthenticatedTimeoutSeconds = 10;

    private readonly IPAddress address;
    private readonly int port;
    private readonly string assetRoot;
    private readonly MfdSnapshotStore snapshot;
    private readonly string host;
    private readonly string origin;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, ApprovedDevice> approved = new();
    private readonly ConcurrentDictionary<Guid, PairRequest> pending = new();
    private readonly ConcurrentDictionary<WebSocket, MfdWebSocketState> sockets = new();
    private readonly object sendGate = new();
    private readonly CryptoRandomPairCode pairCode = new();
    private WebApplication? app;
    private string? lastError;
    private CancellationTokenSource? publisher;
    private Task? publisherTask;

    public MfdServer(IPAddress address, int port, MfdSnapshotStore snapshot, string assetRoot)
    {
        this.address = address;
        this.port = port;
        this.snapshot = snapshot;
        this.assetRoot = assetRoot;
        host = address.ToString() + ":" + port.ToString(CultureInfo.InvariantCulture);
        origin = "http://" + host;
    }

    public MfdServerStatus Status
    {
        get
        {
            lock (gate)
            {
                var now = DateTimeOffset.UtcNow;
                var stale = pending.Values.Where(p => p.ExpiresAt > now).Select(p => new MfdPairRequest(p.Id, p.Label, p.ExpiresAt)).ToArray();
                var devices = approved.Values.Select(d => new MfdApprovedDevice(d.Id, d.Label, d.ApprovedAt)).ToArray();
                return new(app is not null, app is null ? null : origin + "/", lastError, stale, devices);
            }
        }
    }

    public string CreatePairingCode()
    {
        lock (gate)
        {
            CleanupExpired();
            return pairCode.Create();
        }
    }

    public bool ApproveRequest(Guid requestId)
    {
        lock (gate)
        {
            if (!pending.TryRemove(requestId, out var request) || request.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;
            var deviceId = Guid.NewGuid();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            approved[deviceId] = new ApprovedDevice(deviceId, request.Label, token, DateTimeOffset.UtcNow);
            request.Completion.TrySetResult(token);
            return true;
        }
    }

    public bool DenyRequest(Guid requestId)
    {
        lock (gate)
        {
            if (!pending.TryRemove(requestId, out var request))
                return false;
            request.Completion.TrySetResult(null);
            return true;
        }
    }

    public bool RevokeDevice(Guid deviceId)
    {
        if (!approved.TryRemove(deviceId, out _))
            return false;
        foreach (var item in sockets.Where(s => s.Value.DeviceId == deviceId))
        {
            sockets.TryRemove(item.Key, out _);
            _ = item.Key.CloseAsync(WebSocketCloseStatus.NormalClosure, "revoked", CancellationToken.None);
        }
        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Production"
        });
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));
        builder.Services.AddSingleton(this);
        app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapMethods("/mfd", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapMethods("/index.html", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapGet("/assets/{*path}", ServeAsset);
        app.MapMethods("/api/pair/request", [HttpMethods.Post], PairRequestAsync);
        app.MapMethods("/mfd/ws", [HttpMethods.Get], WebSocketAsync);
        app.MapMethods("/{*path}", [HttpMethods.Options], (HttpContext context) =>
        {
            context.Response.StatusCode = 405;
            return Task.CompletedTask;
        });
        app.MapFallback(async context =>
        {
            context.Response.StatusCode = 404;
            await context.Response.CompleteAsync();
        });
        try
        {
            await app.StartAsync(cancellationToken);
            publisher = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publisherTask = PublishLoop(publisher.Token);
            lastError = null;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            throw;
        }
    }

    private async Task PublishLoop(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(snapshot.Current(), WireJson.Options);
            if (json.Length > MfdSnapshotStore.MaxMessageBytes)
                continue;
            foreach (var item in sockets)
            {
                if (item.Value.DeviceId is null) continue;
                _ = SendLatestAsync(item.Key, json, cancellationToken);
            }
        }
    }

    private async Task SendLatestAsync(WebSocket socket, byte[] json, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open) return;
        lock (sendGate)
        {
            if (socket.State != WebSocketState.Open) return;
            try
            {
                socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken).GetAwaiter().GetResult();
            }
            catch
            {
                sockets.TryRemove(socket, out _);
            }
        }
        await Task.CompletedTask;
    }

    private async Task ServeIndex(HttpContext context)
    {
        if (!OriginAllowed(context) && context.Request.Headers.Origin.Count > 0)
        {
            context.Response.StatusCode = 403;
            return;
        }
        var path = Path.Combine(assetRoot, "index.html");
        if (!File.Exists(path))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = "text/html";
        await context.Response.SendFileAsync(path);
    }

    private async Task ServeAsset(HttpContext context)
    {
        if (!OriginAllowed(context) && context.Request.Headers.Origin.Count > 0)
        {
            context.Response.StatusCode = 403;
            return;
        }
        var relative = context.Request.RouteValues["path"]?.ToString() ?? "";
        var full = Path.GetFullPath(Path.Combine(assetRoot, "assets", relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(Path.Combine(assetRoot, "assets"));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
        {
            context.Response.StatusCode = 404;
            return;
        }
        context.Response.ContentType = GetMime(full);
        await context.Response.SendFileAsync(full);
    }

    private async Task PairRequestAsync(HttpContext context)
    {
        if (!OriginAllowed(context) || context.Request.Headers.Origin.Count == 0 ||
            context.Request.Headers.Host.Count == 0)
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (context.Request.Headers.ContentLength is > MaxRequestBytes)
        {
            context.Response.StatusCode = 413;
            return;
        }
        string json;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, false, leaveOpen: false))
            json = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(json) > MaxRequestBytes)
        {
            context.Response.StatusCode = 413;
            return;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 1 ||
                root.GetProperty("type").GetString() != "mfd.pair.request")
                throw new JsonException();
            var code = root.GetProperty("code").GetString();
            var label = root.GetProperty("device_label").GetString()?.Trim();
            if (string.IsNullOrEmpty(code) || label is null || label.Length is < 1 or > 48)
                throw new JsonException();
            if (!pairCode.TryConsume(code))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "pairing_code_invalid" });
                return;
            }
            lock (gate)
            {
                CleanupExpired();
                if (pending.Count >= MaxPairRequests)
                {
                    context.Response.StatusCode = 429;
                    return;
                }
            }
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new PairRequest(Guid.NewGuid(), label, completion, DateTimeOffset.UtcNow.AddMinutes(2));
            pending[request.Id] = request;
            var token = await completion.Task.WaitAsync(TimeSpan.FromMinutes(2));
            if (token is null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "pairing_denied" });
                return;
            }
            await context.Response.WriteAsJsonAsync(new
            {
                type = "mfd.pair.approved",
                schema_version = 1,
                session_token = token
            });
        }
        catch (JsonException)
        {
            context.Response.StatusCode = 400;
        }
        catch (TimeoutException)
        {
            context.Response.StatusCode = 408;
        }
    }

    private async Task WebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !OriginAllowed(context) ||
            context.Request.Headers.Origin.Count == 0)
        {
            context.Response.StatusCode = 403;
            return;
        }
        if (sockets.Count >= MfdSnapshotStore.MaxClients)
        {
            context.Response.StatusCode = 429;
            return;
        }
        var socket = await context.WebSockets.AcceptWebSocketAsync();
        var state = new MfdWebSocketState();
        sockets[socket] = state;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(UnauthenticatedTimeoutSeconds));
        try
        {
            var buffer = new byte[MaxAuthBytes];
            var received = await socket.ReceiveAsync(buffer, timeout.Token);
            if (received.MessageType != WebSocketMessageType.Text || !received.EndOfMessage)
                throw new WebSocketException("Invalid authentication message.");
            using var document = JsonDocument.Parse(buffer.AsMemory(0, received.Count));
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() != "mfd.auth" ||
                root.GetProperty("schema_version").GetInt32() != 1)
                throw new WebSocketException("Invalid authentication schema.");
            var token = root.GetProperty("session_token").GetString();
            var device = approved.Values.FirstOrDefault(d =>
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(d.Token), Encoding.UTF8.GetBytes(token ?? "")));
            if (device is null)
                throw new WebSocketException("Not approved.");
            state.DeviceId = device.Id;
            var initial = JsonSerializer.SerializeToUtf8Bytes(snapshot.Current(), WireJson.Options);
            await socket.SendAsync(initial, WebSocketMessageType.Text, true, CancellationToken.None);
            var readBuffer = new byte[128];
            while (socket.State == WebSocketState.Open)
                await socket.ReceiveAsync(readBuffer, CancellationToken.None);
        }
        catch
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication_failed", CancellationToken.None);
        }
        finally
        {
            sockets.TryRemove(socket, out _);
            socket.Dispose();
        }
    }

    private bool OriginAllowed(HttpContext context)
    {
        if (!string.Equals(context.Request.Headers.Host.ToString(), host, StringComparison.OrdinalIgnoreCase))
            return false;
        var originHeader = context.Request.Headers.Origin.FirstOrDefault();
        return originHeader is null || originHeader.Equals(origin, StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupExpired()
    {
        foreach (var item in pending.Where(p => p.Value.ExpiresAt <= DateTimeOffset.UtcNow))
        {
            if (pending.TryRemove(item.Key, out var request))
                request.Completion.TrySetResult(null);
        }
    }

    public async ValueTask DisposeAsync()
    {
        pairCode.Revoke();
        foreach (var device in approved.Keys.ToArray()) RevokeDevice(device);
        approved.Clear();
        pending.Clear();
        publisher?.Cancel();
        if (publisherTask is not null)
        {
            try { await publisherTask; } catch (OperationCanceledException) { }
        }
        if (app is not null) await app.StopAsync();
        app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        publisher?.Dispose();
    }

    private static string GetMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private sealed record PairRequest(Guid Id, string Label,
        TaskCompletionSource<string?> Completion, DateTimeOffset ExpiresAt);
    private sealed record ApprovedDevice(Guid Id, string Label, string Token, DateTimeOffset ApprovedAt);
    private sealed class MfdWebSocketState { public Guid? DeviceId { get; set; } }
    private sealed class CryptoRandomPairCode
    {
        private string? active;
        private DateTimeOffset expires;
        private int attempts;

        public string Create()
        {
            Revoke();
            var number = RandomNumberGenerator.GetInt32(1_000_000);
            active = number.ToString("D6", CultureInfo.InvariantCulture);
            expires = DateTimeOffset.UtcNow.AddSeconds(PairingCodeLifetimeSeconds);
            attempts = 0;
            return active;
        }

        public bool TryConsume(string code)
        {
            if (active is null || expires <= DateTimeOffset.UtcNow) return false;
            attempts++;
            if (attempts > 5) { Revoke(); return false; }
            if (code.Length != 6 || !code.All(char.IsAsciiDigit))
                return false;
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(code), Encoding.ASCII.GetBytes(active)))
                return false;
            Revoke();
            return true;
        }

        public void Revoke() { active = null; expires = default; attempts = 0; }
    }
}
