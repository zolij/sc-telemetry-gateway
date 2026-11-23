namespace ScTelemetryGateway.Installer;

internal sealed class PrivacyDialog : Form
{
    private readonly CheckBox usage = new()
    {
        Text = "Send application usage statistics",
        Checked = true,
        AutoSize = true
    };
    private readonly Button install = new() { Text = "Install", Location = new Point(370, 265) };
    private readonly Button cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(465, 265) };
    private bool installing;

    public PrivacyDialog(Func<bool, Task> installApplication)
    {
        Text = "Install Verse Data Gateway";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(560, 310);

        var notice = new Label
        {
            AutoSize = false,
            Size = new Size(520, 160),
            Location = new Point(20, 20),
            Text = "The application can send usage statistics to the project service hosted by Cloudflare: " +
                "a random installation ID, application version and whether collection is running. " +
                "No game data, receiver URL or API key is sent. Cloudflare can see your IP address. " +
                "Records become eligible for hourly cleanup after 30 days.\n\n" +
                "You can disable statistics below or change the choice later in Settings > Privacy."
        };
        usage.Location = new Point(20, 185);
        var policy = new LinkLabel
        {
            Text = "Usage statistics and privacy",
            AutoSize = true,
            Location = new Point(20, 220)
        };
        policy.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "https://github.com/zolij/verse-data-gateway/blob/main/docs/USAGE-STATISTICS.md")
                { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        var status = new Label { AutoSize = true, Location = new Point(20, 270) };
        install.Click += async (_, _) =>
        {
            installing = true;
            install.Enabled = cancel.Enabled = usage.Enabled = policy.Enabled = false;
            ControlBox = false;
            status.Text = "Installing...";
            try
            {
                await installApplication(usage.Checked);
                installing = false;
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
                System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                installing = false;
                status.Text = "";
                install.Enabled = cancel.Enabled = usage.Enabled = policy.Enabled = true;
                ControlBox = true;
            }
        };
        FormClosing += (_, e) => { if (installing) e.Cancel = true; };
        Controls.AddRange([notice, usage, policy, status, install, cancel]);
        AcceptButton = install;
        CancelButton = cancel;
    }

    public static int SmokeTest()
    {
        ApplicationConfiguration.Initialize();
        foreach (var choice in new[] { false, true })
        {
            bool? received = null;
            using var dialog = new PrivacyDialog(value =>
            {
                received = value;
                return Task.CompletedTask;
            });
            if (!dialog.usage.Checked) return 1;
            dialog.Shown += (_, _) =>
            {
                dialog.usage.Checked = choice;
                dialog.install.PerformClick();
            };
            if (dialog.ShowDialog() != DialogResult.OK || received != choice) return 1;
        }
        var called = false;
        using var cancelled = new PrivacyDialog(_ => { called = true; return Task.CompletedTask; });
        cancelled.Shown += (_, _) => cancelled.cancel.PerformClick();
        if (cancelled.ShowDialog() != DialogResult.Cancel || called) return 1;
        return 0;
    }
}
