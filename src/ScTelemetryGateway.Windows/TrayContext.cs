using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class TrayContext : ApplicationContext
{
    public const string ProductName = "Star Citizen Telemetry Gateway";
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private readonly SettingsStore store = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60000 };
    private readonly CancellationTokenSource lifetime = new();
    private readonly ToolStripMenuItem start;
    private readonly ToolStripMenuItem pause;
    private readonly Form statusWindow = new() { Text = ProductName, Size = new Size(640, 300), StartPosition = FormStartPosition.CenterScreen };
    private readonly Label statusLabel = new() { Dock = DockStyle.Fill, Padding = new Padding(16) };
    private GatewayOptions options;
    private string key;
    private GatewayRuntime? runtime;
    private CancellationTokenSource? stop;
    private Task? running;
    private string? error;
    private bool busy;
    private bool updateBusy;
    private string updateStatus = "Not checked";
    private readonly ToolStripMenuItem checkUpdates;

    public TrayContext(bool smokeTest = false)
    {
        (options, key) = store.Load();
        icon = new NotifyIcon { Text = ProductName, Icon = SystemIcons.Application, Visible = true };
        menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => { statusWindow.Show(); statusWindow.Activate(); });
        menu.Items.Add("Settings", null, async (_, _) => await EditSettings());
        start = new("Start", null, (_, _) => Start());
        pause = new("Pause and clear buffer", null, async (_, _) => await Stop());
        menu.Items.Add(start);
        menu.Items.Add(pause);
        checkUpdates = new("Check for updates...", null, async (_, _) => await CheckUpdates(true));
        menu.Items.Add(checkUpdates);
        menu.Items.Add("About", null, (_, _) => MessageBox.Show(
            ProductName + " " + BuildInfo.Version + "\n\nIndependent community project.\n" +
            "Not affiliated with, endorsed by, sponsored by, or supported by\n" +
            "Cloud Imperium Games.\n" +
            "Star Citizen and related marks belong to their respective owners.\n\n" +
            "Apache-2.0. Protocol observed in PTU 5.0.202.205.\n" +
            "See LICENSE, NOTICE and docs in the application folder.", ProductName));
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            if (busy) return;
            busy = true;
            await lifetime.CancelAsync();
            await Stop();
            ExitThread();
        });
        icon.ContextMenuStrip = menu;
        icon.DoubleClick += (_, _) => { statusWindow.Show(); statusWindow.Activate(); };
        statusWindow.Controls.Add(statusLabel);
        statusWindow.FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; statusWindow.Hide(); }
        };
        timer.Tick += (_, _) => RefreshStatus();
        timer.Start();
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = 24 * 60 * 60 * 1000;
            if (options.AutomaticUpdateChecks) await CheckUpdates(false);
        };
        if (!smokeTest) updateTimer.Start();
        RefreshStatus();
        if (smokeTest)
        {
            statusWindow.Show();
            using var settings = new SettingsForm(options, key);
            settings.Show();
            settings.Refresh();
            settings.Close();
            var smokeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
            smokeTimer.Tick += (_, _) => { smokeTimer.Dispose(); ExitThread(); };
            smokeTimer.Start();
        }
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
            running = Observe(runtime.RunAsync(options, key, stop.Token));
            RebindTrayMenu();
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
        RebindTrayMenu();
        RefreshStatus();
    }

    private void RebindTrayMenu()
    {
        icon.Visible = false;
        icon.ContextMenuStrip = null;
        icon.Visible = true;
        icon.ContextMenuStrip = menu;
    }

    private async Task EditSettings()
    {
        if (busy || updateBusy) return;
        busy = true;
        try
        {
            using var form = new SettingsForm(options, key, runtime);
            if (form.ShowDialog() != DialogResult.OK) return;
            await Stop();
            try
            {
                store.Save(form.Options, form.ApiKey);
                options = form.Options;
                key = form.ApiKey;
                error = null;
                updateStatus = "Not checked";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                System.Security.Cryptography.CryptographicException or ArgumentException)
            {
                error = "Settings were not saved. Collection remains stopped.";
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
        var status = runtime?.Status;
        statusLabel.Text = (running is null ? "Collection paused. No data is being uploaded." : "Collection running.") +
            $"\n\nGame: {status?.Game ?? "Stopped"}\nReceiver: {status?.Delivery ?? "Stopped"}" +
            $"\nMFD: {status?.Mfd ?? "Stopped"}" +
            (runtime?.MfdServer?.Status.Url is { } url ? "\nMFD URL: " + url : "") +
            $"\nQueued: {status?.Queued ?? 0} batches / {status?.QueueBytes ?? 0} bytes" +
            $"\nDropped: {status?.BatchesDropped ?? 0} batches, {status?.EventsDropped ?? 0} events" +
            $"\nLast ACK (UTC): {status?.LastAcknowledged?.ToString("u") ?? "None"}" +
            $"\nSelected fields: {options.Fields.Count}\nUpdates: {updateStatus}" + (error is null ? "" : "\n\nError: " + error);
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
        if (disposing)
        {
            timer.Dispose();
            updateTimer.Dispose();
            lifetime.Cancel();
            icon.Visible = false;
            menu.Dispose();
            icon.Dispose();
            statusWindow.Dispose();
            stop?.Dispose();
        }
        base.Dispose(disposing);
    }
}
