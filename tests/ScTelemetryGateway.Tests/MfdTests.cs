using System.Text.Json;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Net.Http.Json;
using System.Text;
using Sc.Game.Telemetry.V1;
using ScTelemetryGateway.Core;
using Google.Protobuf;

namespace ScTelemetryGateway.Tests;

public sealed class MfdTests
{
    [Fact]
    public async Task ChunkedPairingBodyIsRejectedBeforeTheRequestFinishes()
    {
        await using var test = await MfdTestServer.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, test.Port, test.Token);
        var stream = client.GetStream();
        var data = $"POST /api/pair/request HTTP/1.1\r\nHost: 127.0.0.1:{test.Port}\r\nOrigin: {test.Origin}\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n1388\r\n" + new string('x', 5000) + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(data), test.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var reader = new StreamReader(stream);
        Assert.Contains("413", await reader.ReadLineAsync(deadline.Token));
    }

    [Fact]
    public async Task OnePairingCodeImmediatelyApprovesOnlyOneConcurrentRequest()
    {
        await using var test = await MfdTestServer.Start();
        var code = test.Server.CreatePairingCode();
        var requests = Enumerable.Range(0, 20).Select(i => test.RequestPair(code, $"client-{i}")).ToArray();
        var responses = await Task.WhenAll(requests).WaitAsync(test.Token);
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
            Assert.Single(test.Server.Status.Devices);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task PairingUrlCarriesTheOneUseCodeInItsFragment()
    {
        await using var test = await MfdTestServer.Start();
        var code = test.Server.CreatePairingCode();
        Assert.Equal(test.Origin + "/#pair=" + code, test.Server.CreatePairingUrl(code));
    }

    [Fact]
    public async Task ApprovedDeviceSurvivesRestartAndRevocationIsPersistent()
    {
        var root = Path.Combine(Path.GetTempPath(), "sctg-mfd-device-" + Guid.NewGuid().ToString("N"));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var origin = $"http://127.0.0.1:{port}";
        var snapshot = new MfdSnapshotStore([]);
        MfdServer? server = null;
        try
        {
            MfdServer NewServer() => new(IPAddress.Loopback, port, snapshot, Path.GetTempPath(),
                new MfdProfileStore(root), new MfdDeviceStore(root));
            server = NewServer();
            await server.StartAsync(deadline.Token);
            using var http = new HttpClient { BaseAddress = new Uri(origin) };
            http.DefaultRequestHeaders.Add("Origin", origin);
            using var paired = await http.PostAsJsonAsync("/api/pair/request", new
            {
                schema_version = 1, type = "mfd.pair.request",
                code = server.CreatePairingCode(), device_label = "Persistent display"
            }, deadline.Token);
            paired.EnsureSuccessStatusCode();
            using var pairedBody = JsonDocument.Parse(await paired.Content.ReadAsStringAsync(deadline.Token));
            var token = pairedBody.RootElement.GetProperty("session_token").GetString()!;
            var deviceId = Assert.Single(server.Status.Devices).Id;
            var saved = await File.ReadAllTextAsync(Path.Combine(root, "mfd-devices.json"), deadline.Token);
            Assert.DoesNotContain(token, saved);

            await server.DisposeAsync();
            server = null;
            server = NewServer();
            await server.StartAsync(deadline.Token);
            Assert.Equal(deviceId, Assert.Single(server.Status.Devices).Id);
            using (var socket = new ClientWebSocket())
            {
                socket.Options.SetRequestHeader("Origin", origin);
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/mfd/ws"), deadline.Token);
                await Authenticate(socket, token, deadline.Token);
                using var restored = JsonDocument.Parse(await ReadSnapshot(socket, deadline.Token));
                Assert.Equal("mfd.snapshot", restored.RootElement.GetProperty("type").GetString());
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
            }

            Assert.True(server.RevokeDevice(deviceId));
            await server.DisposeAsync();
            server = null;
            server = NewServer();
            await server.StartAsync(deadline.Token);
            Assert.Empty(server.Status.Devices);
            using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/mfd/profiles");
            revokedRequest.Headers.Authorization = new("Bearer", token);
            using var revoked = await http.SendAsync(revokedRequest, deadline.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepairingSameBrowserReplacesPermissionAndDisconnectsOldSession()
    {
        await using var test = await MfdTestServer.Start();
        const string label = "Display 123e4567-e89b-42d3-a456-426614174000";
        async Task<string> Pair(string deviceLabel)
        {
            using var response = await test.RequestPair(test.Server.CreatePairingCode(), deviceLabel);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(test.Token));
            return body.RootElement.GetProperty("session_token").GetString()!;
        }

        var oldToken = await Pair(label);
        var otherToken = await Pair("Display 123e4567-e89b-42d3-a456-426614174001");
        using var socket = await test.Connect();
        await Authenticate(socket, oldToken, test.Token);
        using var snapshot = JsonDocument.Parse(await ReadSnapshot(socket, test.Token));
        var newToken = await Pair(label);

        Assert.Equal(2, test.Server.Status.Devices.Count);
        Assert.Single(test.Server.Status.Devices, device => device.Label == label);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await WaitForDisconnect(socket, deadline.Token);
        using var http = new HttpClient { BaseAddress = new Uri(test.Origin) };
        http.DefaultRequestHeaders.Add("Origin", test.Origin);
        foreach (var (token, expected) in new[]
        {
            (oldToken, HttpStatusCode.Unauthorized),
            (newToken, HttpStatusCode.OK),
            (otherToken, HttpStatusCode.OK)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mfd/profiles");
            request.Headers.Authorization = new("Bearer", token);
            using var response = await http.SendAsync(request, test.Token);
            Assert.Equal(expected, response.StatusCode);
        }
        Assert.Single(new MfdDeviceStore(test.ProfileRoot).List(), device => device.Label == label);
        Assert.True(test.Server.RevokeDevice(test.Server.Status.Devices.Single(device => device.Label == label).Id));
        using var revokedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/mfd/profiles");
        revokedRequest.Headers.Authorization = new("Bearer", newToken);
        using var revoked = await http.SendAsync(revokedRequest, test.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        Assert.Single(new MfdDeviceStore(test.ProfileRoot).List());
    }

    [Fact]
    public void ExistingBrowserDuplicatesKeepNewestPermissionAndOtherLabelsRemainSeparate()
    {
        var root = Path.Combine(Path.GetTempPath(), "sctg-mfd-device-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string label = "Display 123e4567-e89b-42d3-a456-426614174000";
        var newestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var document = new
        {
            SchemaVersion = 1,
            Devices = new[]
            {
                new { Id = newestId, Label = label, TokenHash = MfdDeviceStore.HashToken("new"), ApprovedAt = now },
                new { Id = Guid.NewGuid(), Label = label, TokenHash = MfdDeviceStore.HashToken("old"), ApprovedAt = now.AddMinutes(-1) },
                new { Id = Guid.NewGuid(), Label = "Display", TokenHash = MfdDeviceStore.HashToken("a"), ApprovedAt = now },
                new { Id = Guid.NewGuid(), Label = "Display", TokenHash = MfdDeviceStore.HashToken("b"), ApprovedAt = now }
            }
        };
        File.WriteAllText(Path.Combine(root, "mfd-devices.json"), JsonSerializer.Serialize(document, WireJson.Options));
        try
        {
            var devices = new MfdDeviceStore(root).List();
            Assert.Equal(3, devices.Length);
            var browser = Assert.Single(devices, device => device.Label == label);
            Assert.Equal(newestId, browser.Id);
            Assert.True(MfdDeviceStore.TokenMatches("new", browser.TokenHash));
            Assert.Equal(2, devices.Count(device => device.Label == "Display"));
            Assert.Equal(devices, new MfdDeviceStore(root).List());
            var store = new MfdDeviceStore(root);
            Assert.Empty(store.Add(new(Guid.NewGuid(), "Display", MfdDeviceStore.HashToken("c"), now)));
            Assert.Equal(3, store.List().Count(device => device.Label == "Display"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void CorruptDeviceStoreIsNotSilentlyReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "sctg-mfd-device-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "mfd-devices.json");
        const string corrupt = "{ invalid";
        File.WriteAllText(path, corrupt);
        try
        {
            Assert.Throws<JsonException>(() => new MfdDeviceStore(root).List());
            Assert.Equal(corrupt, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task FragmentedAuthenticationReceivesSnapshotsAndRevocationStopsAccess()
    {
        await using var test = await MfdTestServer.Start();
        var token = await test.Pair();
        using var client = await test.Connect();
        var auth = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "mfd.auth", schema_version = 1, session_token = token }));
        await client.SendAsync(auth.AsMemory(0, 13), WebSocketMessageType.Text, false, test.Token);
        await client.SendAsync(auth.AsMemory(13), WebSocketMessageType.Text, true, test.Token);
        using var initial = JsonDocument.Parse(await ReadSnapshot(client, test.Token));
        Assert.Equal("mfd.snapshot", initial.RootElement.GetProperty("type").GetString());
        using var periodic = JsonDocument.Parse(await ReadSnapshot(client, test.Token));
        Assert.Equal("mfd.snapshot", periodic.RootElement.GetProperty("type").GetString());
        Assert.True(test.Server.RevokeDevice(test.Server.Status.Devices.Single().Id));
        using var closed = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await WaitForDisconnect(client, closed.Token);
        using var revoked = await test.Connect();
        await revoked.SendAsync(auth, WebSocketMessageType.Text, true, test.Token);
        var buffer = new byte[1024];
        var reply = await revoked.ReceiveAsync(buffer, test.Token);
        Assert.Equal(WebSocketMessageType.Close, reply.MessageType);
    }

    [Fact]
    public async Task FragmentedAuthenticationRejectsCumulativeOverflow()
    {
        await using var test = await MfdTestServer.Start();
        using var client = await test.Connect();
        await client.SendAsync(new byte[400], WebSocketMessageType.Text, false, test.Token);
        await client.SendAsync(new byte[113], WebSocketMessageType.Text, true, test.Token);
        var reply = await client.ReceiveAsync(new byte[128], test.Token);
        Assert.Equal(WebSocketMessageType.Close, reply.MessageType);
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", test.Token);
    }

    [Fact]
    public async Task RevocationRacingAuthenticationClosesAllConnections()
    {
        await using var test = await MfdTestServer.Start();
        var token = await test.Pair();
        var clients = await Task.WhenAll(Enumerable.Range(0, MfdSnapshotStore.MaxClients).Select(_ => test.Connect()));
        try
        {
            var auth = clients.Select(client => Authenticate(client, token, test.Token)).ToArray();
            Assert.True(test.Server.RevokeDevice(test.Server.Status.Devices.Single().Id));
            await Task.WhenAll(auth);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            foreach (var client in clients)
                await WaitForDisconnect(client, deadline.Token);
            Assert.Empty(test.Server.Status.Devices);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentHandshakesReserveAtMostEightSlotsAndInvalidClientsReleaseThem()
    {
        await using var test = await MfdTestServer.Start();
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            try { return await test.Connect(); }
            catch (WebSocketException) { return null; }
        }));
        var clients = attempts.OfType<ClientWebSocket>().ToArray();
        try
        {
            Assert.Equal(MfdSnapshotStore.MaxClients, clients.Length);
            foreach (var client in clients)
            {
                await client.SendAsync(Encoding.UTF8.GetBytes("{}"), WebSocketMessageType.Text, true, test.Token);
                var result = await client.ReceiveAsync(new byte[128], test.Token);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            // Do not acknowledge the close frames: these peers must still release their slots.
            await Task.Delay(TimeSpan.FromSeconds(11), test.Token);
            using var fresh = await test.Connect();
            Assert.Equal(WebSocketState.Open, fresh.State);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [Fact]
    public async Task SlowDisplayDoesNotBlockOtherSnapshots()
    {
        await using var test = await MfdTestServer.Start(["vehicle.state.name"]);
        test.Store.Ingest(new() { Vehicle = new() { State = new() { Name = new string('x', 700_000) } } });
        var slowToken = await test.Pair();
        var fastToken = await test.Pair();
        using var slow = await test.Connect();
        using var fast = await test.Connect();
        await Authenticate(slow, slowToken, test.Token);
        await Authenticate(fast, fastToken, test.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        for (var i = 0; i < 20; i++)
        {
            using var frame = JsonDocument.Parse(await ReadSnapshot(fast, deadline.Token));
            Assert.Equal(700_000, frame.RootElement.GetProperty("fields").GetProperty("vehicle.state.name").GetProperty("value").GetString()!.Length);
        }
    }

    private static Task Authenticate(ClientWebSocket socket, string token, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new { type = "mfd.auth", schema_version = 1, session_token = token })),
            WebSocketMessageType.Text, true, cancellationToken);

    private static async Task WaitForDisconnect(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        try
        {
            var buffer = new byte[8192];
            while ((await socket.ReceiveAsync(buffer, cancellationToken)).MessageType != WebSocketMessageType.Close) { }
        }
        catch (WebSocketException) { }
    }

    private static async Task<string> ReadSnapshot(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("Connection closed before a snapshot.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.ToArray());
    }

    private sealed class MfdTestServer : IAsyncDisposable
    {
        private readonly string profileRoot = Path.Combine(Path.GetTempPath(), "sctg-mfd-api-" + Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        private readonly HttpClient http = new();
        private bool disposed;
        public int Port { get; }
        public string Origin => $"http://127.0.0.1:{Port}";
        public CancellationToken Token => deadline.Token;
        public MfdServer Server { get; }
        public MfdSnapshotStore Store { get; }
        public string ProfileRoot => profileRoot;

        private MfdTestServer(IEnumerable<string> fields)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            Store = new(fields);
            Server = new(IPAddress.Loopback, Port, Store, Path.GetTempPath(),
                new MfdProfileStore(profileRoot), new MfdDeviceStore(profileRoot));
            http.DefaultRequestHeaders.Add("Origin", Origin);
        }

        public static async Task<MfdTestServer> Start(IEnumerable<string>? fields = null)
        {
            var test = new MfdTestServer(fields ?? []);
            await test.Server.StartAsync(test.Token);
            return test;
        }

        public Task<HttpResponseMessage> RequestPair(string code, string label) =>
            http.PostAsJsonAsync(Origin + "/api/pair/request",
                new { schema_version = 1, type = "mfd.pair.request", code, device_label = label }, Token);

        public async Task<string> Pair()
        {
            using var response = await RequestPair(Server.CreatePairingCode(), "synthetic").WaitAsync(Token);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
            return body.RootElement.GetProperty("session_token").GetString()!;
        }

        public async Task<ClientWebSocket> Connect()
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", Origin);
            try
            {
                await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/mfd/ws"), Token);
                return socket;
            }
            catch { socket.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed) return;
            disposed = true;
            await Server.DisposeAsync();
            http.Dispose();
            deadline.Dispose();
            if (Directory.Exists(profileRoot)) Directory.Delete(profileRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileApiRequiresApprovalSharesProfilesAndRejectsConcurrentEdits()
    {
        await using var test = await MfdTestServer.Start(["actor.vitals.heart_rate"]);
        using var http = new HttpClient { BaseAddress = new Uri(test.Origin) };
        http.DefaultRequestHeaders.Add("Origin", test.Origin);
        using var denied = await http.GetAsync("/api/mfd/profiles", test.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var deniedEvents = await http.GetAsync("/api/mfd/events", test.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, deniedEvents.StatusCode);
        var firstToken = await test.Pair();
        var firstDevice = test.Server.Status.Devices.Single().Id;
        http.DefaultRequestHeaders.Authorization = new("Bearer", firstToken);
        using var catalog = await http.GetAsync("/api/mfd/fields", test.Token);
        var fields = await catalog.Content.ReadFromJsonAsync<MfdCatalogField[]>(WireJson.Options, test.Token);
        Assert.Equal("actor.vitals.heart_rate", Assert.Single(fields!).Path);
        using var eventCatalog = await http.GetAsync("/api/mfd/events", test.Token);
        var eventTypes = await eventCatalog.Content.ReadFromJsonAsync<string[]>(WireJson.Options, test.Token);
        Assert.Contains("GAME_EVENT_TYPE_ACTOR_ENTER_SEAT", eventTypes!);
        Assert.DoesNotContain("GAME_EVENT_TYPE_UNSPECIFIED", eventTypes!);
        using var created = await http.PostAsJsonAsync("/api/mfd/profiles", MfdProfileTests.Draft(), WireJson.Options, test.Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var profile = (await created.Content.ReadFromJsonAsync<MfdProfile>(WireJson.Options, test.Token))!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", await test.Pair());
        using var shared = await http.GetAsync("/api/mfd/profiles/" + profile.Id, test.Token);
        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        using var list = await http.GetAsync("/api/mfd/profiles", test.Token);
        var etag = list.Headers.ETag!;
        using var unchanged = new HttpRequestMessage(HttpMethod.Get, "/api/mfd/profiles");
        unchanged.Headers.IfNoneMatch.Add(etag);
        using var cached = await http.SendAsync(unchanged, test.Token);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
        async Task<HttpResponseMessage> Save()
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/mfd/profiles/" + profile.Id)
            {
                Content = JsonContent.Create(profile with { Name = "Shared update" }, options: WireJson.Options)
            };
            request.Headers.IfMatch.Add(new("\"1\""));
            return await http.SendAsync(request, test.Token);
        }
        var edits = await Task.WhenAll(Save(), Save());
        try
        {
            Assert.Single(edits, response => response.StatusCode == HttpStatusCode.OK);
            Assert.Single(edits, response => response.StatusCode == HttpStatusCode.PreconditionFailed);
        }
        finally { foreach (var response in edits) response.Dispose(); }
        using var missingRevision = await http.DeleteAsync("/api/mfd/profiles/" + profile.Id, test.Token);
        Assert.Equal((HttpStatusCode)428, missingRevision.StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", firstToken);
        Assert.True(test.Server.RevokeDevice(firstDevice));
        using var revoked = await http.GetAsync("/api/mfd/profiles", test.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Fact]
    public async Task ProfileApiRejectsInvalidBodiesAndForeignOrigins()
    {
        await using var test = await MfdTestServer.Start();
        using var http = new HttpClient { BaseAddress = new Uri(test.Origin) };
        http.DefaultRequestHeaders.Authorization = new("Bearer", await test.Pair());
        http.DefaultRequestHeaders.Add("Origin", "http://foreign.invalid");
        using var foreign = await http.GetAsync("/api/mfd/profiles", test.Token);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        http.DefaultRequestHeaders.Add("Origin", test.Origin);
        using var invalid = await http.PostAsync("/api/mfd/profiles", new StringContent("{broken"), test.Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missingSchema = await http.PostAsync("/api/mfd/profiles",
            new StringContent(JsonSerializer.Serialize(MfdProfileTests.Draft(), WireJson.Options).Replace("\"schema_version\":1,", "")),
            test.Token);
        Assert.Equal(HttpStatusCode.BadRequest, missingSchema.StatusCode);
        var validJson = JsonSerializer.Serialize(MfdProfileTests.Draft(), WireJson.Options);
        using var atLimit = await http.PostAsync("/api/mfd/profiles",
            new StringContent(validJson + new string(' ', MfdProfile.MaxRequestBytes - Encoding.UTF8.GetByteCount(validJson))), test.Token);
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
        using var oversized = await http.PostAsync("/api/mfd/profiles",
            new StringContent(new string('x', MfdProfile.MaxRequestBytes + 1)), test.Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
    }

    [Fact]
    public async Task ChunkedProfileOverflowIsRejectedWithoutWaitingForTheRemainingBody()
    {
        await using var test = await MfdTestServer.Start();
        var token = await test.Pair();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, test.Port, test.Token);
        var stream = client.GetStream();
        var length = MfdProfile.MaxRequestBytes + 1;
        var data = $"POST /api/mfd/profiles HTTP/1.1\r\nHost: 127.0.0.1:{test.Port}\r\nOrigin: {test.Origin}\r\nAuthorization: Bearer {token}\r\nTransfer-Encoding: chunked\r\nContent-Type: application/json\r\n\r\n{length:X}\r\n" + new string('x', length) + "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(data), test.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var reader = new StreamReader(stream);
        Assert.Contains("413", await reader.ReadLineAsync(deadline.Token));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("oversize")]
    [InlineData("profile")]
    public async Task InvalidProfileStoresReturnStructuredStorageErrorsWithoutChangingTheFile(string failure)
    {
        await using var test = await MfdTestServer.Start();
        var document = failure switch
        {
            "schema" => "{\"schema_version\":2,\"profiles\":[]}",
            "oversize" => new string(' ', MfdProfile.MaxStoreBytes + 1),
            "profile" => JsonSerializer.Serialize(new
            {
                SchemaVersion = 1,
                Profiles = new[] { MfdProfileTests.Draft() with { Id = Guid.NewGuid(), Revision = 1, Name = "" } }
            }, WireJson.Options),
            _ => throw new ArgumentException("Unknown storage failure.", nameof(failure))
        };
        Directory.CreateDirectory(test.ProfileRoot);
        var path = Path.Combine(test.ProfileRoot, "mfd-profiles.json");
        await File.WriteAllTextAsync(path, document, test.Token);
        using var http = new HttpClient { BaseAddress = new Uri(test.Origin) };
        http.DefaultRequestHeaders.Add("Origin", test.Origin);
        http.DefaultRequestHeaders.Authorization = new("Bearer", await test.Pair());
        using var response = await http.GetAsync("/api/mfd/profiles", test.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(test.Token));
        Assert.Equal("storage_failed", error.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("message").GetString()));
        Assert.Equal(document, await File.ReadAllTextAsync(path, test.Token));
    }

    [Fact]
    public async Task ServerStartsAndServesBundledIndexAndPngAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), "sctg-mfd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "synthetic MFD");
        var assets = Path.Combine(root, "assets");
        Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "app-icon.png"), "synthetic icon");
        await File.WriteAllTextAsync(Path.Combine(assets, "keep-awake.mp4"), "synthetic video");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        try
        {
            await using var server = new MfdServer(IPAddress.Loopback, port, new MfdSnapshotStore([]), root,
                new MfdProfileStore(root), new MfdDeviceStore(root));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await server.StartAsync(timeout.Token);
            using var client = new HttpClient();
            Assert.Equal("synthetic MFD", await client.GetStringAsync($"http://127.0.0.1:{port}/", timeout.Token));
            using var icon = await client.GetAsync($"http://127.0.0.1:{port}/assets/app-icon.png", timeout.Token);
            Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
            Assert.Equal("synthetic icon", await icon.Content.ReadAsStringAsync(timeout.Token));
            using var video = await client.GetAsync($"http://127.0.0.1:{port}/assets/keep-awake.mp4", timeout.Token);
            Assert.Equal("video/mp4", video.Content.Headers.ContentType?.MediaType);
            Assert.Equal("synthetic video", await video.Content.ReadAsStringAsync(timeout.Token));
        }
        finally
        {
            File.Delete(Path.Combine(root, "index.html"));
            File.Delete(Path.Combine(assets, "app-icon.png"));
            File.Delete(Path.Combine(assets, "keep-awake.mp4"));
            Directory.Delete(assets);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void WireAbsenceClearsNumbersButPreservesBooleanDefaultsAndRemoteZero()
    {
        const string fuel = "vehicle.state.flight.instruments.quantum_fuel";
        const string lights = "vehicle.state.lights_enabled";
        const string balance = "actor.state.uec_balance";
        var store = new MfdSnapshotStore([fuel, lights, balance]);
        var remote = new TelemetryAggregator([fuel], Guid.NewGuid());
        var frame = new TelemetryStreamResponse
        {
            Vehicle = new() { State = new() { Flight = new() { Instruments = new() { QuantumFuel = 75 } } } },
            Actor = new() { State = new() { UecBalance = long.MaxValue } }
        };
        store.Ingest(frame, frame.ToByteArray());
        Assert.Equal(75, store.Current().Fields[fuel].Value!.GetValue<float>());
        Assert.Equal(long.MaxValue.ToString(), store.Current().Fields[balance].Value!.GetValue<string>());
        frame.Vehicle.State.Flight.Instruments.QuantumFuel = 0;
        frame.Actor.State.UecBalance = 0;
        var payload = frame.ToByteArray();
        var parsed = TelemetryStreamResponse.Parser.ParseFrom(payload);
        store.Ingest(parsed, payload);
        var snapshot = store.Current();
        Assert.DoesNotContain(fuel, snapshot.Fields.Keys);
        Assert.DoesNotContain(balance, snapshot.Fields.Keys);
        Assert.Contains(fuel, snapshot.SelectedFields);
        Assert.False(snapshot.Fields[lights].Value!.GetValue<bool>());
        remote.Ingest(parsed);
        Assert.Equal(0, remote.CloseWindow(0)!.Snapshot[fuel].Value!.GetValue<float>());
    }

    [Fact]
    public void ExplicitWireZeroAndRepeatedNumericPositionsArePreserved()
    {
        const string fuel = "vehicle.state.flight.instruments.quantum_fuel";
        // Explicitly encoded zero: vehicle / state / flight / instruments / quantum_fuel.
        byte[] payload = [0x1a, 11, 0x12, 9, 0x42, 7, 0x1a, 5, 0x1d, 0, 0, 0, 0];
        var store = new MfdSnapshotStore([fuel, "weapons.countermeasures.amount"]);
        store.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        Assert.Equal(0, store.Current().Fields[fuel].Value!.GetValue<float>());
        var frame = new TelemetryStreamResponse { Weapons = new() };
        frame.Weapons.Countermeasures.Add(new Countermeasures { Amount = 3 });
        frame.Weapons.Countermeasures.Add(new Countermeasures());
        frame.Weapons.Countermeasures.Add(new Countermeasures { Amount = 9 });
        store.Ingest(frame, frame.ToByteArray());
        Assert.Equal("[3,null,9]", store.Current().Fields["weapons.countermeasures.amount"].Value!.ToJsonString());
    }

    [Fact]
    public void WirePresenceMergesSingularMessagesAndIgnoresWrongWireTypes()
    {
        const string speed = "vehicle.state.flight.instruments.speed";
        const string fuel = "vehicle.state.flight.instruments.quantum_fuel";
        var first = new TelemetryStreamResponse
        {
            Vehicle = new() { State = new() { Flight = new() { Instruments = new() { Speed = 42 } } } }
        };
        byte[] zero = [0x1a, 11, 0x12, 9, 0x42, 7, 0x1a, 5, 0x1d, 0, 0, 0, 0];
        var payload = first.ToByteArray().Concat(zero).ToArray();
        var store = new MfdSnapshotStore([speed, fuel]);
        store.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        Assert.Equal(42, store.Current().Fields[speed].Value!.GetValue<float>());
        Assert.Equal(0, store.Current().Fields[fuel].Value!.GetValue<float>());
        byte[] wrongType = [0x1a, 8, 0x12, 6, 0x42, 4, 0x1a, 2, 0x18, 1];
        store.Ingest(TelemetryStreamResponse.Parser.ParseFrom(wrongType), wrongType);
        Assert.DoesNotContain(fuel, store.Current().Fields.Keys);
    }

    [Fact]
    public void SnapshotUsesIndependentSelectionAndClearsOnStreamStart()
    {
        var store = new MfdSnapshotStore(["vehicle.state.flight.instruments.speed"]);
        store.Ingest(new TelemetryStreamResponse
        {
            Vehicle = new()
            {
                State = new()
                {
                    Flight = new() { Instruments = new() { Speed = 42 } }
                }
            }
        });
        var first = store.Current();
        Assert.Equal("connected", first.ConnectionStatus);
        Assert.Equal(42, first.Fields["vehicle.state.flight.instruments.speed"].Value!.GetValue<float>());

        store.StartStream();
        var reset = store.Current();
        Assert.Empty(reset.Fields);
        Assert.Equal("connected", reset.ConnectionStatus);
        Assert.NotEqual(first.StreamId, reset.StreamId);
    }

    [Fact]
    public void PausedAndDisconnectedStatesClearStaleData()
    {
        var store = new MfdSnapshotStore(["actor.vitals.heart_rate"]);
        store.Ingest(new() { Actor = new() { Vitals = new() { HeartRate = 80 } } });
        store.Disconnected();
        var disconnected = store.Current();
        Assert.Equal("disconnected", disconnected.ConnectionStatus);
        Assert.True(disconnected.Fields["actor.vitals.heart_rate"].LastKnown);

        store.SetPaused();
        var paused = store.Current();
        Assert.Equal("paused", paused.ConnectionStatus);
        Assert.Empty(paused.Fields);
        Assert.Null(paused.LastFrameAt);
    }

    [Fact]
    public void AgesFieldsAndFramesWithMonotonicTimeAcrossClockChanges()
    {
        var clock = new MfdClock();
        const string speed = "vehicle.state.flight.instruments.speed";
        const string heartRate = "actor.vitals.heart_rate";
        var store = new MfdSnapshotStore([speed, heartRate], clock);
        store.Ingest(new() { Vehicle = new() { State = new() { Flight = new() { Instruments = new() { Speed = 12 } } } } });
        clock.Advance(TimeSpan.FromSeconds(3));
        clock.WallTime -= TimeSpan.FromHours(1);
        store.Ingest(new() { Actor = new() { Vitals = new() { HeartRate = 72 } } });
        clock.Advance(TimeSpan.FromMilliseconds(500));

        var snapshot = store.Current();
        Assert.Equal(3500, snapshot.Fields[speed].AgeMs);
        Assert.Equal(500, snapshot.Fields[heartRate].AgeMs);
        Assert.Equal(500, snapshot.LastFrameAgeMs);
        Assert.Contains(speed, snapshot.SelectedFields);
        store.StartStream();
        Assert.Null(store.Current().LastFrameAgeMs);
    }

    private sealed class MfdClock : TimeProvider
    {
        public DateTimeOffset WallTime { get; set; } = DateTimeOffset.UnixEpoch;
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => WallTime;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan by) { timestamp += by.Ticks; WallTime += by; }
    }

    [Fact]
    public void IncompleteRemoteSelectionCanBeEditedButCannotStart()
    {
        var options = JsonSerializer.Deserialize<GatewayOptions>(
            """{"endpoint":"wss://example.org/telemetry","fields":[]}""", WireJson.Options)!;
        options.Validate(requireDestination: false);
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void WildcardMfdAddressIsRejected()
    {
        var options = new GatewayOptions
        {
            MfdEnabled = true, MfdAddress = "0.0.0.0", MfdFields = ["actor.state.locomotion"]
        };
        Assert.Throws<ArgumentException>(() => options.Validate(requireDestination: false));
        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void SnapshotRetainsDuplicateEventHistoryAcrossEmptySections()
    {
        const string eventType = "events.events.type";
        var clock = new MfdClock();
        var store = new MfdSnapshotStore([eventType], clock);
        var frame = new TelemetryStreamResponse { Events = new() };
        frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorLeaveSeat });
        store.Ingest(frame);
        Assert.Equal("[\"GAME_EVENT_TYPE_ACTOR_LEAVE_SEAT\"]", store.Current().Fields[eventType].Value!.ToJsonString());
        store.Ingest(frame);
        Assert.Equal(2, store.Current().Fields[eventType].Value!.AsArray().Count);
        clock.Advance(TimeSpan.FromSeconds(3));
        store.Ingest(new() { Actor = new() });
        Assert.True(store.Current().Fields.ContainsKey(eventType));
        store.Ingest(new() { Events = new() });
        Assert.Equal(2, store.Current().Fields[eventType].Value!.AsArray().Count);
        Assert.Equal(3000, store.Current().Fields[eventType].AgeMs);
    }

    [Fact]
    public void EventIdentitiesSurviveSnapshotRepeatsAndRestartWithTheGameStream()
    {
        var store = new MfdSnapshotStore(["events.events.type"]);
        var frame = new TelemetryStreamResponse { Events = new() };
        frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorEnterSeat });
        frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorEnterSeat });
        store.Ingest(frame);
        var first = store.Current();
        Assert.Equal(new long[] { 1, 2 }, first.Events.Select(item => item.Sequence));
        Assert.Equal(first.Events, store.Current().Events);
        Assert.Equal(2, Assert.Single(first.PageEvents).Sequence);
        store.StartStream();
        Assert.Empty(store.Current().Events);
        Assert.Empty(store.Current().PageEvents);
        store.Ingest(frame);
        var restarted = store.Current();
        Assert.NotEqual(first.StreamId, restarted.StreamId);
        Assert.Equal(new long[] { 1, 2 }, restarted.Events.Select(item => item.Sequence));
    }

    [Fact]
    public void EventHistoryReportsOverflowAndResetsWithTheStream()
    {
        const string eventType = "events.events.type";
        var store = new MfdSnapshotStore([eventType]);
        var frame = new TelemetryStreamResponse { Events = new() };
        frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorSpawn });
        for (var i = 0; i < MfdSnapshotStore.MaxEvents; i++)
            frame.Events.Events.Add(new GameEvent { Type = GameEvent.Types.GameEventType.ActorDead });
        store.Ingest(frame);
        var history = store.Current();
        Assert.Equal(100, history.Fields[eventType].Value!.AsArray().Count);
        Assert.Equal("GAME_EVENT_TYPE_ACTOR_DEAD", history.Fields[eventType].Value!.AsArray()[0]!.GetValue<string>());
        Assert.Equal(1, history.EventsDropped);
        Assert.Equal(2, history.Events[0].Sequence);
        Assert.Equal(101, history.Events[^1].Sequence);
        Assert.Contains(history.PageEvents, item => item.Sequence == 1 && item.Type == "GAME_EVENT_TYPE_ACTOR_SPAWN");
        store.Disconnected();
        Assert.True(store.Current().Fields[eventType].LastKnown);
        Assert.Equal(1, store.Current().EventsDropped);
        store.StartStream();
        Assert.Empty(store.Current().Fields);
        Assert.Equal(0, store.Current().EventsDropped);
        store.Ingest(frame);
        store.SetPaused();
        Assert.Empty(store.Current().Fields);
        Assert.Empty(store.Current().PageEvents);
        Assert.Equal(0, store.Current().EventsDropped);
        var unselected = new MfdSnapshotStore([]);
        unselected.Ingest(frame);
        Assert.Empty(unselected.Current().Fields);
        Assert.Empty(unselected.Current().Events);
        Assert.Empty(unselected.Current().PageEvents);
        Assert.Equal(0, unselected.Current().EventsDropped);
    }

    [Fact]
    public void EmptyMfdSelectionCanBeSavedButCannotBeStarted()
    {
        var options = new GatewayOptions
        {
            RemoteEnabled = false, MfdEnabled = true, MfdAddress = "127.0.0.1"
        };
        options.Validate(requireDestination: false);
        var error = Assert.Throws<ArgumentException>(() => options.Validate());
        Assert.Contains("MFD telemetry field", error.Message);
        (options with { MfdFields = ["actor.state.locomotion"] }).Validate();
    }

    [Fact]
    public void MigratedOptionsPreserveRemoteAndKeepMfdOff()
    {
        var options = JsonSerializer.Deserialize<GatewayOptions>(
            """{"endpoint":"wss://example.org/telemetry","fields":["actor.state.locomotion"]}""",
            WireJson.Options)!;
        Assert.True(options.RemoteEnabled);
        Assert.False(options.MfdEnabled);
        Assert.True(options.RemoteConfigured);
        Assert.Single(options.Fields);
        Assert.Empty(options.MfdFields);
    }
}
