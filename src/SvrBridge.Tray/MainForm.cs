using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class MainForm : Form
{
    private readonly Label _statusName = new();
    private readonly Label _statusDetail = new();
    private readonly Panel _statusPanel = new();
    private readonly TextBox _address = new();
    private readonly ComboBox _action = new();
    private readonly Button _findActions = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _showPassword = new();
    private readonly CheckBox _startWhenOpened = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _test = new();
    private readonly Button _setUpSteamVr = new();
    private readonly TextBox _activity = new();
    private string _selectedActionId = "";
    private bool _applyingActionChoice;
    private bool _allowClose;

    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action? TestRequested;
    public event Action? FindActionsRequested;
    public event Action? SteamVrSetupRequested;
    public event Action? ExitRequested;

    public MainForm()
    {
        Text = "SVR Bridge";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(760, 720);
        Size = new Size(800, 900);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 248, 250);
        ForeColor = Color.FromArgb(32, 37, 43);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        UpdateStatus(
            new BridgeStatus(
                BridgeState.Stopped,
                "Stopped",
                "Your controller shortcut is not running."));

        FormClosing += OnFormClosing;
    }

    public UserSettings ReadSettings() =>
        new()
        {
            StreamerBotAddress = _address.Text,
            ActionName = _action.SelectedItem is StreamerBotAction selected
                         && selected.Id.Equals(
                             _selectedActionId,
                             StringComparison.OrdinalIgnoreCase)
                ? selected.Name
                : _action.Text,
            ActionId = _selectedActionId,
            Password = _password.Text,
            StartBridgeWhenAppOpens = _startWhenOpened.Checked
        };

    public void ApplySettings(UserSettings settings)
    {
        _address.Text = settings.StreamerBotAddress;
        SetActionChoice(settings.ActionName, settings.ActionId);
        _password.Text = settings.Password;
        _startWhenOpened.Checked = settings.StartBridgeWhenAppOpens;
    }

    public void UpdateStatus(BridgeStatus status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateStatus(status));
            return;
        }

        _statusName.Text = status.FriendlyName;
        _statusDetail.Text = status.Detail;
        _statusPanel.BackColor = status.State switch
        {
            BridgeState.Ready => Color.FromArgb(225, 246, 233),
            BridgeState.Starting or BridgeState.Sending => Color.FromArgb(231, 240, 255),
            BridgeState.Error => Color.FromArgb(255, 235, 235),
            _ => Color.FromArgb(236, 239, 243)
        };
    }

    public void AddActivity(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AddActivity(message));
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {message}";
        _activity.AppendText(
            (_activity.TextLength == 0 ? "" : Environment.NewLine) + line);
        _activity.SelectionStart = _activity.TextLength;
        _activity.ScrollToCaret();
    }

    public void SetRunning(bool running)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetRunning(running));
            return;
        }

        _start.Enabled = !running;
        _stop.Enabled = running;
        _address.Enabled = !running;
        _action.Enabled = !running;
        _findActions.Enabled = !running;
        _password.Enabled = !running;
        _showPassword.Enabled = !running;
        _startWhenOpened.Enabled = !running;
    }

    public void ShowSettingsError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Check your settings",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public void ShowActions(IReadOnlyList<StreamerBotAction> actions)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowActions(actions));
            return;
        }

        var currentName = _action.Text;
        var currentId = _selectedActionId;
        _applyingActionChoice = true;
        try
        {
            _action.Items.Clear();
            foreach (var action in actions)
            {
                _action.Items.Add(action);
            }

            var selected = actions.FirstOrDefault(action =>
                               action.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase))
                           ?? actions.FirstOrDefault(action =>
                               action.Name.Equals(currentName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                _action.SelectedItem = selected;
                _selectedActionId = selected.Id;
            }
            else
            {
                _action.Text = currentName;
                _selectedActionId = "";
            }
        }
        finally
        {
            _applyingActionChoice = false;
        }
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 8
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < root.RowCount; row++)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        root.Controls.Add(CreateHeader());
        root.Controls.Add(CreateStatusCard());
        root.Controls.Add(CreateShortcutCard());
        root.Controls.Add(CreateConnectionCard());
        root.Controls.Add(CreateActionCard());
        root.Controls.Add(CreateButtonRow());
        root.Controls.Add(CreateActivityCard());
        root.Controls.Add(CreateFooter());

        Controls.Add(root);
        AcceptButton = _start;
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Height = 78, Dock = DockStyle.Top };
        panel.Controls.Add(new Label
        {
            Text = "SVR Bridge",
            Font = new Font(Font.FontFamily, 22F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 2)
        });
        panel.Controls.Add(new Label
        {
            Text = "Run a Streamer.bot action from a safe VR controller shortcut.",
            ForeColor = Color.FromArgb(91, 99, 110),
            AutoSize = true,
            Location = new Point(2, 48)
        });
        return panel;
    }

    private Control CreateStatusCard()
    {
        _statusPanel.Height = 88;
        _statusPanel.Dock = DockStyle.Top;
        _statusPanel.Padding = new Padding(18, 14, 18, 12);
        _statusPanel.Margin = new Padding(0, 0, 0, 16);

        _statusName.Font = new Font(Font.FontFamily, 13F, FontStyle.Bold);
        _statusName.AutoSize = true;
        _statusName.Location = new Point(18, 14);

        _statusDetail.AutoSize = false;
        _statusDetail.Location = new Point(18, 45);
        _statusDetail.Size = new Size(640, 32);
        _statusDetail.ForeColor = Color.FromArgb(69, 76, 86);

        _statusPanel.Controls.Add(_statusName);
        _statusPanel.Controls.Add(_statusDetail);
        return _statusPanel;
    }

    private Control CreateShortcutCard()
    {
        var card = CreateCard("Controller shortcut");
        var shortcut = new Label
        {
            Text = "Hold Left Grip, then press Right Trigger",
            Font = new Font(Font.FontFamily, 12F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 48)
        };
        var note = new Label
        {
            Text = "The left grip acts as a safety button, so the action cannot run from the trigger alone.",
            ForeColor = Color.FromArgb(91, 99, 110),
            AutoSize = false,
            Size = new Size(640, 40),
            Location = new Point(18, 78)
        };
        card.Height = 130;
        card.Controls.Add(shortcut);
        card.Controls.Add(note);
        return card;
    }

    private Control CreateConnectionCard()
    {
        var card = CreateCard("Streamer.bot connection");
        card.Height = 180;

        ConfigureTextBox(_address, "ws://127.0.0.1:8080/1");
        AddField(
            card,
            "Streamer.bot address",
            "Usually ws://127.0.0.1:8080/1 when both apps are on this PC.",
            _address,
            48);

        ConfigureTextBox(_password, "");
        _password.Size = new Size(210, 28);
        _password.UseSystemPasswordChar = true;
        AddField(
            card,
            "Connection password (optional)",
            "Leave blank when authentication is disabled in Streamer.bot.",
            _password,
            112);

        _showPassword.Text = "Show password";
        _showPassword.AutoSize = true;
        _showPassword.Location = new Point(510, 111);
        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;
        card.Controls.Add(_showPassword);
        return card;
    }

    private Control CreateActionCard()
    {
        var card = CreateCard("Action to run");
        card.Height = 165;

        _action.DropDownStyle = ComboBoxStyle.DropDown;
        _action.FlatStyle = FlatStyle.Flat;
        _action.Size = new Size(250, 30);
        _action.DisplayMember = nameof(StreamerBotAction.FriendlyName);
        _action.SelectedIndexChanged += (_, _) =>
        {
            if (!_applyingActionChoice && _action.SelectedItem is StreamerBotAction selected)
            {
                _selectedActionId = selected.Id;
            }
        };
        _action.TextUpdate += (_, _) =>
        {
            if (!_applyingActionChoice)
            {
                _selectedActionId = "";
            }
        };
        AddField(
            card,
            "Streamer.bot action",
            "Choose a familiar name. SVR Bridge remembers its reliable internal link.",
            _action,
            48);

        ConfigureButton(_findActions, "Find actions", false);
        _findActions.Location = new Point(550, 44);
        _findActions.MinimumSize = new Size(105, 34);
        _findActions.Click += (_, _) => FindActionsRequested?.Invoke();
        card.Controls.Add(_findActions);

        _startWhenOpened.Text = "Start the controller shortcut when SVR Bridge opens";
        _startWhenOpened.AutoSize = true;
        _startWhenOpened.Location = new Point(18, 127);
        card.Controls.Add(_startWhenOpened);
        return card;
    }

    private Control CreateButtonRow()
    {
        var panel = new FlowLayoutPanel
        {
            Height = 98,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Padding = new Padding(0, 14, 0, 12),
            Margin = new Padding(0)
        };

        ConfigureButton(_start, "Save and Start", true);
        ConfigureButton(_stop, "Stop", false);
        ConfigureButton(_test, "Test Streamer.bot", false);
        ConfigureButton(_setUpSteamVr, "Set up SteamVR", false);

        _stop.Enabled = false;
        _start.Click += (_, _) => StartRequested?.Invoke();
        _stop.Click += (_, _) => StopRequested?.Invoke();
        _test.Click += (_, _) => TestRequested?.Invoke();
        _setUpSteamVr.Click += (_, _) => SteamVrSetupRequested?.Invoke();

        panel.Controls.AddRange([_start, _stop, _test, _setUpSteamVr]);
        return panel;
    }

    private Control CreateActivityCard()
    {
        var card = CreateCard("Recent activity");
        card.Height = 180;
        _activity.Location = new Point(18, 48);
        _activity.Size = new Size(650, 112);
        _activity.Multiline = true;
        _activity.ReadOnly = true;
        _activity.ScrollBars = ScrollBars.Vertical;
        _activity.BackColor = Color.White;
        _activity.BorderStyle = BorderStyle.FixedSingle;
        card.Controls.Add(_activity);
        return card;
    }

    private Control CreateFooter()
    {
        var panel = new Panel { Height = 56, Dock = DockStyle.Top };
        var hint = new Label
        {
            Text = "Closing this window keeps SVR Bridge in the notification area.",
            ForeColor = Color.FromArgb(91, 99, 110),
            AutoSize = true,
            Location = new Point(0, 18)
        };
        var exit = new LinkLabel
        {
            Text = "Exit SVR Bridge",
            AutoSize = true,
            Location = new Point(560, 18)
        };
        exit.LinkClicked += (_, _) => ExitRequested?.Invoke();
        panel.Controls.Add(hint);
        panel.Controls.Add(exit);
        return panel;
    }

    private Panel CreateCard(string title)
    {
        var card = new Panel
        {
            Dock = DockStyle.Top,
            BackColor = Color.White,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 0, 16)
        };
        card.Controls.Add(new Label
        {
            Text = title,
            Font = new Font(Font.FontFamily, 11F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 16)
        });
        return card;
    }

    private static void ConfigureTextBox(TextBox textBox, string placeholder)
    {
        textBox.PlaceholderText = placeholder;
        textBox.BorderStyle = BorderStyle.FixedSingle;
        textBox.Size = new Size(360, 28);
    }

    private static void AddField(
        Control parent,
        string label,
        string help,
        Control input,
        int top)
    {
        parent.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Location = new Point(18, top)
        });
        input.Location = new Point(285, top - 3);
        parent.Controls.Add(input);
        parent.Controls.Add(new Label
        {
            Text = help,
            AutoSize = false,
            ForeColor = Color.FromArgb(91, 99, 110),
            Location = new Point(18, top + 27),
            Size = new Size(630, 30)
        });
    }

    private void SetActionChoice(string actionName, string actionId)
    {
        _applyingActionChoice = true;
        try
        {
            _action.Items.Clear();
            if (!string.IsNullOrWhiteSpace(actionName))
            {
                var action = new StreamerBotAction(actionId, actionName, "");
                _action.Items.Add(action);
                _action.SelectedItem = action;
            }
            else
            {
                _action.Text = "";
            }

            _selectedActionId = actionId;
        }
        finally
        {
            _applyingActionChoice = false;
        }
    }

    private static void ConfigureButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(120, 38);
        button.Padding = new Padding(10, 2, 10, 2);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary
            ? Color.FromArgb(37, 99, 235)
            : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(32, 37, 43);
        button.Margin = new Padding(0, 0, 10, 8);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose || eventArgs.CloseReason == CloseReason.WindowsShutDown)
        {
            return;
        }

        eventArgs.Cancel = true;
        Hide();
    }
}
