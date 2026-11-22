using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class TrayContext : ApplicationContext
{
    public const string ProductName = "Star Citizen Telemetry Gateway";
    private readonly Icon appIcon;
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly SettingsStore store;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60000 };
    private readonly System.Windows.Forms.Timer usageTimer = new() { Interval = 6 * 60 * 60 * 1000 };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource usageCancellation = new();
    private readonly HttpClient usageClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(10) };
    private readonly UsageIdentityStore usageIdentity = new();
    private readonly UsageReporter usageReporter;
    private readonly bool smokeTest;
    private Task usageTask = Task.CompletedTask;
    private bool pendingUsage;
    private bool pendingCollection;
    private string usageStatus = "Not reported";
    private readonly ToolStripMenuItem start;
    private readonly ToolStripMenuItem pause;
    private readonly DashboardForm statusWindow = new();
    private GatewayOptions options;
    private string key;
    private GatewayRuntime? runtime;
    private CancellationTokenSource? stop;
    private Task? running;
    private string? error;
    private bool busy;
    private bool updateBusy;
    private bool disposed;
    private string updateStatus = "Not checked";
    private readonly ToolStripMenuItem checkUpdates;

    public TrayContext(bool smokeTest = false, SettingsStore? settingsStore = null)
    {
        this.smokeTest = smokeTest;
        usageReporter = new UsageReporter(usageClient, usageIdentity, UsageReporter.Endpoint);
        store = settingsStore ?? new SettingsStore();
        (options, key) = store.Load();
        if (!smokeTest && !options.UsageNoticeShown)
        {
            var enabled = MessageBox.Show(
                "This application sends usage statistics to the project service hosted by Cloudflare: " +
                "a random installation ID, application version and whether collection is running. " +
                "No game data, receiver URL or API key is sent. Cloudflare can see your IP address. " +
                "Records become eligible for hourly cleanup after 30 days.\n\n" +
                "Send usage statistics? You can change this later in Settings > Privacy.",
                ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;
            options = options with { UsageStatisticsEnabled = enabled, UsageNoticeShown = true };
            store.Save(options, key);
        }
        if (!smokeTest && !options.UsageStatisticsEnabled)
        {
            try { usageIdentity.Delete(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "Usage statistics are disabled, but the local installation ID could not be deleted. " +
                    "No further reports will be sent. " + ex.Message;
            }
        }
        appIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "mfd", "assets", "app-icon.ico"));
        statusWindow.Icon = appIcon;
        icon = new NotifyIcon { Text = ProductName, Icon = appIcon, Visible = true };
        menu = new ContextMenuStrip();
        menu.Items.Add("Open dashboard", null, (_, _) => ShowDashboard());
        menu.Items.Add("Settings", null, async (_, _) => await EditSettings());
        start = new("Start", null, (_, _) => Start());
        pause = new("Pause and clear buffer", null, async (_, _) => await Pause());
        menu.Items.Add(start);
        menu.Items.Add(pause);
        checkUpdates = new("Check for updates...", null, async (_, _) => await CheckUpdates(true));
        menu.Items.Add(checkUpdates);
        menu.Items.Add("About", null, (_, _) => MessageBox.Show(
            ProductName + " " + BuildInfo.Version + "\n\nIndependent community project.\n" +
            "Not affiliated with, endorsed by, sponsored by, or supported by\n" +
            "Cloud Imperium Games.\n" +
            "Star Citizen and related marks belong to their respective owners.\n\n" +
            "Use at your own risk. Provided \"AS IS\", without warranties.\n" +
            "Authors and contributors disclaim liability to the fullest\n" +
            "extent permitted by law. See docs/DISCLAIMER.md.\n\n" +
            "Apache-2.0. Protocol observed in PTU 5.0.202.205.\n" +
            "See LICENSE, NOTICE and docs in the application folder.", ProductName));
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            if (busy) return;
            busy = true;
            await StopUsageAsync(deleteIdentity: false);
            await lifetime.CancelAsync();
            await Stop();
            ExitThread();
        });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => ShowDashboard();
        statusWindow.StartRequested += (_, _) => Start();
        statusWindow.PauseRequested += async (_, _) => await Pause();
        statusWindow.SettingsRequested += async (_, _) => await EditSettings();
        statusWindow.PairingCodeRequested += (_, _) =>
        {
            if (runtime?.Status.Mfd != "Listener active" || runtime.MfdServer is null) return;
            var code = runtime.MfdServer.CreatePairingCode();
            statusWindow.ShowPairingCode(code, runtime.MfdServer.CreatePairingUrl(code));
        };
        statusWindow.RevokeRequested += (_, _) =>
        {
            try
            {
                if (statusWindow.SelectedDevice is { } id && runtime?.MfdServer?.RevokeDevice(id) != true)
                    MessageBox.Show(statusWindow, "The device is no longer available.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(statusWindow, "The device permission could not be revoked. Check the local device store.\n\n" + ex.Message,
                    ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshStatus();
        };
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = 24 * 60 * 60 * 1000;
            if (options.AutomaticUpdateChecks) await CheckUpdates(false);
        };
        if (!smokeTest) updateTimer.Start();
        usageTimer.Tick += (_, _) => RequestUsageReport(running is { IsCompleted: false });
        RefreshStatus();
        statusWindow.Show();
        if (!smokeTest && options.UsageStatisticsEnabled)
        {
            usageTimer.Start();
            RequestUsageReport(false);
        }
        if (smokeTest)
        {
            using var settings = new SettingsForm(options, key);
            settings.Show();
            settings.Refresh();
            settings.Close();
            var smokeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            smokeTimer.Tick += (_, _) => { smokeTimer.Dispose(); ExitThread(); };
            smokeTimer.Start();
        }
    }

    private void ShowDashboard()
    {
        statusWindow.Show();
        if (statusWindow.WindowState == FormWindowState.Minimized)
            statusWindow.WindowState = FormWindowState.Normal;
        statusWindow.Activate();
    }

    private void Start()
    {
        if (running is not null || busy) return;
        try
        {
            options.Validate();
            if (options.RemoteConfigured && (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)))
                throw new ArgumentException("Configure an API key in Settings first.");
            error = null;
            runtime = new();
            stop = new();
            var activeRuntime = runtime;
            var token = stop.Token;
            running = Observe(Task.Run(() => activeRuntime.RunAsync(options, key, token)));
            RequestUsageReport(true);
        }
        catch (ArgumentException ex) { MessageBox.Show(ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        RefreshStatus();
    }

    private async Task Observe(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) when (stop?.IsCancellationRequested == true) { }
        catch (Exception ex)
        {
            error = ex is ReceiverRejectedException or ArgumentException
                ? ex.Message : $"Collection stopped due to {ex.GetType().Name}. Check settings and restart.";
            icon.ShowBalloonTip(8000, ProductName, error, ToolTipIcon.Error);
        }
    }

    private async Task Stop()
    {
        if (stop is not null) await stop.CancelAsync();
        if (running is not null) await running;
        running = null;
        stop?.Dispose();
        stop = null;
        RefreshStatus();
    }

    private async Task Pause()
    {
        if (busy) return;
        busy = true;
        RefreshStatus();
        try { await Stop(); }
        finally { busy = false; RefreshStatus(); }
    }

    private async Task EditSettings()
    {
        if (busy || updateBusy) return;
        busy = true;
        try
        {
            using var form = new SettingsForm(options, key, running is not null);
            if (form.ShowDialog(statusWindow.Visible ? statusWindow : null) != DialogResult.OK) return;
            await Stop();
            var saved = false;
            try
            {
                var previousUsageEnabled = options.UsageStatisticsEnabled;
                store.Save(form.Options, form.ApiKey);
                saved = true;
                options = form.Options;
                key = form.ApiKey;
                error = null;
                updateStatus = "Not checked";
                if (!smokeTest && previousUsageEnabled && !options.UsageStatisticsEnabled)
                    await StopUsageAsync(deleteIdentity: true);
                else if (!smokeTest && !previousUsageEnabled && options.UsageStatisticsEnabled)
                {
                    usageCancellation.Dispose();
                    usageCancellation = new CancellationTokenSource();
                    usageStatus = "Not reported";
                    usageTimer.Start();
                    RequestUsageReport(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                error = saved
                    ? "Settings were saved, but the usage statistics ID could not be deleted. No further reports will be sent."
                    : "Settings were not saved. Collection remains stopped.";
                MessageBox.Show(error + "\n\n" + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally { busy = false; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        if (running?.IsCompleted == true)
        {
            running = null;
            stop?.Dispose();
            stop = null;
        }
        start.Enabled = running is null && !busy;
        pause.Enabled = running is not null && !busy;
        checkUpdates.Enabled = !busy && !updateBusy;
        statusWindow.UpdateView(running is not null, busy || updateBusy, options, runtime?.Status,
            runtime?.MfdServer?.Status, updateStatus,
            options.UsageStatisticsEnabled ? usageStatus : "Disabled", error);
    }

    private void RequestUsageReport(bool collectionRunning)
    {
        if (smokeTest || !options.UsageStatisticsEnabled || usageCancellation.IsCancellationRequested) return;
        pendingUsage = true;
        pendingCollection |= collectionRunning;
        if (usageTask.IsCompleted) usageTask = SendUsageReportsAsync();
    }

    private async Task SendUsageReportsAsync()
    {
        while (pendingUsage && !usageCancellation.IsCancellationRequested)
        {
            var collectionRunning = pendingCollection;
            pendingUsage = false;
            pendingCollection = false;
            try
            {
                await usageReporter.ReportAsync(collectionRunning, usageCancellation.Token);
                usageStatus = "Reported";
            }
            catch (OperationCanceledException) when (usageCancellation.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                UnauthorizedAccessException or OperationCanceledException)
            {
                usageStatus = "Report failed (" + ex.GetType().Name + "); will retry later";
            }
            if (!usageCancellation.IsCancellationRequested) RefreshStatus();
        }
    }

    private async Task StopUsageAsync(bool deleteIdentity)
    {
        usageTimer.Stop();
        pendingUsage = false;
        pendingCollection = false;
        await usageCancellation.CancelAsync();
        await usageTask;
        if (deleteIdentity) usageIdentity.Delete();
    }

    private async Task CheckUpdates(bool interactive)
    {
        if (busy || updateBusy || lifetime.IsCancellationRequested) return;
        updateBusy = true;
        try
        {
            var updater = ApplicationUpdater.Create(options.IncludePreviewUpdates);
            if (!updater.IsInstalled)
            {
                updateStatus = "Use Setup to enable built-in updates";
                if (interactive) MessageBox.Show(updateStatus, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            updateStatus = "Checking GitHub releases...";
            RefreshStatus();
            var version = await updater.CheckAsync();
            lifetime.Token.ThrowIfCancellationRequested();
            if (version is null)
            {
                updateStatus = "No compatible newer release found";
                if (interactive) MessageBox.Show(updateStatus, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            updateStatus = "Version " + version + " is available";
            if (!interactive)
            {
                icon.ShowBalloonTip(8000, ProductName, updateStatus + ". Use Check for updates to download.", ToolTipIcon.Info);
                return;
            }
            var previewWarning = options.IncludePreviewUpdates ? "\n\nPreview releases may be unsigned and are not stable." : "";
            if (MessageBox.Show("Download version " + version + " from GitHub?" + previewWarning,
                    ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            IProgress<int> progress = new Progress<int>(value =>
            {
                if (lifetime.IsCancellationRequested) return;
                updateStatus = "Downloading update: " + value + "%";
                RefreshStatus();
            });
            await updater.DownloadAsync(progress.Report, lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            updateStatus = "Downloaded " + version + "; awaiting installation approval";
            if (MessageBox.Show("Install version " + version + " and restart now?\n\n" +
                    "Telemetry collection will stop and unsent data will be cleared. " +
                    "Collection stays paused after restart.", ProductName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            busy = true;
            try
            {
                await Stop();
                updater.ScheduleInstall();
                await StopUsageAsync(deleteIdentity: false);
                await lifetime.CancelAsync();
                ExitThread();
            }
            finally { busy = false; }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            updateStatus = "Update failed (" + ex.GetType().Name + "). Try again later.";
            if (!lifetime.IsCancellationRequested)
            {
                if (interactive) MessageBox.Show(updateStatus, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                else icon.ShowBalloonTip(8000, ProductName, updateStatus, ToolTipIcon.Warning);
            }
        }
        finally
        {
            updateBusy = false;
            if (!lifetime.IsCancellationRequested) RefreshStatus();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            timer.Dispose();
            updateTimer.Dispose();
            usageTimer.Dispose();
            usageCancellation.Cancel();
            usageCancellation.Dispose();
            usageClient.Dispose();
            lifetime.Cancel();
            icon.Visible = false;
            menu.Dispose();
            icon.Dispose();
            appIcon.Dispose();
            statusWindow.Dispose();
            stop?.Dispose();
        }
        base.Dispose(disposing);
    }
}
