using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class DashboardForm : Form
{
    private readonly Button start = new() { Text = "Start collection", AutoSize = true };
    private readonly Button pause = new() { Text = "Pause and clear buffer", AutoSize = true };
    private readonly Button settings = new() { Text = "Settings...", AutoSize = true };
    private readonly Button createCode = new() { Text = "Create pairing code", AutoSize = true };
    private readonly Button approve = new() { Text = "Approve", AutoSize = true };
    private readonly Button deny = new() { Text = "Deny", AutoSize = true };
    private readonly Button revoke = new() { Text = "Revoke", AutoSize = true };
    private readonly Label summary = new() { Dock = DockStyle.Fill, AutoSize = false };
    private readonly Label details = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly Label mfdGuidance = new() { Dock = DockStyle.Fill };
    private readonly Label code = new() { AutoSize = true, Anchor = AnchorStyles.Left, Text = "No pairing code" };
    private readonly TextBox url = new() { Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = "MFD address" };
    private readonly ListBox pending = new() { Dock = DockStyle.Fill, AccessibleName = "Pending pairing requests" };
    private readonly ListBox devices = new() { Dock = DockStyle.Fill, AccessibleName = "Approved devices" };
    private DateTimeOffset? codeExpires;
    private bool listenerActive;
    private bool busy;

    public event EventHandler? StartRequested;
    public event EventHandler? PauseRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? PairingCodeRequested;
    public event EventHandler? ApproveRequested;
    public event EventHandler? DenyRequested;
    public event EventHandler? RevokeRequested;
    public Guid? SelectedRequest => (pending.SelectedItem as DeviceItem)?.Id;
    public Guid? SelectedDevice => (devices.SelectedItem as DeviceItem)?.Id;

    public DashboardForm()
    {
        Text = TrayContext.ProductName;
        Size = new Size(950, 820);
        MinimumSize = new Size(760, 700);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 150));
        layout.RowStyles.Add(new(SizeType.Absolute, 32));
        var title = new Label { Text = "Local MFD dashboard", Dock = DockStyle.Fill, AutoSize = false };
        title.Font = new Font(title.Font.FontFamily, 17, FontStyle.Bold);
        layout.Controls.Add(title, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        actions.Controls.AddRange([start, pause, settings]);
        layout.Controls.Add(actions, 0, 1);
        var overview = new GroupBox { Text = "Collection and optional remote upload", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var overviewLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        overviewLayout.RowStyles.Add(new(SizeType.Absolute, 32));
        overviewLayout.RowStyles.Add(new(SizeType.Percent, 100));
        summary.Font = new Font(summary.Font, FontStyle.Bold);
        overviewLayout.Controls.Add(summary, 0, 0);
        var detailsPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        detailsPanel.Controls.Add(details);
        overviewLayout.Controls.Add(detailsPanel, 0, 1);
        overview.Controls.Add(overviewLayout);
        layout.Controls.Add(overview, 0, 3);

        var pairing = new GroupBox { Text = "Connect a local display", Dock = DockStyle.Fill, Padding = new Padding(12) };
        var pairLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7 };
        pairLayout.ColumnStyles.Add(new(SizeType.Percent, 50));
        pairLayout.ColumnStyles.Add(new(SizeType.Percent, 50));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 36));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 48));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 32));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 42));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 26));
        pairLayout.RowStyles.Add(new(SizeType.Percent, 100));
        pairLayout.RowStyles.Add(new(SizeType.Absolute, 42));
        var warning = new Label
        {
            Text = "Unencrypted HTTP/WS: network observers or attackers can read or change the page, pairing codes, credentials and telemetry. Use only on a trusted network.",
            Dock = DockStyle.Fill, ForeColor = Color.DarkRed
        };
        mfdGuidance.Font = new Font(mfdGuidance.Font, FontStyle.Bold);
        pairLayout.Controls.Add(mfdGuidance, 0, 0);
        pairLayout.SetColumnSpan(mfdGuidance, 2);
        pairLayout.Controls.Add(warning, 0, 1);
        pairLayout.SetColumnSpan(warning, 2);
        var address = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        address.ColumnStyles.Add(new(SizeType.Absolute, 100));
        address.ColumnStyles.Add(new(SizeType.Percent, 100));
        address.Controls.Add(new Label { Text = "Browser URL", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        address.Controls.Add(url, 1, 0);
        pairLayout.Controls.Add(address, 0, 2);
        pairLayout.SetColumnSpan(address, 2);
        var codeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        codeRow.Controls.Add(createCode);
        codeRow.Controls.Add(code);
        pairLayout.Controls.Add(codeRow, 0, 3);
        pairLayout.SetColumnSpan(codeRow, 2);
        pairLayout.Controls.Add(new Label { Text = "Pending requests", AutoSize = true }, 0, 4);
        pairLayout.Controls.Add(new Label { Text = "Approved devices", AutoSize = true }, 1, 4);
        pairLayout.Controls.Add(pending, 0, 5);
        pairLayout.Controls.Add(devices, 1, 5);
        var requestActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        requestActions.Controls.AddRange([approve, deny]);
        var deviceActions = new FlowLayoutPanel { Dock = DockStyle.Fill };
        deviceActions.Controls.Add(revoke);
        pairLayout.Controls.Add(requestActions, 0, 6);
        pairLayout.Controls.Add(deviceActions, 1, 6);
        pairing.Controls.Add(pairLayout);
        layout.Controls.Add(pairing, 0, 2);
        layout.Controls.Add(new Label { Text = "Closing this window keeps the application in the notification area.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        Controls.Add(layout);

        start.Click += (_, _) => StartRequested?.Invoke(this, EventArgs.Empty);
        pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        settings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        createCode.Click += (_, _) => PairingCodeRequested?.Invoke(this, EventArgs.Empty);
        approve.Click += (_, _) => ApproveRequested?.Invoke(this, EventArgs.Empty);
        deny.Click += (_, _) => DenyRequested?.Invoke(this, EventArgs.Empty);
        revoke.Click += (_, _) => RevokeRequested?.Invoke(this, EventArgs.Empty);
        pending.SelectedIndexChanged += (_, _) => RefreshButtons();
        devices.SelectedIndexChanged += (_, _) => RefreshButtons();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
    }

    public void ShowPairingCode(string value)
    {
        code.Text = value + " (one use; expires in 2 minutes)";
        codeExpires = DateTimeOffset.UtcNow.AddMinutes(2);
    }

    public void ClearPairingCode()
    {
        code.Text = "No pairing code";
        codeExpires = null;
    }

    public void UpdateView(bool running, bool busy, GatewayOptions options, GatewayStatus? status,
        MfdServerStatus? mfdStatus, string updateStatus, string usageStatus, string? error)
    {
        this.busy = busy;
        start.Enabled = !running && !busy;
        pause.Enabled = running && !busy;
        settings.Enabled = !busy;
        summary.Text = running ? "Collection running" : "Collection paused - no data is being collected.";
        details.Text = (error is null ? "" : "Error: " + error + "\n") +
            $"Game: {status?.Game ?? "Stopped"}    MFD: {status?.Mfd ?? "Stopped"}    MFD fields: {options.MfdFields.Count}\n" +
            $"Remote upload: {(options.RemoteConfigured ? status?.Delivery ?? "Stopped" : "Not configured")}    Remote fields: {options.Fields.Count}\n" +
            $"Queue: {status?.Queued ?? 0} batches / {status?.QueueBytes ?? 0} bytes    " +
            $"Dropped: {status?.BatchesDropped ?? 0} batches, {status?.EventsDropped ?? 0} events\n" +
            $"Last ACK (UTC): {status?.LastAcknowledged?.ToString("u") ?? "None"}    Updates: {updateStatus}\n" +
            $"Usage statistics: {usageStatus}";
        listenerActive = running && status?.Mfd == "Listener active" && mfdStatus?.Listening == true;
        mfdGuidance.Text = listenerActive ? "Open the URL on your display, then create a pairing code." :
            !options.MfdEnabled ? "Open Settings to configure and enable the local MFD." :
            options.MfdFields.Count == 0 ? "Select at least one MFD field in Settings." :
            !running ? "Start collection to connect a display." :
            "The local MFD is unavailable. Check collection status below.";
        url.Text = listenerActive ? mfdStatus!.Url ?? "" :
            !options.MfdEnabled ? "Enable local MFD access in Settings." :
            options.MfdFields.Count == 0 ? "Select MFD fields in Settings." :
            !running ? "Start collection to display the URL." :
            "MFD is not available yet. Check its status below.";
        if (!listenerActive || codeExpires <= DateTimeOffset.UtcNow) ClearPairingCode();
        UpdateItems(pending, listenerActive ? mfdStatus!.PendingRequests.Select(r => new DeviceItem(r.Id, r.Label)) : []);
        UpdateItems(devices, listenerActive ? mfdStatus!.Devices.Select(d => new DeviceItem(d.Id, d.Label)) : []);
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        createCode.Enabled = listenerActive && !busy;
        approve.Enabled = listenerActive && !busy && SelectedRequest.HasValue;
        deny.Enabled = listenerActive && !busy && SelectedRequest.HasValue;
        revoke.Enabled = listenerActive && !busy && SelectedDevice.HasValue;
    }

    private static void UpdateItems(ListBox list, IEnumerable<DeviceItem> entries)
    {
        var items = entries.OrderBy(item => item.Id).ToArray();
        if (list.Items.Cast<DeviceItem>().SequenceEqual(items)) return;
        var selected = (list.SelectedItem as DeviceItem)?.Id;
        list.BeginUpdate();
        try
        {
            list.Items.Clear();
            list.Items.AddRange(items);
            for (var i = 0; i < items.Length; i++)
                if (items[i].Id == selected) list.SelectedIndex = i;
        }
        finally { list.EndUpdate(); }
    }

    private sealed record DeviceItem(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }
}
