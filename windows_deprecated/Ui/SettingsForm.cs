using AiLife.WindowsAgent.Config;

namespace AiLife.WindowsAgent.Ui;

public sealed class SettingsForm : Form
{
    private readonly string _configPath;
    private readonly TextBox _serverUrlBox;
    private readonly TextBox _deviceTokenBox;
    private readonly TextBox _deviceIdBox;
    private readonly TextBox _deviceNameBox;
    private readonly NumericUpDown _heartbeatBox;
    private readonly NumericUpDown _afkBox;
    private readonly ComboBox _titleModeBox;
    private readonly TextBox _queueDirectoryBox;
    private readonly NumericUpDown _maxQueueBox;
    private readonly CheckBox _enableLogBox;

    public SettingsForm(AgentConfig currentConfig, string configPath)
    {
        _configPath = configPath;
        ResultConfig = currentConfig;

        Text = "AI Life Windows Agent - 设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 470);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 11,
            Padding = new Padding(20),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        var configLabel = new Label
        {
            Text = $"配置文件: {_configPath}",
            AutoSize = false,
            Height = 32,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
        };
        root.Controls.Add(configLabel, 0, 0);
        root.SetColumnSpan(configLabel, 2);

        _serverUrlBox = AddTextRow(root, 1, "服务器地址", currentConfig.ServerUrl);
        _deviceTokenBox = AddTextRow(root, 2, "Device Token", currentConfig.DeviceToken);
        _deviceTokenBox.PasswordChar = '*';
        _deviceIdBox = AddTextRow(root, 3, "Device ID", currentConfig.DeviceId);
        _deviceNameBox = AddTextRow(root, 4, "设备名称", currentConfig.DeviceName);
        _heartbeatBox = AddNumberRow(root, 5, "上报间隔 (秒)", currentConfig.HeartbeatIntervalSeconds, 1, 3600);
        _afkBox = AddNumberRow(root, 6, "AFK 判定 (秒)", currentConfig.AfkThresholdSeconds, 5, 86400);

        AddLabel(root, 7, "窗口标题模式");
        _titleModeBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Dock = DockStyle.Left,
            Width = 180,
        };
        _titleModeBox.Items.AddRange(["hash", "none", "raw"]);
        _titleModeBox.SelectedItem = currentConfig.WindowTitleMode is "none" or "raw" ? currentConfig.WindowTitleMode : "hash";
        root.Controls.Add(_titleModeBox, 1, 7);

        AddLabel(root, 8, "队列目录");
        var queuePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        queuePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        queuePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _queueDirectoryBox = new TextBox { Text = currentConfig.QueueDirectory, Dock = DockStyle.Fill };
        var browseButton = new Button { Text = "浏览", Width = 72, Height = 28 };
        browseButton.Click += (_, _) => BrowseQueueDirectory();
        queuePanel.Controls.Add(_queueDirectoryBox, 0, 0);
        queuePanel.Controls.Add(browseButton, 1, 0);
        root.Controls.Add(queuePanel, 1, 8);

        _maxQueueBox = AddNumberRow(root, 9, "最大离线队列", currentConfig.MaxQueuedHeartbeats, 1, 100_000);

        _enableLogBox = new CheckBox
        {
            Text = "写入 agent.log",
            Checked = currentConfig.EnableLog,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(_enableLogBox, 1, 10);

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 8, 20, 16),
        };
        var saveButton = new Button { Text = "保存", Width = 88, Height = 32 };
        var cancelButton = new Button { Text = "取消", Width = 88, Height = 32, DialogResult = DialogResult.Cancel };
        saveButton.Click += OnSave;
        buttonPanel.Controls.Add(saveButton);
        buttonPanel.Controls.Add(cancelButton);

        layout.Controls.Add(root, 0, 0);
        layout.Controls.Add(buttonPanel, 0, 1);
        Controls.Add(layout);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    public AgentConfig ResultConfig { get; private set; }

    private static TextBox AddTextRow(TableLayoutPanel root, int row, string label, string value)
    {
        AddLabel(root, row, label);
        var box = new TextBox { Text = value, Dock = DockStyle.Fill };
        root.Controls.Add(box, 1, row);
        return box;
    }

    private static NumericUpDown AddNumberRow(TableLayoutPanel root, int row, string label, int value, int min, int max)
    {
        AddLabel(root, row, label);
        var box = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Min(max, Math.Max(min, value)),
            Dock = DockStyle.Left,
            Width = 120,
        };
        root.Controls.Add(box, 1, row);
        return box;
    }

    private static void AddLabel(TableLayoutPanel root, int row, string text)
    {
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.Controls.Add(
            new Label
            {
                Text = text,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
            },
            0,
            row);
    }

    private void BrowseQueueDirectory()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "选择离线队列目录",
            SelectedPath = Directory.Exists(_queueDirectoryBox.Text) ? _queueDirectoryBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _queueDirectoryBox.Text = dialog.SelectedPath;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        AgentConfig next;
        try
        {
            next = new AgentConfig(
                _serverUrlBox.Text.Trim(),
                _deviceTokenBox.Text.Trim(),
                _deviceIdBox.Text.Trim(),
                _deviceNameBox.Text.Trim(),
                (int)_heartbeatBox.Value,
                (int)_afkBox.Value,
                (_titleModeBox.SelectedItem as string) ?? "hash",
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(_queueDirectoryBox.Text.Trim())),
                (int)_maxQueueBox.Value,
                _enableLogBox.Checked);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            MessageBox.Show(this, ex.Message, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var error = AgentConfig.Validate(next);
        if (error is not null)
        {
            MessageBox.Show(this, error, "配置错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            AgentConfig.Save(_configPath, next);
            ResultConfig = next;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"无法写入配置文件: {ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

