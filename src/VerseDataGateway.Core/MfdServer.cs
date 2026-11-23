using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace VerseDataGateway.Core;

public sealed record MfdApprovedDevice(Guid Id, string Label, DateTimeOffset ApprovedAt);
public sealed record MfdServerStatus(bool Listening, string? Url, string? Error,
    IReadOnlyList<MfdApprovedDevice> Devices);

public sealed partial class MfdServer : IAsyncDisposable
{
    public const int PairingCodeLifetimeSeconds = 120;
    private const int MaxRequestBytes = 4096;
    private const int MaxAuthBytes = 512;
    private const int UnauthenticatedTimeoutSeconds = 10;

    private readonly IPAddress address;
    private readonly int port;
    private readonly string assetRoot;
    private readonly MfdSnapshotStore snapshot;
    private readonly MfdProfileStore profiles;
    private readonly MfdDeviceStore deviceStore;
    private readonly string host;
    private readonly string origin;
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, MfdStoredDevice> approved = new();
    private readonly ConcurrentDictionary<WebSocket, MfdWebSocketState> sockets = new();
    private readonly CryptoRandomPairCode pairCode = new();
    private readonly CancellationTokenSource shutdown = new();
    private int sessionCount;
    private bool stopping;
    private ILogger<MfdServer>? logger;
    private WebApplication? app;
    private string? lastError;
    private CancellationTokenSource? publisher;
    private Task? publisherTask;

