using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class TrayContext : ApplicationContext
{
    public const string ProductName = "Star Citizen Telemetry Gateway";
    private readonly NotifyIcon icon;
    private readonly SettingsStore store = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
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

    public TrayContext(bool smokeTest = false)
    {
        (options, key) = store.Load();
        icon = new NotifyIcon { Text = ProductName, Icon = SystemIcons.Application, Visible = true };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Status", null, (_, _) => { statusWindow.Show(); statusWindow.Activate(); });
        menu.Items.Add("Settings", null, async (_, _) => await EditSettings());
        start = new("Start", null, (_, _) => Start());
        pause = new("Pause and clear buffer", null, async (_, _) => await Stop());
        menu.Items.Add(start);
        menu.Items.Add(pause);
        menu.Items.Add("About", null, (_, _) => MessageBox.Show(
            ProductName + " 0.1.0\n\nIndependent community project.\n" +
            "Not affiliated with, endorsed by, sponsored by, or supported by\n" +
            "Cloud Imperium Games or Roberts Space Industries.\n" +
            "Star Citizen and related marks belong to their respective owners.\n\n" +
            "Apache-2.0. Protocol observed in PTU 5.0.202.205.\n" +
            "See LICENSE, NOTICE and docs in the application folder.", ProductName));
        menu.Items.Add("Exit", null, async (_, _) =>
        {
            if (busy) return;
            busy = true;
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
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
                throw new ArgumentException("Configure an API key in Settings first.");
            error = null;
            runtime = new();
            stop = new();
            running = Observe(runtime.RunAsync(options, key, stop.Token));
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

    private async Task EditSettings()
    {
        if (busy) return;
        busy = true;
        try
        {
            using var form = new SettingsForm(options, key);
            if (form.ShowDialog() != DialogResult.OK) return;
            await Stop();
            try
            {
                store.Save(form.Options, form.ApiKey);
                options = form.Options;
                key = form.ApiKey;
                error = null;
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
        var status = runtime?.Status;
        statusLabel.Text = (running is null ? "Collection paused. No data is being uploaded." : "Collection running.") +
            $"\n\nGame: {status?.Game ?? "Stopped"}\nReceiver: {status?.Delivery ?? "Stopped"}" +
            $"\nQueued: {status?.Queued ?? 0} batches / {status?.QueueBytes ?? 0} bytes" +
            $"\nDropped: {status?.BatchesDropped ?? 0} batches, {status?.EventsDropped ?? 0} events" +
            $"\nLast ACK (UTC): {status?.LastAcknowledged?.ToString("u") ?? "None"}" +
            $"\nSelected fields: {options.Fields.Count}" + (error is null ? "" : "\n\nError: " + error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            timer.Dispose();
            icon.Visible = false;
            icon.ContextMenuStrip?.Dispose();
            icon.Dispose();
            statusWindow.Dispose();
            stop?.Dispose();
        }
        base.Dispose(disposing);
    }
}
