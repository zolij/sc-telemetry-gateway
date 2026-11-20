using ScTelemetryGateway.Core;

namespace ScTelemetryGateway.Windows;

internal sealed class SettingsForm : Form
{
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 65535, Width = 110 };
    private readonly NumericUpDown interval = new() { Minimum = 1, Maximum = 300, Width = 110 };
    private readonly TextBox endpoint = new() { Dock = DockStyle.Fill, PlaceholderText = "wss://telemetry.example.org/v1/telemetry/stream" };
    private readonly TextBox apiKey = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly TreeView fields = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
    private bool checking;
    public GatewayOptions Options { get; private set; }
    public string ApiKey => apiKey.Text;

    public SettingsForm(GatewayOptions options, string key)
    {
        Options = options;
        Text = "Settings - " + TrayContext.ProductName;
        Size = new Size(800, 700);
        MinimumSize = new Size(640, 500);
        StartPosition = FormStartPosition.CenterScreen;
        port.Value = options.GamePort;
        interval.Value = options.IntervalSeconds;
        endpoint.Text = options.Endpoint;
        apiKey.Text = key;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        for (var i = 0; i < 4; i++) layout.RowStyles.Add(new(SizeType.Absolute, 36));
        layout.RowStyles.Add(new(SizeType.Absolute, 65));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        void Row(string caption, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
        Row("Local game port", port, 0);
        Row("Receiver WSS URL", endpoint, 1);
        Row("API key", apiKey, 2);
        Row("Interval (seconds)", interval, 3);
        var warning = new Label
        {
            Text = "Only checked fields are collected and uploaded. Names, loadout, vitals and balance can be private.\n" +
                "Applying settings stops collection and clears queued data. Already sent data cannot be recalled.",
            Dock = DockStyle.Fill, AutoSize = false
        };
        layout.Controls.Add(warning, 0, 4);
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
        layout.Controls.Add(fields, 0, 5);
        layout.SetColumnSpan(fields, 2);
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
                    Endpoint = endpoint.Text.Trim(),
                    Fields = fields.Nodes.Cast<TreeNode>().SelectMany(n => n.Nodes.Cast<TreeNode>())
                        .Where(n => n.Checked).Select(n => (string)n.Tag!).ToHashSet(StringComparer.Ordinal)
                };
                next.Validate(requireEndpoint: false);
                if (ApiKey.Any(char.IsControl)) throw new ArgumentException("API key cannot contain control characters.");
                Options = next;
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons, 0, 6);
        layout.SetColumnSpan(buttons, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
