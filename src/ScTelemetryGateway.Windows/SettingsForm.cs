using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 110 };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 300, Width = 110 };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "wss://telemetry.example.org/v1/telemetry/stream" };
    private readonly TextBox apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TreeView fields = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    private readonly TreeView mfdFields = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    private readonly CheckBox remoteEnabled = new() { Text = "Enable remote WSS upload", AutoSize = true };
    private readonly CheckBox mfdEnabled = new() { Text = "Enable local MFD access", AutoSize = true };
    private readonly TextBox mfdAddress = new() { Dock = DockStyle.Fill, PlaceholderText = "192.168.1.100" };
    private readonly NumericUpDown mfdPort = new() { Minimum = 1024, Maximum = 65535, Width = 110, Value = 50052 };
    private readonly Label mfdWarning = new()
    {
        Text = "Local HTTP/WebSocket access is unencrypted. Use only on a trusted network. " +
               "An on-path observer or active attacker can read telemetry or alter this page.",
        Dock = DockStyle.Fill, ForeColor = Color.DarkRed, AutoSize = false
    };
    private readonly Button pairingCode = new() { Text = "Create pairing code", AutoSize = true };
    private readonly Label pairingCodeValue = new() { AutoSize = true, Text = "" };
    private readonly ListBox pendingDevices = new() { Dock = DockStyle.Fill, Height = 70 };
    private readonly ListBox approvedDevices = new() { Dock = DockStyle.Fill, Height = 70 };
    private readonly Button approve = new() { Text = "Approve", AutoSize = true };
    private readonly Button deny = new() { Text = "Deny", AutoSize = true };
    private readonly Button revoke = new() { Text = "Revoke", AutoSize = true };
    private readonly GatewayRuntime? runtime;
    private readonly CheckBox automaticUpdates = new() { Text = "Check for updates automatically (GitHub)", AutoSize = true };
    private readonly CheckBox previewUpdates = new() { Text = "Include unsigned preview updates (not recommended)", AutoSize = true };
    private bool checking;
    public GatewayOptions Options { get; private set; }
    public string ApiKey => apiKey.Text;

    public SettingsForm(GatewayOptions options, string key, GatewayRuntime? runtime = null)
    {
        Options = options;
        this.runtime = runtime;
        Text = "Settings - " + TrayContext.ProductName;
        Size = new Size(900, 760);
        MinimumSize = new Size(700, 560);
        StartPosition = FormStartPosition.CenterScreen;
        port.Value = options.GamePort;
        interval.Value = options.IntervalSeconds;
        endpoint.Text = options.Endpoint;
        apiKey.Text = key;
        remoteEnabled.Checked = options.RemoteEnabled;
        mfdEnabled.Checked = options.MfdEnabled;
        mfdAddress.Text = options.MfdAddress;
        mfdPort.Value = options.MfdPort;
        automaticUpdates.Checked = options.AutomaticUpdateChecks;
        previewUpdates.Checked = options.IncludePreviewUpdates;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 17 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 17; i++) layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        void Row(string caption, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
        Row("Local game port", port, 0);
        layout.Controls.Add(remoteEnabled, 0, 1);
        layout.SetColumnSpan(remoteEnabled, 2);
        Row("Receiver WSS URL", endpoint, 2);
        Row("API key", apiKey, 3);
        Row("Interval (seconds)", interval, 4);
        layout.Controls.Add(mfdEnabled, 0, 5);
        layout.SetColumnSpan(mfdEnabled, 2);
        Row("MFD IPv4 address", mfdAddress, 6);
        Row("MFD port", mfdPort, 7);
        var warning = new Label
        {
            Text = "Only checked fields are collected and uploaded. Names, loadout, vitals and balance can be private.\n" +
                "Applying settings stops collection and clears queued data. Already sent data cannot be recalled.",
            Dock = DockStyle.Fill, AutoSize = false
        };
        layout.Controls.Add(warning, 0, 8);
        layout.SetColumnSpan(warning, 2);
        foreach (var category in FieldCatalog.All.Values.GroupBy(f => f.Path.Split('.')[0]).OrderBy(g => g.Key))
        {
            var node = new TreeNode(category.Key);
            foreach (var field in category.OrderBy(f => f.Path))
                node.Nodes.Add(new TreeNode(field.Path[(category.Key.Length + 1)..])
                    { Tag = field.Path, Checked = options.Fields.Contains(field.Path) });
            node.Checked = node.Nodes.Cast<TreeNode>().All(child => child.Checked);
            fields.Nodes.Add(node);
        }
        fields.AfterCheck += (_, e) =>
        {
            if (checking || e.Node is null) return;
            checking = true;
            try
            {
                if (e.Node.Parent is null)
                    foreach (TreeNode child in e.Node.Nodes) child.Checked = e.Node.Checked;
                else
                    e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(child => child.Checked);
            }
            finally { checking = false; }
        };
        layout.Controls.Add(fields, 0, 9);
        layout.SetColumnSpan(fields, 2);
        foreach (var category in FieldCatalog.All.Values.GroupBy(f => f.Path.Split('.')[0]).OrderBy(g => g.Key))
        {
            var node = new TreeNode(category.Key);
            foreach (var field in category.OrderBy(f => f.Path))
                node.Nodes.Add(new TreeNode(field.Path[(category.Key.Length + 1)..])
                    { Tag = field.Path, Checked = options.MfdFields.Contains(field.Path) });
            node.Checked = node.Nodes.Cast<TreeNode>().All(child => child.Checked);
            mfdFields.Nodes.Add(node);
        }
        mfdFields.AfterCheck += (_, e) =>
        {
            if (checking || e.Node is null) return;
            checking = true;
            try
            {
                if (e.Node.Parent is null)
                    foreach (TreeNode child in e.Node.Nodes) child.Checked = e.Node.Checked;
                else
                    e.Node.Parent.Checked = e.Node.Parent.Nodes.Cast<TreeNode>().All(child => child.Checked);
            }
            finally { checking = false; }
        };
        layout.Controls.Add(mfdFields, 0, 10);
        layout.SetColumnSpan(mfdFields, 2);
        layout.Controls.Add(mfdWarning, 0, 11);
        layout.SetColumnSpan(mfdWarning, 2);
        var pairing = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        pairing.Controls.Add(pairingCode);
        pairing.Controls.Add(pairingCodeValue);
        pairing.Controls.Add(approve);
        pairing.Controls.Add(deny);
        pairing.Controls.Add(revoke);
        layout.Controls.Add(pairing, 0, 12);
        layout.SetColumnSpan(pairing, 2);
        layout.Controls.Add(pendingDevices, 0, 13);
        layout.SetColumnSpan(pendingDevices, 2);
        layout.Controls.Add(approvedDevices, 0, 14);
        layout.SetColumnSpan(approvedDevices, 2);
        var updateOptions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        updateOptions.Controls.Add(automaticUpdates);
        updateOptions.Controls.Add(previewUpdates);
        layout.Controls.Add(updateOptions, 0, 15);
        layout.SetColumnSpan(updateOptions, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Apply", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                var next = new GatewayOptions
                {
                    GamePort = (int)port.Value, IntervalSeconds = (int)interval.Value,
                    Endpoint = endpoint.Text.Trim(), AutomaticUpdateChecks = automaticUpdates.Checked,
                    IncludePreviewUpdates = previewUpdates.Checked,
                    Fields = fields.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>())
                        .Where(n => n.Checked).Select(n => (string)n.Tag!).ToHashSet(StringComparer.Ordinal),
                    RemoteEnabled = remoteEnabled.Checked,
                    MfdEnabled = mfdEnabled.Checked,
                    MfdAddress = mfdAddress.Text.Trim(),
                    MfdPort = (int)mfdPort.Value,
                    MfdFields = mfdFields.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>())
                        .Where(n => n.Checked).Select(n => (string)n.Tag!).ToHashSet(StringComparer.Ordinal)
                };
                next.Validate(requireDestination: false);
                if (ApiKey.Any(char.IsControl)) throw new ArgumentException("API key cannot contain control characters.");
                Options = next;
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 16);
        layout.SetColumnSpan(buttons, 2);
        RefreshPairing();
        pairingCode.Click += (_, _) =>
        {
            if (runtime?.MfdServer is null)
            {
                MessageBox.Show(this, "Start the MFD listener before creating a pairing code.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            pairingCodeValue.Text = runtime.MfdServer.CreatePairingCode();
        };
        approve.Click += (_, _) =>
        {
            if (pendingDevices.SelectedItem is PairingItem item)
                runtime?.MfdServer?.ApproveRequest(item.Id);
            RefreshPairing();
        };
        deny.Click += (_, _) =>
        {
            if (pendingDevices.SelectedItem is PairingItem item)
                runtime?.MfdServer?.DenyRequest(item.Id);
            RefreshPairing();
        };
        revoke.Click += (_, _) =>
        {
            if (approvedDevices.SelectedItem is PairingItem item)
                runtime?.MfdServer?.RevokeDevice(item.Id);
            RefreshPairing();
        };
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;

        void RefreshPairing()
        {
            pendingDevices.Items.Clear();
            approvedDevices.Items.Clear();
            var status = runtime?.MfdServer?.Status;
            if (status is null) return;
            foreach (var request in status.PendingRequests)
                pendingDevices.Items.Add(new PairingItem(request.Id,
                    request.Label + " (expires " + request.ExpiresAt.ToLocalTime().ToString("HH:mm:ss") + ")"));
            foreach (var device in status.Devices)
                approvedDevices.Items.Add(new PairingItem(device.Id, device.Label));
        }
    }

    private sealed record PairingItem(Guid Id, string Text)
    {
        public override string ToString() => Text;
    }
}
