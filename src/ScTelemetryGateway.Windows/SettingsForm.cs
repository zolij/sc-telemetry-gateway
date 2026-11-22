using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 120 };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 300, Width = 120 };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "wss://receiver.example/telemetry" };
    private readonly TextBox apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TreeView fields = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    private readonly TreeView mfdFields = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    private readonly CheckBox remoteEnabled = new() { Text = "Enable remote upload", AutoSize = true };
    private readonly CheckBox mfdEnabled = new() { Text = "Enable local MFD access", AutoSize = true };
    private readonly ComboBox mfdAddress = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown mfdPort = new() { Minimum = 1024, Maximum = 65535, Width = 120 };
    private readonly CheckBox automaticUpdates = new() { Text = "Check for updates automatically (GitHub)", AutoSize = true };
    private readonly CheckBox previewUpdates = new() { Text = "Include unsigned preview updates (not recommended)", AutoSize = true };
    private readonly CheckBox usageStatistics = new() { Text = "Send application usage statistics", AutoSize = true };

    public GatewayOptions Options { get; private set; }
    public string ApiKey => apiKey.Text;

    public SettingsForm(GatewayOptions options, string key, bool collectionRunning = false)
    {
        Options = options;
        Text = "Settings - " + TrayContext.ProductName;
        Size = new Size(840, 700);
        MinimumSize = new Size(650, 540);
        StartPosition = FormStartPosition.CenterParent;
        port.Value = options.GamePort;
        interval.Value = options.IntervalSeconds;
        endpoint.Text = options.Endpoint;
        apiKey.Text = key;
        remoteEnabled.Checked = options.RemoteEnabled;
        mfdEnabled.Checked = options.MfdEnabled;
        mfdPort.Value = options.MfdPort;
        automaticUpdates.Checked = options.AutomaticUpdateChecks;
        previewUpdates.Checked = options.IncludePreviewUpdates;
        usageStatistics.Checked = options.UsageStatisticsEnabled;

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
                     .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses
                         .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                                     !IPAddress.IsLoopback(a.Address) && !a.Address.Equals(IPAddress.Any)))
                mfdAddress.Items.Add(new AddressItem(unicast.Address.ToString(), adapter.Name));
        if (options.MfdAddress.Length > 0 &&
            !mfdAddress.Items.Cast<AddressItem>().Any(item => item.Address == options.MfdAddress))
            mfdAddress.Items.Add(new AddressItem(options.MfdAddress, "Not currently available"));
        for (var i = 0; i < mfdAddress.Items.Count; i++)
            if (((AddressItem)mfdAddress.Items[i]!).Address == options.MfdAddress) mfdAddress.SelectedIndex = i;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        var connection = CreatePage("Game & receiver");
        AddText(connection, "Game connection", true);
        AddText(connection, "The game service is read-only and accessed on this PC. Collection starts only when you choose Start.");
        AddRow(connection, "Game port", port);
        AddText(connection, "Remote upload", true);
        connection.Controls.Add(remoteEnabled);
        AddText(connection, "Optional. Only fields selected on the Remote fields tab are uploaded.");
        AddRow(connection, "Receiver WSS URL", endpoint);
        AddRow(connection, "API key", apiKey);
        AddRow(connection, "Interval (seconds)", interval);
        tabs.TabPages.Add(Page("Game & receiver", connection));

        var local = CreatePage("Local MFD");
        AddText(local, "Local phone and tablet display", true);
        local.Controls.Add(mfdEnabled);
        AddText(local, "Choose the PC network interface and port. Select shared values separately on the MFD fields tab.");
        AddRow(local, "Local IPv4 interface", mfdAddress);
        AddRow(local, "MFD port", mfdPort);
        AddText(local, "WARNING: HTTP and WebSocket traffic are unencrypted. Anyone on the network may observe or modify the page, pairing codes, credentials and telemetry. Use only on a trusted network.");
        AddText(local, "Windows Firewall may require a rule you create yourself. Do not forward this port on your router.");
        tabs.TabPages.Add(Page("Local MFD", local));

        tabs.TabPages.Add(FieldPage("Remote fields", fields, options.Fields,
            "Only checked fields are sent to the remote receiver. This selection is independent of the MFD."));
        tabs.TabPages.Add(FieldPage("MFD fields", mfdFields, options.MfdFields,
            "Only checked fields are shared with approved local devices. This selection is independent of remote upload."));

        var updates = CreatePage("Updates");
        AddText(updates, "Application updates", true);
        updates.Controls.Add(automaticUpdates);
        updates.Controls.Add(previewUpdates);
        AddText(updates, "Updates are never installed without your approval.");
        tabs.TabPages.Add(Page("Updates", updates));

        var privacy = CreatePage("Privacy");
        AddText(privacy, "Usage statistics", true);
        privacy.Controls.Add(usageStatistics);
        AddText(privacy, "When enabled, the application sends a random installation ID, application version and whether collection is running to the project usage service hosted by Cloudflare. No game telemetry, receiver URL or API key is sent.");
        AddText(privacy, "Cloudflare can see network metadata including your IP address. Usage records become eligible for hourly cleanup after 30 days. Disabling deletes the local ID and stops further reports; previously sent records remain until cleanup.");
        tabs.TabPages.Add(Page("Privacy", privacy));

        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Apply settings", AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        save.Click += (_, _) =>
        {
            try
            {
                var next = new GatewayOptions
                {
                    GamePort = (int)port.Value, IntervalSeconds = (int)interval.Value,
                    Endpoint = endpoint.Text.Trim(), AutomaticUpdateChecks = automaticUpdates.Checked,
                    IncludePreviewUpdates = previewUpdates.Checked, Fields = Selected(fields),
                    UsageStatisticsEnabled = usageStatistics.Checked, UsageNoticeShown = options.UsageNoticeShown,
                    RemoteEnabled = remoteEnabled.Checked, MfdEnabled = mfdEnabled.Checked,
                    MfdAddress = (mfdAddress.SelectedItem as AddressItem)?.Address ?? "",
                    MfdPort = (int)mfdPort.Value, MfdFields = Selected(mfdFields)
                };
                next.Validate(requireDestination: false);
                if (ApiKey.Any(char.IsControl)) throw new ArgumentException("API key cannot contain control characters.");
                if (collectionRunning && MessageBox.Show(this,
                        "Applying settings stops collection, clears the remote queue, and disconnects local devices. Continue?",
                        Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                Options = next;
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        Controls.Add(tabs);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static FlowLayoutPanel CreatePage(string name)
    {
        var page = new FlowLayoutPanel
        {
            Name = name, Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown,
            WrapContents = false, Padding = new Padding(20)
        };
        page.SizeChanged += (_, _) =>
        {
            var width = Math.Max(200, page.ClientSize.Width - page.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 8);
            foreach (Control child in page.Controls)
            {
                if (child is TableLayoutPanel row) row.Width = width;
                else if (child is Label label) label.MaximumSize = new Size(width, 0);
            }
        };
        return page;
    }

    private static TabPage Page(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private static void AddText(FlowLayoutPanel page, string text, bool heading = false)
    {
        var label = new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(700, 0),
            Margin = new Padding(3, heading ? 12 : 5, 3, 10)
        };
        if (heading) label.Font = new Font(label.Font, FontStyle.Bold);
        page.Controls.Add(label);
    }

    private static void AddRow(FlowLayoutPanel page, string title, Control control)
    {
        var row = new TableLayoutPanel { Width = 680, Height = 38, ColumnCount = 2, Margin = new Padding(0, 3, 0, 3) };
        row.ColumnStyles.Add(new(SizeType.Absolute, 165));
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        row.Controls.Add(new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        row.Controls.Add(control, 1, 0);
        page.Controls.Add(row);
    }

    private static TabPage FieldPage(string title, TreeView tree, HashSet<string> selected, string description)
    {
        var page = new TabPage(title);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = description + "\nSelecting a group selects every field in it. Names and other values may be private.",
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8) }, 0, 0);
        var count = new Label { AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
        layout.Controls.Add(count, 0, 1);
        layout.Controls.Add(tree, 0, 2);
        page.Controls.Add(layout);

        foreach (var path in FieldCatalog.All.Keys.Order(StringComparer.Ordinal))
        {
            var nodes = tree.Nodes;
            TreeNode? current = null;
            foreach (var part in path.Split('.'))
            {
                current = nodes.Cast<TreeNode>().FirstOrDefault(n => n.Name == part);
                if (current is null)
                {
                    current = new TreeNode(part.Replace('_', ' ')) { Name = part };
                    nodes.Add(current);
                }
                nodes = current.Nodes;
            }
            current!.Tag = path;
            current.Checked = selected.Contains(path);
        }
        void SetParents(TreeNode node)
        {
            foreach (TreeNode child in node.Nodes) SetParents(child);
            if (node.Nodes.Count > 0) node.Checked = node.Nodes.Cast<TreeNode>().All(child => child.Checked);
        }
        foreach (TreeNode root in tree.Nodes) SetParents(root);
        void UpdateCount() => count.Text = $"{Selected(tree).Count} fields selected";
        UpdateCount();
        var checking = false;
        tree.AfterCheck += (_, e) =>
        {
            if (checking || e.Node is null) return;
            checking = true;
            try
            {
                void SetChildren(TreeNode node, bool value)
                {
                    foreach (TreeNode child in node.Nodes) { child.Checked = value; SetChildren(child, value); }
                }
                SetChildren(e.Node, e.Node.Checked);
                for (var parent = e.Node.Parent; parent is not null; parent = parent.Parent)
                    parent.Checked = parent.Nodes.Cast<TreeNode>().All(child => child.Checked);
                UpdateCount();
            }
            finally { checking = false; }
        };
        return page;
    }

    private static HashSet<string> Selected(TreeView tree)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        void Visit(TreeNode node)
        {
            if (node.Tag is string path && node.Checked) result.Add(path);
            foreach (TreeNode child in node.Nodes) Visit(child);
        }
        foreach (TreeNode root in tree.Nodes) Visit(root);
        return result;
    }

    private sealed record AddressItem(string Address, string InterfaceName)
    {
        public override string ToString() => Address + "  -  " + InterfaceName;
    }
}
