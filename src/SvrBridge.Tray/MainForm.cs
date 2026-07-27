using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class MainForm : Form
{
    private readonly Label _statusName = new();
    private readonly Label _statusDetail = new();
    private readonly Panel _statusPanel = new();
    private readonly Label _controllerFamily = new();
    private readonly Label _bindingDetail = new();
    private readonly DataGridView _shortcuts = new();
    private readonly TextBox _address = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _showPassword = new();
    private readonly CheckBox _startWhenOpened = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _add = new();
    private readonly Button _edit = new();
    private readonly Button _remove = new();
    private readonly Button _toggle = new();
    private readonly Button _test = new();
    private readonly Button _findActions = new();
    private readonly Button _setUpSteamVr = new();
    private readonly Button _changeBindings = new();
    private readonly Button _vrDashboard = new();
    private readonly TextBox _activity = new();
    private readonly List<ShortcutConfig> _shortcutItems = [];
    private IReadOnlyList<StreamerBotAction> _actions = [];
    private bool _allowClose;

    public event Action? StartRequested;
    public event Action? StopRequested;
    public event Action<ShortcutConfig>? TestRequested;
    public event Action? FindActionsRequested;
    public event Action? SteamVrSetupRequested;
    public event Action? BindingsRequested;
    public event Action? DashboardRequested;
    public event Action? LogsRequested;
    public event Action? ExitRequested;
    public event Func<Task<RecordedGesture?>>? RecordRequested;

    public MainForm()
    {
        Text = "SVR Bridge — VR shortcuts";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 700);
        Size = new Size(1040, 860);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 248, 250);
        ForeColor = Color.FromArgb(32, 37, 43);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        UpdateStatus(
            new BridgeStatus(
                BridgeState.Stopped,
                "Stopped",
                "Your VR shortcuts are not running."));
        FormClosing += OnFormClosing;
    }

    public UserSettings ReadSettings()
    {
        var first = _shortcutItems.FirstOrDefault();
        return new UserSettings
        {
            StreamerBotAddress = _address.Text,
            Password = _password.Text,
            ActionName = first?.ActionName ?? "",
            ActionId = first?.ActionId ?? "",
            GestureMode = first?.Gesture.Mode ?? ChordMode.Modifier,
            Shortcuts = _shortcutItems.ToArray(),
            StartBridgeWhenAppOpens = _startWhenOpened.Checked
        };
    }

    public void ApplySettings(UserSettings settings)
    {
        _address.Text = settings.StreamerBotAddress;
        _password.Text = settings.Password;
        _startWhenOpened.Checked = settings.StartBridgeWhenAppOpens;
        _shortcutItems.Clear();
        _shortcutItems.AddRange(settings.GetShortcuts());
        RefreshShortcutGrid();
    }

    public ShortcutConfig? SelectedShortcut =>
        _shortcuts.CurrentRow?.Tag as ShortcutConfig;

    public IReadOnlyList<StreamerBotAction> AvailableActions => _actions;

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

        _activity.AppendText(
            (_activity.TextLength == 0 ? "" : Environment.NewLine)
            + $"{DateTime.Now:HH:mm:ss}  {message}");
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
        _password.Enabled = !running;
        _add.Enabled = !running;
        _edit.Enabled = !running;
        _remove.Enabled = !running;
        _toggle.Enabled = !running;
        _findActions.Enabled = !running;
        _startWhenOpened.Enabled = !running;
    }

    public void UpdateControllerSetup(ControllerSetup setup)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateControllerSetup(setup));
            return;
        }

        var families = setup.Controllers
            .Select(controller => controller.FriendlyName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        _controllerFamily.Text = families.Length == 0
            ? "Waiting for active VR controllers"
            : string.Join(", ", families);
        _bindingDetail.Text = setup.Availability switch
        {
            BindingAvailability.Ready when setup.UsesValidatedVivePreset
                => "Validated Vive SteamVR binding is active.",
            BindingAvailability.Ready
                => "SteamVR controller binding is active.",
            BindingAvailability.NeedsSetup
                => "The SteamVR binding needs attention.",
            _ => "Turn on both controllers to inspect them."
        };
    }

    public void ShowSettingsError(string message) =>
        MessageBox.Show(
            this,
            message,
            "Check your shortcuts",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);

    public void ShowActions(IReadOnlyList<StreamerBotAction> actions)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowActions(actions));
            return;
        }

        _actions = actions;
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    private void BuildInterface()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var shortcutsTab = new TabPage("Shortcuts") { BackColor = BackColor };
        var settingsTab = new TabPage("Connection & setup") { BackColor = BackColor };
        var activityTab = new TabPage("Activity") { BackColor = BackColor };
        tabs.TabPages.AddRange([shortcutsTab, settingsTab, activityTab]);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(18)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateStatusCard(), 0, 1);
        root.Controls.Add(tabs, 0, 2);
        Controls.Add(root);

        shortcutsTab.Controls.Add(CreateShortcutsPage());
        settingsTab.Controls.Add(CreateSettingsPage());
        activityTab.Controls.Add(CreateActivityPage());
    }

    private Control CreateHeader()
    {
        var panel = new Panel { Height = 72, Dock = DockStyle.Top };
        panel.Controls.Add(new Label
        {
            Text = "SVR Bridge",
            Font = new Font(Font.FontFamily, 22, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 0)
        });
        panel.Controls.Add(new Label
        {
            Text = "Connect friendly controller shortcuts to Streamer.bot actions.",
            AutoSize = true,
            ForeColor = Color.FromArgb(92, 101, 112),
            Location = new Point(2, 42)
        });
        return panel;
    }

    private Control CreateStatusCard()
    {
        _statusPanel.Height = 72;
        _statusPanel.Dock = DockStyle.Top;
        _statusPanel.Padding = new Padding(16, 12, 16, 10);
        _statusName.AutoSize = true;
        _statusName.Font = new Font(Font, FontStyle.Bold);
        _statusDetail.AutoSize = true;
        _statusDetail.Location = new Point(16, 39);
        _statusPanel.Controls.AddRange([_statusName, _statusDetail]);
        return _statusPanel;
    }

    private Control CreateShortcutsPage()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var intro = new Label
        {
            Text = "Each row is one controller gesture and the Streamer.bot action it runs.",
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 12)
        };
        panel.Controls.Add(intro, 0, 0);

        _shortcuts.Dock = DockStyle.Fill;
        _shortcuts.ReadOnly = true;
        _shortcuts.AllowUserToAddRows = false;
        _shortcuts.AllowUserToDeleteRows = false;
        _shortcuts.AllowUserToResizeRows = false;
        _shortcuts.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _shortcuts.BackgroundColor = Color.White;
        _shortcuts.BorderStyle = BorderStyle.FixedSingle;
        _shortcuts.RowHeadersVisible = false;
        _shortcuts.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _shortcuts.MultiSelect = false;
        _shortcuts.Columns.Add("status", "On");
        _shortcuts.Columns.Add("name", "Shortcut name");
        _shortcuts.Columns.Add("gesture", "Controller inputs");
        _shortcuts.Columns.Add("action", "Runs in Streamer.bot");
        _shortcuts.Columns[0].FillWeight = 35;
        _shortcuts.Columns[1].FillWeight = 90;
        _shortcuts.Columns[2].FillWeight = 175;
        _shortcuts.Columns[3].FillWeight = 120;
        _shortcuts.CellDoubleClick += (_, _) => EditSelected();
        panel.Controls.Add(_shortcuts, 0, 1);

        var editRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 4)
        };
        ConfigureButton(_add, "Add shortcut", true);
        ConfigureButton(_edit, "Edit", false);
        ConfigureButton(_test, "Test action", false);
        ConfigureButton(_toggle, "Enable / disable", false);
        ConfigureButton(_remove, "Remove", false);
        _add.Click += (_, _) => AddShortcut();
        _edit.Click += (_, _) => EditSelected();
        _test.Click += (_, _) =>
        {
            if (SelectedShortcut is { } shortcut)
            {
                TestRequested?.Invoke(shortcut);
            }
        };
        _toggle.Click += (_, _) => ToggleSelected();
        _remove.Click += (_, _) => RemoveSelected();
        editRow.Controls.AddRange([_add, _edit, _test, _toggle, _remove]);
        panel.Controls.Add(editRow, 0, 2);

        var runRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        ConfigureButton(_start, "Save and start", true);
        ConfigureButton(_stop, "Stop", false);
        _stop.Enabled = false;
        _start.Click += (_, _) => StartRequested?.Invoke();
        _stop.Click += (_, _) => StopRequested?.Invoke();
        runRow.Controls.AddRange([_start, _stop]);
        panel.Controls.Add(runRow, 0, 3);
        return panel;
    }

    private Control CreateSettingsPage()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(14)
        };
        panel.Controls.Add(FieldLabel("Streamer.bot WebSocket address"));
        _address.Width = 620;
        _address.PlaceholderText = "ws://127.0.0.1:8080/1";
        panel.Controls.Add(_address);
        panel.Controls.Add(FieldLabel("Password (only if Streamer.bot requires one)"));
        _password.Width = 620;
        _password.UseSystemPasswordChar = true;
        panel.Controls.Add(_password);
        _showPassword.Text = "Show password";
        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;
        panel.Controls.Add(_showPassword);

        ConfigureButton(_findActions, "Refresh Streamer.bot actions", false);
        _findActions.Click += (_, _) => FindActionsRequested?.Invoke();
        panel.Controls.Add(_findActions);
        panel.Controls.Add(new Label
        {
            Text = "Controller",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 20, 0, 4)
        });
        _controllerFamily.AutoSize = true;
        _controllerFamily.Text = "Waiting for active VR controllers";
        _bindingDetail.AutoSize = true;
        _bindingDetail.ForeColor = Color.FromArgb(92, 101, 112);
        panel.Controls.Add(_controllerFamily);
        panel.Controls.Add(_bindingDetail);

        var setupRow = new FlowLayoutPanel { AutoSize = true };
        ConfigureButton(_setUpSteamVr, "Set up SteamVR", false);
        ConfigureButton(_changeBindings, "SteamVR input bindings", false);
        ConfigureButton(_vrDashboard, "Show in VR", true);
        _setUpSteamVr.Click += (_, _) => SteamVrSetupRequested?.Invoke();
        _changeBindings.Click += (_, _) => BindingsRequested?.Invoke();
        _vrDashboard.Click += (_, _) => DashboardRequested?.Invoke();
        setupRow.Controls.AddRange([_setUpSteamVr, _changeBindings, _vrDashboard]);
        panel.Controls.Add(setupRow);

        _startWhenOpened.Text = "Start my shortcuts when SVR Bridge opens";
        _startWhenOpened.AutoSize = true;
        _startWhenOpened.Margin = new Padding(0, 18, 0, 0);
        panel.Controls.Add(_startWhenOpened);
        return panel;
    }

    private Control CreateActivityPage()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        _activity.Dock = DockStyle.Fill;
        _activity.Multiline = true;
        _activity.ReadOnly = true;
        _activity.ScrollBars = ScrollBars.Vertical;
        _activity.BackColor = Color.White;
        panel.Controls.Add(_activity);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.LeftToRight
        };
        var logs = new Button();
        var exit = new Button();
        ConfigureButton(logs, "Open log folder", false);
        ConfigureButton(exit, "Exit SVR Bridge", false);
        logs.Click += (_, _) => LogsRequested?.Invoke();
        exit.Click += (_, _) => ExitRequested?.Invoke();
        footer.Controls.AddRange([logs, exit]);
        panel.Controls.Add(footer);
        return panel;
    }

    private void AddShortcut()
    {
        var draft = new ShortcutConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "New VR shortcut",
            ActionName = "",
            ActionId = null
        };
        using var editor = new ShortcutEditorForm(
            draft,
            _actions,
            InvokeRecordAsync,
            BindingsRequested);
        if (editor.ShowDialog(this) == DialogResult.OK)
        {
            _shortcutItems.Add(editor.Shortcut);
            RefreshShortcutGrid(editor.Shortcut.Id);
        }
    }

    private void EditSelected()
    {
        if (SelectedShortcut is not { } selected)
        {
            ShowSettingsError("Choose a shortcut first.");
            return;
        }

        using var editor = new ShortcutEditorForm(
            selected,
            _actions,
            InvokeRecordAsync,
            BindingsRequested);
        if (editor.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var index = _shortcutItems.FindIndex(item => item.Id == selected.Id);
        _shortcutItems[index] = editor.Shortcut;
        RefreshShortcutGrid(editor.Shortcut.Id);
    }

    private void ToggleSelected()
    {
        if (SelectedShortcut is not { } selected)
        {
            return;
        }

        var index = _shortcutItems.FindIndex(item => item.Id == selected.Id);
        _shortcutItems[index] = selected with { Enabled = !selected.Enabled };
        RefreshShortcutGrid(selected.Id);
    }

    private void RemoveSelected()
    {
        if (SelectedShortcut is not { } selected)
        {
            return;
        }

        if (MessageBox.Show(
                this,
                $"Remove “{selected.Name}”?",
                "Remove shortcut",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        _shortcutItems.RemoveAll(item => item.Id == selected.Id);
        RefreshShortcutGrid();
    }

    private void RefreshShortcutGrid(string? selectId = null)
    {
        _shortcuts.Rows.Clear();
        foreach (var shortcut in _shortcutItems)
        {
            var index = _shortcuts.Rows.Add(
                shortcut.Enabled ? "Yes" : "No",
                shortcut.Name,
                shortcut.FriendlyGesture,
                shortcut.ActionName);
            var row = _shortcuts.Rows[index];
            row.Tag = shortcut;
            if (!shortcut.Enabled)
            {
                row.DefaultCellStyle.ForeColor = Color.Gray;
            }

            if (shortcut.Id == selectId)
            {
                row.Selected = true;
                _shortcuts.CurrentCell = row.Cells[1];
            }
        }
    }

    private static Label FieldLabel(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 8, 0, 3)
        };

    private Task<RecordedGesture?> InvokeRecordAsync() =>
        RecordRequested?.Invoke()
        ?? Task.FromResult<RecordedGesture?>(null);

    private static void ConfigureButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(112, 38);
        button.Padding = new Padding(10, 2, 10, 2);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White;
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

internal sealed class ShortcutEditorForm : Form
{
    private readonly TextBox _name = new();
    private readonly ComboBox _mode = new();
    private readonly Label _gesture = new();
    private readonly ComboBox _action = new();
    private readonly Label _recordingStatus = new();
    private readonly Func<Task<RecordedGesture?>>? _record;
    private ControllerInputBinding _safety;
    private ControllerInputBinding _actionInput;
    private string? _actionId;

    public ShortcutEditorForm(
        ShortcutConfig shortcut,
        IReadOnlyList<StreamerBotAction> actions,
        Func<Task<RecordedGesture?>>? record,
        Action? openBindings)
    {
        _record = record;
        _safety = shortcut.SafetyInput;
        _actionInput = shortcut.ActionInput;
        _actionId = shortcut.ActionId;
        Text = shortcut.Name == "New VR shortcut" ? "Add shortcut" : "Edit shortcut";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(620, 590);
        MinimumSize = Size;
        MaximizeBox = false;
        MinimizeBox = false;
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 248, 250);

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(22)
        };
        panel.Controls.Add(Heading("Name"));
        _name.Width = 540;
        _name.Text = shortcut.Name;
        panel.Controls.Add(_name);
        panel.Controls.Add(Heading("Controller gesture"));
        _gesture.AutoSize = true;
        _gesture.MaximumSize = new Size(535, 0);
        _gesture.Font = new Font(Font, FontStyle.Bold);
        panel.Controls.Add(_gesture);
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.Width = 310;
        _mode.Items.AddRange(["Hold first, then press second", "Press both together"]);
        _mode.SelectedIndex = shortcut.Gesture.Mode == ChordMode.Modifier ? 0 : 1;
        _mode.SelectedIndexChanged += (_, _) => RefreshGesture();
        panel.Controls.Add(_mode);

        var inputRow = new FlowLayoutPanel { AutoSize = true };
        var recordButton = new Button();
        var bindingButton = new Button();
        ConfigureDialogButton(recordButton, "Record controller inputs", true);
        ConfigureDialogButton(bindingButton, "Use SteamVR bindings", false);
        recordButton.Click += async (_, _) => await RecordAsync(recordButton);
        bindingButton.Click += (_, _) =>
        {
            _safety = ControllerInputBinding.SteamVrSafety;
            _actionInput = ControllerInputBinding.SteamVrAction;
            RefreshGesture();
            openBindings?.Invoke();
        };
        inputRow.Controls.AddRange([recordButton, bindingButton]);
        panel.Controls.Add(inputRow);
        _recordingStatus.AutoSize = true;
        _recordingStatus.ForeColor = Color.FromArgb(92, 101, 112);
        panel.Controls.Add(_recordingStatus);

        panel.Controls.Add(Heading("Streamer.bot action"));
        _action.Width = 540;
        _action.DropDownStyle = ComboBoxStyle.DropDown;
        foreach (var action in actions)
        {
            _action.Items.Add(action);
        }

        var selectedAction = actions.FirstOrDefault(action =>
            action.Id.Equals(shortcut.ActionId, StringComparison.OrdinalIgnoreCase));
        _action.SelectedItem = selectedAction;
        _action.Text = selectedAction?.FriendlyName ?? shortcut.ActionName;
        _action.SelectedIndexChanged += (_, _) =>
        {
            if (_action.SelectedItem is StreamerBotAction selected)
            {
                _actionId = selected.Id;
            }
        };
        _action.TextChanged += (_, _) =>
        {
            if (_action.SelectedItem is not StreamerBotAction selected
                || selected.FriendlyName != _action.Text)
            {
                _actionId = null;
            }
        };
        panel.Controls.Add(_action);
        panel.Controls.Add(new Label
        {
            Text = actions.Count == 0
                ? "Type an action name, or use “Refresh Streamer.bot actions” first."
                : "Choose an action by its friendly name.",
            AutoSize = true,
            ForeColor = Color.FromArgb(92, 101, 112)
        });

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Margin = new Padding(0, 24, 0, 0)
        };
        var save = new Button();
        var cancel = new Button();
        ConfigureDialogButton(save, "Save shortcut", true);
        ConfigureDialogButton(cancel, "Cancel", false);
        save.Click += (_, _) => SaveShortcut(shortcut);
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([save, cancel]);
        panel.Controls.Add(buttons);
        Controls.Add(panel);
        AcceptButton = save;
        CancelButton = cancel;
        RefreshGesture();
    }

    public ShortcutConfig Shortcut { get; private set; } = new();

    private async Task RecordAsync(Button button)
    {
        if (_record is null)
        {
            return;
        }

        button.Enabled = false;
        _recordingStatus.Text =
            "In VR: release all buttons, then hold the first input and press the second.";
        try
        {
            var recorded = await _record();
            if (recorded is not null)
            {
                _safety = recorded.SafetyInput;
                _actionInput = recorded.ActionInput;
                _recordingStatus.Text = $"Recorded on {recorded.ControllerFamily}.";
                RefreshGesture();
            }
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private void SaveShortcut(ShortcutConfig original)
    {
        var actionName = _action.SelectedItem is StreamerBotAction selected
            && selected.Id == _actionId
                ? selected.Name
                : _action.Text.Trim();
        var candidate = original with
        {
            Name = _name.Text.Trim(),
            SafetyInput = _safety,
            ActionInput = _actionInput,
            Gesture = new ChordConfig
            {
                Mode = _mode.SelectedIndex == 1
                    ? ChordMode.Simultaneous
                    : ChordMode.Modifier,
                WindowMs = _mode.SelectedIndex == 1 ? 300 : 2000,
                CooldownMs = 250
            },
            ActionName = actionName,
            ActionId = _actionId
        };

        try
        {
            candidate.Validate();
        }
        catch (InvalidDataException exception)
        {
            MessageBox.Show(
                this,
                exception.Message,
                "Finish this shortcut",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Shortcut = candidate;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RefreshGesture()
    {
        _gesture.Text = _mode.SelectedIndex == 1
            ? $"Press {_safety.FriendlyName} and {_actionInput.FriendlyName} together"
            : $"Hold {_safety.FriendlyName}, then press {_actionInput.FriendlyName}";
    }

    private static Label Heading(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4)
        };

    private static void ConfigureDialogButton(Button button, string text, bool primary)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(135, 38);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(32, 37, 43);
        button.Margin = new Padding(0, 4, 10, 4);
    }
}
