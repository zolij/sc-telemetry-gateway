using VerseDataGateway.Core;
using VerseDataGateway.TelemetryDebug;

if (args.Length > 1 || args.Length == 1 && (!int.TryParse(args[0], out _) || int.Parse(args[0]) is < 1024 or > 65535))
{
    Console.Error.WriteLine("Usage: dotnet run --project tools/TelemetryDebug -- [game-port (1024-65535)]");
    return 2;
}

var port = args.Length == 0 ? 29420 : int.Parse(args[0]);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
var view = new TelemetryDebugView();
var reader = new GameTelemetryReader(port);
var task = reader.RunAsync([], view.SetStatus, stop.Token, view.Clear, view.Clear, view.Ingest);
async Task Display()
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    while (await timer.WaitForNextTickAsync(stop.Token))
    {
        if (!Console.IsOutputRedirected) Console.Clear();
        Console.Write(view.Render(DateTimeOffset.UtcNow));
    }
}
var display = Display();
try
{
    await await Task.WhenAny(task, display);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
finally
{
    await stop.CancelAsync();
    try { await Task.WhenAll(task, display); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
}
return 0;
