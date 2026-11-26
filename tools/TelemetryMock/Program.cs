using VerseDataGateway.TelemetryMock;

if (args.Length > 1 || (args.Length == 1 &&
    (!int.TryParse(args[0], out var parsed) || parsed is < 1024 or > 65535)))
{
    Console.Error.WriteLine("Usage: TelemetryMock [port (1024-65535)]");
    return 1;
}

var port = args.Length == 0 ? 29420 : int.Parse(args[0]);
await using var app = MockHost.Build(port);
Console.WriteLine($"Synthetic game telemetry mock on 127.0.0.1:{port}. Stop with Ctrl+C.");
await app.RunAsync();
return 0;