    public MfdServer(IPAddress address, int port, MfdSnapshotStore snapshot, string assetRoot,
        MfdProfileStore? profiles = null, MfdDeviceStore? deviceStore = null)
    {
        this.address = address;
        this.port = port;
        this.snapshot = snapshot;
        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VerseDataGateway");
        this.profiles = profiles ?? new MfdProfileStore(dataDirectory);
        this.deviceStore = deviceStore ?? new MfdDeviceStore(dataDirectory);
        foreach (var device in this.deviceStore.List())
            approved[device.Id] = device;
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
                var devices = approved.Values.Select(d => new MfdApprovedDevice(d.Id, d.Label, d.ApprovedAt)).ToArray();
                return new(app is not null, app is null ? null : origin + "/", lastError, devices);
            }
        }
    }

    public string CreatePairingCode()
    {
        lock (gate)
        {
            return pairCode.Create();
        }
    }

    public string CreatePairingUrl(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        return origin + "/#pair=" + Uri.EscapeDataString(code);
    }

    public bool RevokeDevice(Guid deviceId)
    {
        lock (gate)
        {
            if (!approved.ContainsKey(deviceId)) return false;
            if (!deviceStore.Remove(deviceId))
                throw new InvalidDataException("Approved MFD device was missing from the device store.");
            approved.TryRemove(deviceId, out _);
            foreach (var item in sockets.Where(s => s.Value.DeviceId == deviceId))
                item.Key.Abort();
            return true;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            Args = [],
            EnvironmentName = "Production"
        });
        builder.WebHost.UseKestrelCore();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));
        builder.Services.AddRouting();
        builder.Services.AddSingleton(this);
        app = builder.Build();
        logger = app.Services.GetRequiredService<ILogger<MfdServer>>();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapMethods("/mfd", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapMethods("/index.html", [HttpMethods.Get, HttpMethods.Head], ServeIndex);
        app.MapGet("/assets/{*path}", ServeAsset);
        app.MapMethods("/api/pair/request", [HttpMethods.Post], PairRequestAsync);
        app.MapMethods("/mfd/ws", [HttpMethods.Get], WebSocketAsync);
        MapProfileApi(app);
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
                lock (gate)
                {
                    if (item.Value.DeviceId is not { } id || !approved.ContainsKey(id)) continue;
                    item.Value.Snapshots.Writer.TryWrite(json);
                }
            }
        }
    }

    private static async Task SendSnapshotsAsync(WebSocket socket, MfdWebSocketState state,
        byte[] initial, CancellationToken cancellationToken)
    {
        async Task Send(byte[] json)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await socket.SendAsync(json, WebSocketMessageType.Text, true, deadline.Token);
        }
        await Send(initial);
        await foreach (var json in state.Snapshots.Reader.ReadAllAsync(cancellationToken))
            await Send(json);
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, context.RequestAborted);
        try
        {
            var body = new byte[MaxRequestBytes + 1];
            var count = 0;
            while (count < body.Length)
            {
                var read = await context.Request.Body.ReadAsync(body.AsMemory(count), lifetime.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxRequestBytes)
            {
                context.Response.StatusCode = 413;
                return;
            }
            using var document = JsonDocument.Parse(body.AsMemory(0, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "mfd.pair.request" ||
                !root.TryGetProperty("code", out var codeElement) || codeElement.ValueKind != JsonValueKind.String)
                throw new JsonException();
            var code = codeElement.GetString();
            var label = root.TryGetProperty("device_label", out var labelElement) &&
                labelElement.ValueKind == JsonValueKind.String ? labelElement.GetString()?.Trim() : "Display";
            if (string.IsNullOrEmpty(code) || label is null || label.Length is < 1 or > 48)
                throw new JsonException();
            string? token = null;
            lock (gate)
            {
                if (stopping)
                {
                    context.Response.StatusCode = 503;
                    return;
                }
                if (!pairCode.TryConsume(code))
                {
                    context.Response.StatusCode = 401;
                }
                else
                {
                    token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                    var deviceId = Guid.NewGuid();
                    var device = new MfdStoredDevice(deviceId, label, MfdDeviceStore.HashToken(token), DateTimeOffset.UtcNow);
                    var replacedIds = deviceStore.Add(device);
                    foreach (var replacedId in replacedIds)
                    {
                        approved.TryRemove(replacedId, out _);
                        foreach (var item in sockets.Where(s => s.Value.DeviceId == replacedId))
                            item.Key.Abort();
                    }
                    approved[deviceId] = device;
                }
            }
            if (token is null)
            {
                await context.Response.WriteAsJsonAsync(new { error = "pairing_code_invalid" }, lifetime.Token);
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
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            context.Response.StatusCode = 503;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogError("MFD device permission could not be saved: {FailureType}", ex.GetType().Name);
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "permission_store_unavailable" });
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
        lock (gate)
        {
            if (stopping || sessionCount >= MfdSnapshotStore.MaxClients)
            {
                context.Response.StatusCode = stopping ? 503 : 429;
                return;
            }
            sessionCount++;
        }
        WebSocket? socket = null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, context.RequestAborted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(UnauthenticatedTimeoutSeconds));
        Task? sender = null;
        Task? receiver = null;
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            var state = new MfdWebSocketState();
            lock (gate)
            {
                if (stopping) throw new OperationCanceledException(shutdown.Token);
                sockets[socket] = state;
            }
            var buffer = new byte[MaxAuthBytes + 1];
            var count = 0;
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), timeout.Token);
                count += received.Count;
                if (received.MessageType != WebSocketMessageType.Text || count > MaxAuthBytes)
                    throw new WebSocketException("Invalid authentication message.");
            } while (!received.EndOfMessage);
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "mfd.auth" ||
                !root.TryGetProperty("schema_version", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1 ||
                !root.TryGetProperty("session_token", out var tokenElement) || tokenElement.ValueKind != JsonValueKind.String)
                throw new WebSocketException("Invalid authentication schema.");
            var token = Encoding.UTF8.GetBytes(tokenElement.GetString() ?? "");
            lock (gate)
            {
                var device = approved.Values.FirstOrDefault(d => MfdDeviceStore.TokenMatches(
                    Encoding.UTF8.GetString(token), d.TokenHash));
                if (stopping || device is null)
                    throw new WebSocketException("Not approved.");
                state.DeviceId = device.Id;
            }
            var initial = JsonSerializer.SerializeToUtf8Bytes(snapshot.Current(), WireJson.Options);
            sender = SendSnapshotsAsync(socket, state, initial, lifetime.Token);
            receiver = ReceiveUntilClosedAsync(socket, lifetime.Token);
            await await Task.WhenAny(sender, receiver);
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or OperationCanceledException or IOException)
        {
            if (!lifetime.IsCancellationRequested)
                logger?.LogWarning("MFD connection ended: {FailureType}", ex.GetType().Name);
            if (sender is null && socket?.State is (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "authentication_failed", timeout.Token);
                }
                catch (Exception closeError) when (closeError is WebSocketException or OperationCanceledException or IOException)
                {
                    logger?.LogDebug("MFD authentication close: {FailureType}", closeError.GetType().Name);
                    socket.Abort();
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            socket?.Abort();
            try
            {
                await Task.WhenAll(new[] { sender, receiver }.OfType<Task>());
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
            {
                logger?.LogDebug("MFD connection cleanup: {FailureType}", ex.GetType().Name);
            }
            finally
            {
                lock (gate)
                {
                    if (socket is not null) sockets.TryRemove(socket, out _);
                    sessionCount--;
                }
                socket?.Dispose();
            }
        }
    }

    private static async Task ReceiveUntilClosedAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[128];
        while (socket.State == WebSocketState.Open)
            if ((await socket.ReceiveAsync(buffer, cancellationToken)).MessageType == WebSocketMessageType.Close)
                return;
    }

    private bool OriginAllowed(HttpContext context)
    {
        if (!string.Equals(context.Request.Headers.Host.ToString(), host, StringComparison.OrdinalIgnoreCase))
            return false;
        var originHeader = context.Request.Headers.Origin.FirstOrDefault();
        return originHeader is null || originHeader.Equals(origin, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            stopping = true;
            pairCode.Revoke();
            approved.Clear();
            foreach (var socket in sockets.Keys) socket.Abort();
        }
        publisher?.Cancel();
        if (publisherTask is not null)
        {
            try { await publisherTask; } catch (OperationCanceledException) { }
        }
        if (app is not null) await app.StopAsync();
        if (app is not null) await app.DisposeAsync();
        await shutdown.CancelAsync();
        publisher?.Dispose();
        shutdown.Dispose();
    }

    private static string GetMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".png" => "image/png",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };

    private sealed class MfdWebSocketState
    {
        public Guid? DeviceId { get; set; }
        public Channel<byte[]> Snapshots { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1)
        {
            SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest
        });
    }
    private sealed class CryptoRandomPairCode
    {
        private readonly object gate = new();
        private string? active;
        private DateTimeOffset expires;
        private int attempts;

        public string Create()
        {
            lock (gate)
            {
                Revoke();
                var number = RandomNumberGenerator.GetInt32(1_000_000);
                active = number.ToString("D6", CultureInfo.InvariantCulture);
                expires = DateTimeOffset.UtcNow.AddSeconds(PairingCodeLifetimeSeconds);
                attempts = 0;
                return active;
            }
        }

        public bool TryConsume(string code)
        {
            lock (gate)
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
        }

        public void Revoke() { lock (gate) { active = null; expires = default; attempts = 0; } }
    }
}
