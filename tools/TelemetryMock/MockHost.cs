using System.Net;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace VerseDataGateway.TelemetryMock;

public static class MockHost
{
    public static WebApplication Build(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton<MockState>();
        var app = builder.Build();
        app.MapGrpcService<MockTelemetryService>();
        return app;
    }
}
