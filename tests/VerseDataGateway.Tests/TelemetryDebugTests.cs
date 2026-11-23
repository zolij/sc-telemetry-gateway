using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sc.Game.Telemetry.V1;
using System.Net;
using System.Text.Json;
using VerseDataGateway.Core;
using VerseDataGateway.TelemetryDebug;

namespace VerseDataGateway.Tests;

public sealed class TelemetryDebugTests
{
    [Fact]
    public void DisplaysWirePresenceAndExplicitDefaultValues()
    {
        byte[] payload = [0x32, 0x04, 0x12, 0x02, 0x08, 0x00];
        var view = new TelemetryDebugView();
        view.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        var output = view.Render(DateTimeOffset.UtcNow);

        Assert.Contains("locomotion=ACTOR_LOCOMOTION_STATE_UNSPECIFIED (0)", output);
        Assert.Contains("crew_role=<omitted>", output);
        Assert.Contains("vitals=<omitted>", output);
        Assert.Contains("[vehicle] waiting", output);
    }

    [Fact]
    public void PreservesRawNumbersListsAndStrings()
    {
        var frame = new TelemetryStreamResponse
        {
            Vehicle = new() { State = new()
            {
                Name = "Ship \"A\"",
                Flight = new() { Instruments = new() { Speed = float.MaxValue } },
                Shields = new() { Faces = { 0.25f, 0.75f } }
            } },
            Actor = new() { State = new() { UecBalance = 9007199254740993 } }
        };
        var payload = frame.ToByteArray();
        var view = new TelemetryDebugView();
        view.Ingest(TelemetryStreamResponse.Parser.ParseFrom(payload), payload);
        var output = view.Render(DateTimeOffset.UtcNow);

        Assert.Contains("name=" + JsonSerializer.Serialize("Ship \"A\""), output);
        Assert.Contains("speed=3.4028235E+38", output);
        Assert.Contains("faces=[0.25, 0.75]", output);
        Assert.Contains("uec_balance=9007199254740993", output);
    }

    [Fact]
    public void RetainsOtherSectionsUntilStreamIsCleared()
    {
        var view = new TelemetryDebugView();
        var actor = new TelemetryStreamResponse { Actor = new() { Vitals = new() { HeartRate = 72 } } };
        var vehicle = new TelemetryStreamResponse { Vehicle = new() { State = new() { Name = "Ship" } } };
        view.Ingest(actor, actor.ToByteArray());
        view.Ingest(vehicle, vehicle.ToByteArray());
        var output = view.Render(DateTimeOffset.UtcNow);
        Assert.Contains("heart_rate=72", output);
        Assert.Contains("name=\"Ship\"", output);

        output = view.Render(DateTimeOffset.UtcNow, "actor");
        Assert.Contains("heart_rate=72", output);
        Assert.DoesNotContain("[vehicle]", output);

        view.Clear();
        output = view.Render(DateTimeOffset.UtcNow);
        Assert.DoesNotContain("heart_rate=72", output);
        Assert.Contains("[actor] waiting", output);
        Assert.Contains("[vehicle] waiting", output);
    }

    [Fact]
    public async Task ReadsDirectlyFromGameStream()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var app = builder.Build();
        app.MapGrpcService<DebugGame>();
        await app.StartAsync(stop.Token);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        var port = new Uri(Assert.Single(addresses.Addresses)).Port;
        var view = new TelemetryDebugView();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new GameTelemetryReader(port);
        var task = reader.RunAsync([], view.SetStatus, stop.Token, view.Clear, view.Clear, (frame, payload) =>
        {
            view.Ingest(frame, payload);
            received.TrySetResult();
        });
        try
        {
            await received.Task.WaitAsync(stop.Token);
            Assert.Contains("heart_rate=72", view.Render(DateTimeOffset.UtcNow));
        }
        finally
        {
            await stop.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    public sealed class DebugGame : GameTelemetryService.GameTelemetryServiceBase
    {
        public override async Task TelemetryStream(TelemetryStreamRequest request,
            IServerStreamWriter<TelemetryStreamResponse> responseStream, ServerCallContext context)
        {
            await responseStream.WriteAsync(new() { Actor = new() { Vitals = new() { HeartRate = 72 } } });
            try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
        }
    }
}
