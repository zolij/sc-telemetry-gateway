using Velopack;

namespace ScTelemetryGateway.Windows;

internal static class InstallerSmoke
{
    public static bool TryRun(string[] args, out int result)
    {
        result = 0;
        if (args.Length != 2 || args[0] is not ("--download-update-smoke-test" or "--apply-update-smoke-test"))
            return false;
        try
        {
            if (!Path.IsPathFullyQualified(args[1]) || !Directory.Exists(args[1]))
                throw new ArgumentException("The installer test feed must be an existing absolute local directory.");
            var manager = new UpdateManager(args[1], new UpdateOptions
            {
                ExplicitChannel = "preview", MaximumDeltasBeforeFallback = -1, AllowVersionDowngrade = false
            });
            if (!manager.IsInstalled) throw new InvalidOperationException("Installer test requires an installed application.");
            if (args[0] == "--download-update-smoke-test")
            {
                var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult()
                    ?? throw new InvalidOperationException("Installer test feed contains no newer update.");
                manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            }
            else
            {
                var pending = manager.UpdatePendingRestart
                    ?? throw new InvalidOperationException("Installer test requires a downloaded update.");
                manager.WaitExitThenApplyUpdates(pending, restart: true, restartArgs: ["--smoke-test"]);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            result = 1;
        }
        return true;
    }
}
