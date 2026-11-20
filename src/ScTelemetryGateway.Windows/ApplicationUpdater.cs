using Velopack;
using Velopack.Sources;

namespace ScTelemetryGateway.Windows;

public sealed class ApplicationUpdater(UpdateManager manager)
{
    public const string Repository = "https://github.com/zolij/sc-telemetry-gateway";
    private UpdateInfo? available;
    private bool downloaded;

    public bool IsInstalled => manager.IsInstalled && !manager.IsPortable;

    public static ApplicationUpdater Create(bool includePreviews) => new(new UpdateManager(
        new GithubSource(Repository, null, includePreviews),
        new UpdateOptions
        {
            ExplicitChannel = includePreviews ? "preview" : "win",
            AllowVersionDowngrade = false,
            MaximumDeltasBeforeFallback = -1
        }));

    public async Task<string?> CheckAsync()
    {
        if (!IsInstalled)
            throw new InvalidOperationException("Install the application using Setup before using built-in updates.");
        available = null;
        downloaded = false;
        available = await manager.CheckForUpdatesAsync();
        return available?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(Action<int> progress, CancellationToken token)
    {
        var update = available ?? throw new InvalidOperationException("Check for an update before downloading.");
        downloaded = false;
        await manager.DownloadUpdatesAsync(update, progress, token);
        token.ThrowIfCancellationRequested();
        downloaded = true;
    }

    public void ScheduleInstall()
    {
        if (!downloaded || available is null)
            throw new InvalidOperationException("A verified update must finish downloading before installation.");
        manager.WaitExitThenApplyUpdates(available.TargetFullRelease, restart: true);
    }
}
