using System.Globalization;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class MainForm : Form
{
    private readonly Label _statusName = new();
    private readonly Label _statusDetail = new();

    // A FlowLayoutPanel rather than a plain Panel: it needs to autosize to
    // its two stacked labels rather than carry a fixed height. See
    // CreateStatusCard's remarks.
    private readonly FlowLayoutPanel _statusPanel = new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink
    };
    private readonly Label _controllerFamily = new();
    private readonly Label _bindingDetail = new();
    private readonly DataGridView _shortcuts = new();
    private readonly TextBox _address = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _showPassword = new();
    private readonly CheckBox _eventStream = new();
    private readonly Label _eventStreamState = new();
    private readonly CheckBox _notifications = new();
    private readonly CheckBox _chat = new();
    private readonly ComboBox _chatAnchorMode = new();
    private readonly ComboBox _chatAnchorHand = new();
    private readonly ComboBox _notificationAnchorMode = new();
    private readonly ComboBox _notificationAnchorHand = new();
    private readonly TrackBar _chatOpacity = new();
    private readonly TrackBar _chatSizeScale = new();
    private readonly ComboBox _gazeSensitivity = new();
    private readonly CheckBox _chatGazeScale = new();
    private readonly TrackBar _notificationOpacity = new();
    private readonly TrackBar _notificationSizeScale = new();

    // §B3/§B4/§B5 of the Phase 7 plan - notification appearance.
    private readonly TextBox _notificationBackgroundColour = new();
    private readonly TextBox _notificationTextColour = new();
    private readonly TextBox _notificationAccentColour = new();
    private readonly Button _pickBackgroundColour = new();
    private readonly Button _pickTextColour = new();
    private readonly Button _pickAccentColour = new();
    private readonly NumericUpDown _notificationDurationSeconds = new();
    private readonly ComboBox _notificationTransition = new();
    private readonly ComboBox _notificationSlideEdge = new();
    private readonly TextBox _notificationTemplatePath = new();
    private readonly Button _browseTemplatePath = new();
    private readonly Button _clearTemplatePath = new();
    private readonly TrackBar _notificationBackgroundOpacity = new();
    private readonly NumericUpDown _notificationCornerRadius = new();
    private readonly NumericUpDown _notificationPanelWidth = new();
    private readonly NumericUpDown _notificationPanelHeight = new();

    // §B2 - direct event subscription. Opt-in, not "subscribe to
    // everything": Streamer.bot exposes no way to ask which events currently
    // have an enabled trigger (confirmed live - disabling every event in its
    // own Settings > Events panel did not stop this app receiving them), so
    // a broader default pulled in non-alert plumbing (OBS scene changes and
    // the like) indistinguishable from a real alert.
    //
    // The picker itself is NotificationEventPicker, which owns the enabled
    // set and the search. Two earlier versions that listed every available
    // event instead were both rejected live; see that type's own remarks for
    // why the list is inverted rather than merely tidied up.
    private readonly NotificationEventPicker _eventPicker = new();
    private readonly ComboBox _templateEventPicker = new();
    private readonly CheckBox _showTestEvents = new();
    private readonly Button _editEventTemplate = new();
    private readonly Dictionary<string, string> _eventTemplates =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Carried through untouched by <see cref="ReadSettings"/>, which builds a
    /// whole <see cref="UserSettings"/> from the form's controls - so a
    /// setting with no control here would be silently reset to its default by
    /// the next desktop save. The chat window's placement has no desktop
    /// control by design: it is chosen by dragging the window in the headset,
    /// and the reset back to the proven default lives on the VR settings page
    /// beside it, where a wearer who has put it somewhere unreachable can
    /// actually get at it.
    /// </summary>
    private OverlayPlacement _chatPlacement = OverlayPlacement.Default;

    /// <summary>The notification panel's hand-placed offset - see <see cref="_chatPlacement"/>'s own remarks; the same reasoning applies since §B1 of the Phase 7 plan.</summary>
    private OverlayPlacement _notificationPlacement = OverlayPlacement.Default;

    private bool _allowClose;
    private bool _applyingSettings;

    public event Action? SettingsChanged;
    public event Action<ShortcutConfig>? TestRequested;
    public event Action? FindActionsRequested;
    public event Action? NotificationEventsRefreshRequested;
    public event Action? SteamVrSetupRequested;
    public event Action? BindingsRequested;
    public event Action? DashboardRequested;
    public event Action? LogsRequested;
    public event Action? ExitRequested;
    public event Func<ChordMode, Task<RecordedGesture?>>? RecordRequested;

    public MainForm()
    {
        Text = "SteamVR2Bot — VR shortcuts";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 700);
        Size = new Size(1040, 860);
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.FromArgb(247, 248, 250);
        ForeColor = Color.FromArgb(32, 37, 43);
        AutoScaleMode = AutoScaleMode.Dpi;

        BuildInterface();
        _address.TextChanged += (_, _) => NotifySettingsChanged();
        _password.TextChanged += (_, _) => NotifySettingsChanged();
        UpdateStatus(
            new BridgeStatus(
                BridgeState.Starting,
                "Starting automatically…",
                "SteamVR2Bot runs whenever this app is open."));
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
            StartBridgeWhenAppOpens = true,
            EventStreamEnabled = _eventStream.Checked,
            NotificationsEnabled = _notifications.Checked,
            ChatEnabled = _chat.Checked,
            ChatAnchorMode = SelectedAnchorMode(_chatAnchorMode),
            ChatAnchorHand = SelectedAnchorHand(_chatAnchorHand),
            ChatPlacement = _chatPlacement,
            NotificationAnchorMode = SelectedAnchorMode(_notificationAnchorMode),
            NotificationAnchorHand = SelectedAnchorHand(_notificationAnchorHand),
            ChatOpacity = OpacityFromSlider(_chatOpacity),
            ChatSizeScale = SizeScaleFromSlider(_chatSizeScale),
            GazeSensitivity = (GazeSensitivity)Math.Clamp(_gazeSensitivity.SelectedIndex, 0, 2),
            ChatGazeScaleEnabled = _chatGazeScale.Checked,
            NotificationOpacity = OpacityFromSlider(_notificationOpacity),
            NotificationSizeScale = SizeScaleFromSlider(_notificationSizeScale),
            NotificationPlacement = _notificationPlacement,
            NotificationBackgroundColour = _notificationBackgroundColour.Text.Trim(),
            NotificationTextColour = _notificationTextColour.Text.Trim(),
            NotificationAccentColour = _notificationAccentColour.Text.Trim(),
            NotificationDefaultDurationMs = (int)(_notificationDurationSeconds.Value * 1000),
            NotificationTransitionKind = (NotificationTransition)Math.Max(0, _notificationTransition.SelectedIndex),
            NotificationSlideEdge = (NotificationSlideEdge)Math.Max(0, _notificationSlideEdge.SelectedIndex),
            NotificationTemplatePath = _notificationTemplatePath.Text.Trim(),
            NotificationBackgroundOpacity = _notificationBackgroundOpacity.Value / 100.0,
            NotificationCornerRadiusPixels = (double)_notificationCornerRadius.Value,
            NotificationPanelWidth = (int)_notificationPanelWidth.Value,
            NotificationPanelHeight = (int)_notificationPanelHeight.Value,
            EnabledEvents = _eventPicker.EnabledKeys,
            EventTemplates = new Dictionary<string, string>(_eventTemplates, StringComparer.OrdinalIgnoreCase),
            ShowTestEvents = _showTestEvents.Checked
        };
    }

    private static OverlayAnchorMode SelectedAnchorMode(ComboBox combo) =>
        combo.SelectedIndex == 1 ? OverlayAnchorMode.Head : OverlayAnchorMode.Controller;

    private static OverlayAnchorHand SelectedAnchorHand(ComboBox combo) =>
        combo.SelectedIndex == 1 ? OverlayAnchorHand.Right : OverlayAnchorHand.Left;

    private static void ApplyAnchorMode(ComboBox combo, OverlayAnchorMode mode) =>
        combo.SelectedIndex = mode == OverlayAnchorMode.Head ? 1 : 0;

    private static void ApplyAnchorHand(ComboBox combo, OverlayAnchorHand hand) =>
        combo.SelectedIndex = hand == OverlayAnchorHand.Right ? 1 : 0;

    // Opacity's usable range is 0.2-1.0 - below 0.2 a panel is not worth
    // showing at all - mapped onto the TrackBar's 0-100 integer range.
    private static double OpacityFromSlider(TrackBar slider) => 0.2 + (slider.Value / 100.0 * 0.8);

    private static void ApplyOpacityToSlider(TrackBar slider, double opacity) =>
        slider.Value = (int)Math.Round(Math.Clamp((opacity - 0.2) / 0.8, 0, 1) * 100);

    // Size scale's usable range is 0.5-2.0 - half to double today's hardcoded
    // widths - mapped the same way.
    private static double SizeScaleFromSlider(TrackBar slider) => 0.5 + (slider.Value / 100.0 * 1.5);

    private static void ApplySizeScaleToSlider(TrackBar slider, double scale) =>
        slider.Value = (int)Math.Round(Math.Clamp((scale - 0.5) / 1.5, 0, 1) * 100);

    public void ApplySettings(UserSettings settings)
    {
        _applyingSettings = true;
        try
        {
            _address.Text = settings.StreamerBotAddress;
            _password.Text = settings.Password;
            _eventStream.Checked = settings.EventStreamEnabled;
            _notifications.Checked = settings.NotificationsEnabled;
            _chat.Checked = settings.ChatEnabled;
            ApplyAnchorMode(_chatAnchorMode, settings.ChatAnchorMode);
            ApplyAnchorHand(_chatAnchorHand, settings.ChatAnchorHand);
            _chatPlacement = settings.ChatPlacement;
            ApplyAnchorMode(_notificationAnchorMode, settings.NotificationAnchorMode);
            ApplyAnchorHand(_notificationAnchorHand, settings.NotificationAnchorHand);
            ApplyOpacityToSlider(_chatOpacity, settings.ChatOpacity);
            ApplySizeScaleToSlider(_chatSizeScale, settings.ChatSizeScale);
            _gazeSensitivity.SelectedIndex = (int)settings.GazeSensitivity;
            _chatGazeScale.Checked = settings.ChatGazeScaleEnabled;
            ApplyOpacityToSlider(_notificationOpacity, settings.NotificationOpacity);
            ApplySizeScaleToSlider(_notificationSizeScale, settings.NotificationSizeScale);
            _notificationPlacement = settings.NotificationPlacement;
            _notificationBackgroundColour.Text = settings.NotificationBackgroundColour;
            _notificationTextColour.Text = settings.NotificationTextColour;
            _notificationAccentColour.Text = settings.NotificationAccentColour;
            _notificationDurationSeconds.Value = Math.Clamp(
                settings.NotificationDefaultDurationMs / 1000m,
                _notificationDurationSeconds.Minimum,
                _notificationDurationSeconds.Maximum);
            _notificationTransition.SelectedIndex = (int)settings.NotificationTransitionKind;
            _notificationSlideEdge.SelectedIndex = (int)settings.NotificationSlideEdge;
            RefreshSlideEdgeEnabled();
            _notificationTemplatePath.Text = settings.NotificationTemplatePath;
            _notificationBackgroundOpacity.Value = (int)Math.Round(
                Math.Clamp(settings.NotificationBackgroundOpacity, 0, 1) * 100);
            _notificationCornerRadius.Value = (decimal)Math.Clamp(
                settings.NotificationCornerRadiusPixels,
                (double)_notificationCornerRadius.Minimum,
                (double)_notificationCornerRadius.Maximum);
            // Through the appearance record's Safe* properties, so a settings
            // file written before the panel size existed shows the proven
            // default here rather than the zero it deserialised to.
            _notificationPanelWidth.Value = Math.Clamp(
                settings.NotificationAppearance.SafePanelWidth,
                _notificationPanelWidth.Minimum,
                _notificationPanelWidth.Maximum);
            _notificationPanelHeight.Value = Math.Clamp(
                settings.NotificationAppearance.SafePanelHeight,
                _notificationPanelHeight.Minimum,
                _notificationPanelHeight.Maximum);
            _showTestEvents.Checked = settings.ShowTestEvents;
            _eventPicker.SetEnabledKeys(settings.EnabledEvents);
            _eventTemplates.Clear();
            foreach (var (key, value) in settings.EventTemplates)
            {
                _eventTemplates[key] = value;
            }

            _shortcutItems.Clear();
            _shortcutItems.AddRange(settings.GetShortcuts());
            RefreshShortcutGrid();
        }
        finally
        {
            _applyingSettings = false;
        }
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

    /// <summary>
    /// Shows what the Streamer.bot event feed is doing. A null state means the
    /// feature is switched off, which is not the same as a feed that is trying
    /// and failing to connect.
    /// </summary>
    public void UpdateEventStreamState(StreamerBotStreamState? state)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateEventStreamState(state));
            return;
        }

        _eventStreamState.Text = state switch
        {
            StreamerBotStreamState.Connecting => "Connecting to Streamer.bot…",
            StreamerBotStreamState.Connected => "Listening for Streamer.bot broadcasts.",
            StreamerBotStreamState.Reconnecting =>
                "Streamer.bot is not answering; retrying in the background.",
            _ => "Not listening."
        };
    }

    public void SetRunning(bool running)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetRunning(running));
            return;
        }

        // The bridge is always active while the app is open. Controls remain
        // editable because changes are saved and applied automatically.
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
                => "Direct Vive button recording is available.",
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

    /// <summary>
    /// Hands a live <c>GetEvents</c> response to the Notifications section's
    /// picker and to the per-event template dropdown - §B2 of the Phase 7
    /// plan. Never hardcoded: every entry comes from what the connected
    /// Streamer.bot instance itself reported.
    /// <para>
    /// This can arrive well after <see cref="ApplySettings"/> has already run,
    /// so it deliberately only supplies the searchable catalog. The enabled
    /// set belongs to <see cref="NotificationEventPicker"/> and is not
    /// reconciled against the response - an event this instance no longer
    /// reports stays enabled, since it is far more likely to be a failed
    /// fetch or a momentarily unavailable integration than a decision the
    /// user made.
    /// </para>
    /// </summary>
    public void ShowNotificationEvents(IReadOnlyList<StreamerBotEventDescriptor> events)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ShowNotificationEvents(events));
            return;
        }

        _eventPicker.SetCatalog(events);

        _templateEventPicker.Items.Clear();
        foreach (var descriptor in events
                     .OrderBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(entry => entry.Type, StringComparer.OrdinalIgnoreCase))
        {
            _templateEventPicker.Items.Add(descriptor);
        }
    }

    private void RefreshSlideEdgeEnabled() =>
        _notificationSlideEdge.Enabled =
            (NotificationTransition)Math.Max(0, _notificationTransition.SelectedIndex)
            == NotificationTransition.Slide;

    /// <summary>Opens a colour picker seeded with <paramref name="target"/>'s current value, writing the chosen colour back as "#RRGGBB".</summary>
    private void PickColour(TextBox target)
    {
        using var dialog = new ColorDialog();
        if (TryParseHexColour(target.Text) is { } current)
        {
            dialog.Color = current;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        target.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        NotifySettingsChanged();
    }

    private static Color? TryParseHexColour(string hex)
    {
        var trimmed = hex.Trim();
        if (trimmed.Length != 7
            || trimmed[0] != '#'
            || !int.TryParse(
                trimmed.AsSpan(1),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        return Color.FromArgb((value >> 16) & 0xFF, (value >> 8) & 0xFF, value & 0xFF);
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

    // CreateHeader and CreateStatusCard used to be a fixed-Height Panel with
    // its labels manually placed at hardcoded Point offsets - tuned to fit a
    // 22pt title and a status line at 100% text size. Windows' "text size"
    // accessibility setting (distinct from, and invisible to, the per-monitor
    // DPI scaling AutoScaleMode.Dpi already handles) scales rendered text
    // without ever resizing the box it sits in, so on a system with that
    // setting raised the second line landed lower than the fixed box was
    // tall and overlapped whatever came next. A top-down AutoSize
    // FlowLayoutPanel - the same pattern Section() already uses successfully
    // below - sizes itself from its labels' actual rendered height instead
    // of a guess, so it grows with them no matter what set the text size.
    private Control CreateHeader()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top
        };
        panel.Controls.Add(new Label
        {
            Text = "SteamVR2Bot",
            Font = new Font(Font.FontFamily, 22, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0)
        });
        panel.Controls.Add(new Label
        {
            Text = "Connect friendly controller shortcuts to Streamer.bot actions.",
            AutoSize = true,
            ForeColor = Color.FromArgb(92, 101, 112),
            Margin = new Padding(2, 4, 0, 8)
        });
        return panel;
    }

    private Control CreateStatusCard()
    {
        _statusPanel.Dock = DockStyle.Top;
        _statusPanel.Padding = new Padding(16, 12, 16, 12);
        _statusName.AutoSize = true;
        _statusName.Font = new Font(Font, FontStyle.Bold);
        _statusName.Margin = new Padding(0);
        _statusDetail.AutoSize = true;
        _statusDetail.Margin = new Padding(0, 4, 0, 0);
        _statusPanel.Controls.AddRange([_statusName, _statusDetail]);
        return _statusPanel;
    }

    private Control CreateShortcutsPage()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            Padding = new Padding(12)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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

        return panel;
    }

    // --- Settings page layout ---
    // Every setting row puts its label in a column of the same width, so the
    // controls beside them line up down the page - three "Pick…" buttons in a
    // column of three previously landed in three different places without
    // this. That column used to be a hardcoded 300x26 pixels, tuned to fit
    // the longest label ("Background image (optional):") at 100% text size.
    // Windows' "text size" accessibility setting scales rendered text
    // without resizing anything laid out in fixed pixels and without
    // triggering the DPI-changed notification AutoScaleMode.Dpi reacts to,
    // so raising it clipped every label in the column - "WebSocket address:"
    // and friends were cut off mid-word. AlignSettingRowLabels measures each
    // RowLabel's own actual rendered size after the whole page is built and
    // gives all of them the widest one's width, so the column is always
    // exactly as wide as it needs to be for whatever text size is in effect,
    // and no wider.
    private const int LabelColumnHorizontalPadding = 10;
    private const int LabelColumnVerticalPadding = 8;

    /// <summary>The page's own left edge for section bodies, so every section indents identically.</summary>
    private const int SectionBodyIndent = 14;

    /// <summary>Marks a <see cref="SettingRow"/> label so <see cref="AlignSettingRowLabels"/> can find every one of them once the page is fully built.</summary>
    private sealed class RowLabel : Label;

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

        panel.Controls.Add(ConnectionSection());
        panel.Controls.Add(NotificationsSection());
        panel.Controls.Add(NotificationAppearanceSection());
        panel.Controls.Add(NotificationEventsSection());
        panel.Controls.Add(ChatSection());
        panel.Controls.Add(ControllerSection());

        panel.Controls.Add(new Label
        {
            Text = "SteamVR2Bot stays available in SteamVR and runs your shortcuts whenever this app "
                   + "is open. Changes save automatically.",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            ForeColor = MutedForeColour,
            Margin = new Padding(0, 18, 0, 0)
        });

        AlignSettingRowLabels(panel);
        return panel;
    }

    /// <summary>
    /// Gives every <see cref="RowLabel"/> on the settings page the same
    /// width and height, measured from each label's own <see
    /// cref="Control.PreferredSize"/> - i.e. its actual rendered text at
    /// whatever font, DPI and text-size scale is currently in effect -
    /// rather than a constant tuned for one of those. Must run after every
    /// section has already been added to <paramref name="root"/>: it walks
    /// the tree it is handed and does nothing for labels added later.
    /// </summary>
    private static void AlignSettingRowLabels(Control root)
    {
        var labels = FindRowLabels(root).ToList();
        if (labels.Count == 0)
        {
            return;
        }

        var columnWidth = labels.Max(label => label.PreferredSize.Width) + LabelColumnHorizontalPadding;
        var rowHeight = labels.Max(label => label.PreferredSize.Height) + LabelColumnVerticalPadding;
        foreach (var label in labels)
        {
            label.AutoSize = false;
            label.Size = new Size(columnWidth, rowHeight);
        }
    }

    private static IEnumerable<RowLabel> FindRowLabels(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is RowLabel rowLabel)
            {
                yield return rowLabel;
            }

            foreach (var nested in FindRowLabels(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Where and how to reach Streamer.bot, and whether to listen to it at all.</summary>
    private Control ConnectionSection()
    {
        _address.Width = 420;
        _address.PlaceholderText = "ws://127.0.0.1:8080/1";
        _password.Width = 420;
        _password.UseSystemPasswordChar = true;
        _showPassword.Text = "Show";
        _showPassword.AutoSize = true;
        _showPassword.CheckedChanged += (_, _) =>
            _password.UseSystemPasswordChar = !_showPassword.Checked;

        _eventStream.Text = "Listen for Streamer.bot chat and events";
        _eventStream.AutoSize = true;
        _eventStream.CheckedChanged += (_, _) => NotifySettingsChanged();
        _eventStreamState.AutoSize = true;
        _eventStreamState.ForeColor = MutedForeColour;
        _eventStreamState.Text = "Not listening.";

        return Section(
            "Streamer.bot connection",
            "Everything below needs this connection. Streamer.bot actions can also broadcast "
            + "straight to this app with the “WebsocketBroadcastJson” sub-action.",
            SettingRow("WebSocket address:", _address),
            SettingRow("Password (if required):", _password, _showPassword),
            Indented(_eventStream),
            Indented(_eventStreamState));
    }

    /// <summary>Whether headset notifications appear at all, and where.</summary>
    private Control NotificationsSection()
    {
        _notifications.Text = "Show notifications in the headset";
        _notifications.AutoSize = true;
        _notifications.CheckedChanged += (_, _) => NotifySettingsChanged();

        return Section(
            "Notifications",
            "A panel appears in VR for a few seconds. Position it by hand from the VR "
            + "dashboard's Notifications tab.",
            Indented(_notifications),
            AnchorRow("Anchor:", _notificationAnchorMode, _notificationAnchorHand),
            SliderRow("Opacity:", _notificationOpacity),
            SliderRow("Size:", _notificationSizeScale));
    }

    /// <summary>The wrist chat window and its gaze behaviour.</summary>
    private Control ChatSection()
    {
        _chat.Text = "Show chat messages on your wrist";
        _chat.AutoSize = true;
        _chat.CheckedChanged += (_, _) => NotifySettingsChanged();

        _gazeSensitivity.DropDownStyle = ComboBoxStyle.DropDownList;
        _gazeSensitivity.Items.AddRange(["Relaxed", "Normal", "Tight"]);
        _gazeSensitivity.Width = 130;
        _gazeSensitivity.SelectedIndexChanged += (_, _) => NotifySettingsChanged();

        // Off leaves the window at its configured size and opacity instead of
        // growing it on gaze. Gaze is still measured either way - it is what
        // decides whether the window accepts the laser pointer - so this is
        // purely about whether the window changes size while you read it.
        _chatGazeScale.Text = "Grow and brighten the window when you look at it";
        _chatGazeScale.AutoSize = true;
        _chatGazeScale.CheckedChanged += (_, _) => NotifySettingsChanged();

        return Section(
            "Chat window",
            "Your Twitch chat appears in a window on your wrist - no Streamer.bot action needed. "
            + "Grab it with the laser from the VR dashboard's Chat tab to place it.",
            Indented(_chat),
            AnchorRow("Anchor:", _chatAnchorMode, _chatAnchorHand),
            SliderRow("Opacity:", _chatOpacity),
            SliderRow("Size:", _chatSizeScale),
            SettingRow("Gaze sensitivity:", _gazeSensitivity),
            Indented(_chatGazeScale));
    }

    /// <summary>Controller status and the SteamVR repair/binding buttons.</summary>
    private Control ControllerSection()
    {
        _controllerFamily.AutoSize = true;
        _controllerFamily.Text = "Waiting for active VR controllers";
        _bindingDetail.AutoSize = true;
        _bindingDetail.ForeColor = MutedForeColour;

        ConfigureButton(_findActions, "Refresh Streamer.bot actions", false);
        _findActions.Click += (_, _) => FindActionsRequested?.Invoke();
        ConfigureButton(_setUpSteamVr, "Repair SteamVR setup", false);
        ConfigureButton(_changeBindings, "SteamVR input bindings", false);
        ConfigureButton(_vrDashboard, "Open SteamVR dashboard", true);
        _setUpSteamVr.Click += (_, _) => SteamVrSetupRequested?.Invoke();
        _changeBindings.Click += (_, _) => BindingsRequested?.Invoke();
        _vrDashboard.Click += (_, _) => DashboardRequested?.Invoke();

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 6, 0, 0) };
        buttons.Controls.AddRange([_findActions, _setUpSteamVr, _changeBindings, _vrDashboard]);

        return Section(
            "Controller and SteamVR",
            null,
            Indented(_controllerFamily),
            Indented(_bindingDetail),
            Indented(buttons));
    }

    /// <summary>The grey this page uses for every explanatory line, so "secondary text" is one decision rather than sixteen copies of an RGB triple.</summary>
    private static readonly Color MutedForeColour = Color.FromArgb(92, 101, 112);

    private static readonly Color SectionRuleColour = Color.FromArgb(226, 230, 235);

    /// <summary>
    /// One titled group of settings, with an optional line of explanation and
    /// a hairline above it. Sections exist because this page had grown to
    /// hold the connection, notifications, their appearance, the alert
    /// picker, chat and the controller in one undifferentiated column, where
    /// the only thing separating "background opacity" from "SteamVR input
    /// bindings" was how far you had scrolled.
    /// </summary>
    private Control Section(string title, string? description, params Control[] body)
    {
        var section = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 18)
        };

        section.Controls.Add(new Panel
        {
            Height = 1,
            Width = 660,
            BackColor = SectionRuleColour,
            Margin = new Padding(0, 0, 0, 10)
        });
        section.Controls.Add(new Label
        {
            Text = title,
            Font = new Font("Segoe UI", 11F, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, description is null ? 8 : 2)
        });
        if (description is not null)
        {
            section.Controls.Add(new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(640, 0),
                ForeColor = MutedForeColour,
                Margin = new Padding(0, 0, 0, 10)
            });
        }

        foreach (var control in body)
        {
            section.Controls.Add(control);
        }

        return section;
    }

    /// <summary>
    /// One setting: its label in a column shared with every other row, then
    /// its controls.
    /// <para>
    /// The shared column is the entire point. Every row used to size its own
    /// label, so the control after it began at whatever x that label happened
    /// to end at - which is why three colour rows put their three identical
    /// "Pick…" buttons in three different places. Nothing here is cleverer
    /// than giving them all the same column to start from; see
    /// <see cref="AlignSettingRowLabels"/> for how that column's width is
    /// actually decided.
    /// </para>
    /// </summary>
    private static Control SettingRow(string label, params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(SectionBodyIndent, 0, 0, 8)
        };
        row.Controls.Add(new RowLabel
        {
            Text = label,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 8, 0)
        });

        foreach (var control in controls)
        {
            control.Margin = new Padding(0, 0, 8, 0);
            row.Controls.Add(control);
        }

        return row;
    }

    /// <summary>A control that has no label column of its own - a checkbox, a status line - lined up with the labels above and below it.</summary>
    private static Control Indented(Control control)
    {
        control.Margin = new Padding(SectionBodyIndent, 0, 0, 8);
        return control;
    }

    /// <summary>A "Controller / Headset" mode combo plus a "Left / Right" hand combo, the latter only meaningful in Controller mode.</summary>
    private Control AnchorRow(string label, ComboBox modeCombo, ComboBox handCombo)
    {
        modeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        modeCombo.Items.AddRange(["Controller", "Headset"]);
        modeCombo.Width = 120;
        handCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        handCombo.Items.AddRange(["Left hand", "Right hand"]);
        handCombo.Width = 120;
        modeCombo.SelectedIndexChanged += (_, _) =>
        {
            handCombo.Enabled = modeCombo.SelectedIndex != 1;
            NotifySettingsChanged();
        };
        handCombo.SelectedIndexChanged += (_, _) => NotifySettingsChanged();
        return SettingRow(label, modeCombo, handCombo);
    }

    /// <summary>A single 0-100 TrackBar row for an opacity or size setting, labelled and change-notifying.</summary>
    private Control SliderRow(string label, TrackBar slider)
    {
        slider.Minimum = 0;
        slider.Maximum = 100;
        slider.TickFrequency = 10;
        slider.Width = 260;
        slider.ValueChanged += (_, _) => NotifySettingsChanged();
        return SettingRow(label, slider);
    }

    /// <summary>§B3/§B4/§B5 of the Phase 7 plan: colours, duration, transition and an optional PNG template.</summary>
    private Control NotificationAppearanceSection()
    {
        _notificationBackgroundOpacity.Minimum = 0;
        _notificationBackgroundOpacity.Maximum = 100;
        _notificationBackgroundOpacity.TickFrequency = 10;
        _notificationBackgroundOpacity.Width = 260;
        _notificationBackgroundOpacity.ValueChanged += (_, _) => NotifySettingsChanged();

        _notificationCornerRadius.Minimum = 0;
        _notificationCornerRadius.Maximum = 60;
        _notificationCornerRadius.Width = 80;
        _notificationCornerRadius.ValueChanged += (_, _) => NotifySettingsChanged();

        foreach (var size in (NumericUpDown[])[_notificationPanelWidth, _notificationPanelHeight])
        {
            size.Minimum = NotificationAppearanceSettings.MinimumPanelDimension;
            size.Maximum = NotificationAppearanceSettings.MaximumPanelDimension;
            size.Increment = 20;
            size.Width = 80;
            size.ValueChanged += (_, _) => NotifySettingsChanged();
        }

        _notificationDurationSeconds.Minimum = 0.5m;
        _notificationDurationSeconds.Maximum = 60m;
        _notificationDurationSeconds.Increment = 0.5m;
        _notificationDurationSeconds.DecimalPlaces = 1;
        _notificationDurationSeconds.Width = 80;
        _notificationDurationSeconds.ValueChanged += (_, _) => NotifySettingsChanged();

        _notificationTransition.DropDownStyle = ComboBoxStyle.DropDownList;
        _notificationTransition.Items.AddRange(["Fade", "Slide", "Scale pop"]);
        _notificationTransition.Width = 120;
        _notificationSlideEdge.DropDownStyle = ComboBoxStyle.DropDownList;
        _notificationSlideEdge.Items.AddRange(["Top", "Bottom", "Left", "Right"]);
        _notificationSlideEdge.Width = 100;
        _notificationTransition.SelectedIndexChanged += (_, _) =>
        {
            RefreshSlideEdgeEnabled();
            NotifySettingsChanged();
        };
        _notificationSlideEdge.SelectedIndexChanged += (_, _) => NotifySettingsChanged();

        _notificationTemplatePath.Width = 340;
        _notificationTemplatePath.TextChanged += (_, _) => NotifySettingsChanged();
        ConfigureButton(_browseTemplatePath, "Browse…", false);
        _browseTemplatePath.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "PNG images (*.png)|*.png",
                Title = "Choose a notification background image"
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _notificationTemplatePath.Text = dialog.FileName;
                NotifySettingsChanged();
            }
        };
        ConfigureButton(_clearTemplatePath, "Clear", false);
        _clearTemplatePath.Click += (_, _) =>
        {
            _notificationTemplatePath.Text = "";
            NotifySettingsChanged();
        };

        return Section(
            "Notification appearance",
            "A payload can override the accent colour, the duration and the image for its own "
            + "notification; these are the defaults for everything that does not.",
            ColourRow("Background colour:", _notificationBackgroundColour, _pickBackgroundColour),
            ColourRow("Text colour:", _notificationTextColour, _pickTextColour),
            ColourRow("Accent colour:", _notificationAccentColour, _pickAccentColour),
            SettingRow("Background opacity:", _notificationBackgroundOpacity),
            SettingRow(
                "Panel size (pixels):",
                _notificationPanelWidth,
                PanelSizeByLabel(),
                _notificationPanelHeight),
            SettingRow("Corner radius (pixels):", _notificationCornerRadius),
            SettingRow("Default duration (seconds):", _notificationDurationSeconds),
            SettingRow("Transition:", _notificationTransition, SlideFromLabel(), _notificationSlideEdge),
            SettingRow(
                "Background image (optional):",
                _notificationTemplatePath,
                _browseTemplatePath,
                _clearTemplatePath));
    }

    // Both of these get their Margin overwritten by SettingRow, which sets
    // the same right-hand gap on every control it lines up - only AutoSize
    // and TextAlign here are actually theirs.

    /// <summary>The inline second label on the transition row - "Slide from:" only qualifies the combo beside it, so it does not get a column of its own.</summary>
    private static Label SlideFromLabel() => new()
    {
        Text = "Slide from:",
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleLeft
    };

    /// <summary>The "x" between the panel's width and height - an inline separator, not a labelled setting of its own.</summary>
    private static Label PanelSizeByLabel() => new()
    {
        Text = "×",
        AutoSize = true,
        TextAlign = ContentAlignment.MiddleCenter
    };

    /// <summary>One "label + hex textbox + pick…" row, shared by the three notification colour settings.</summary>
    private Control ColourRow(string label, TextBox hexBox, Button pickButton)
    {
        hexBox.Width = 90;
        hexBox.PlaceholderText = "#RRGGBB";
        hexBox.TextChanged += (_, _) => NotifySettingsChanged();
        ConfigureButton(pickButton, "Pick…", false);
        pickButton.Click += (_, _) => PickColour(hexBox);

        return SettingRow(label, hexBox, pickButton);
    }

    /// <summary>§B2 of the Phase 7 plan: direct Streamer.bot event subscription, driven entirely by a live <c>GetEvents</c> response.</summary>
    private Control NotificationEventsSection()
    {
        _eventPicker.EnabledKeysChanged += NotifySettingsChanged;
        _eventPicker.RefreshRequested += () => NotificationEventsRefreshRequested?.Invoke();

        _templateEventPicker.DropDownStyle = ComboBoxStyle.DropDown;
        _templateEventPicker.Width = 240;
        _templateEventPicker.Format += (_, args) =>
        {
            if (args.ListItem is StreamerBotEventDescriptor descriptor)
            {
                args.Value = descriptor.Key;
            }
        };
        ConfigureButton(_editEventTemplate, "Edit template…", false);
        _editEventTemplate.Click += (_, _) =>
        {
            if (_templateEventPicker.SelectedItem is not StreamerBotEventDescriptor descriptor)
            {
                ShowSettingsError("Choose an event first (type to search).");
                return;
            }

            _eventTemplates.TryGetValue(descriptor.Key, out var existing);
            using var dialog = new EventTemplateForm(descriptor.Key, existing ?? "");
            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(dialog.Template))
            {
                _eventTemplates.Remove(descriptor.Key);
            }
            else
            {
                _eventTemplates[descriptor.Key] = dialog.Template.Trim();
            }

            NotifySettingsChanged();
        };

        _showTestEvents.Text = "Show test-fired events (Streamer.bot's own “Test” button)";
        _showTestEvents.AutoSize = true;
        _showTestEvents.Checked = true;
        _showTestEvents.CheckedChanged += (_, _) => NotifySettingsChanged();

        return Section(
            "Alerts",
            "Nothing is on until you add it here. Streamer.bot has no way to tell this app which "
            + "events you have already enabled on its side, so this list is its own switch, not a "
            + "mirror of Streamer.bot's Events panel.",
            Indented(_eventPicker),
            SettingRow("Customise wording for:", _templateEventPicker, _editEventTemplate),
            Indented(_showTestEvents));
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
        ConfigureButton(exit, "Exit SteamVR2Bot", false);
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
            NotifySettingsChanged();
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
        NotifySettingsChanged();
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
        NotifySettingsChanged();
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
        NotifySettingsChanged();
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

    private Task<RecordedGesture?> InvokeRecordAsync(ChordMode mode) =>
        RecordRequested?.Invoke(mode)
        ?? Task.FromResult<RecordedGesture?>(null);

    private void NotifySettingsChanged()
    {
        if (!_applyingSettings)
        {
            SettingsChanged?.Invoke();
        }
    }

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
    private readonly Label _holdDurationLabel = new();
    private readonly NumericUpDown _holdDuration = new();
    private readonly ComboBox _action = new();
    private readonly Label _recordingStatus = new();
    private readonly Func<ChordMode, Task<RecordedGesture?>>? _record;
    private ControllerInputBinding _safety;
    private ControllerInputBinding _actionInput;
    private string? _actionId;

    public ShortcutEditorForm(
        ShortcutConfig shortcut,
        IReadOnlyList<StreamerBotAction> actions,
        Func<ChordMode, Task<RecordedGesture?>>? record,
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
        _mode.Items.AddRange(
        [
            "Press one button",
            "Double press one button",
            "Hold one button",
            "Hold first, then press second",
            "Press both together"
        ]);
        _mode.SelectedIndex = shortcut.Gesture.Mode switch
        {
            ChordMode.SinglePress => 0,
            ChordMode.DoublePress => 1,
            ChordMode.LongPress => 2,
            ChordMode.Modifier => 3,
            _ => 4
        };
        _mode.SelectedIndexChanged += (_, _) =>
        {
            RefreshGesture();
            RefreshHoldDurationVisibility();
        };
        panel.Controls.Add(_mode);
        _holdDurationLabel.Text = "Hold duration (seconds)";
        _holdDurationLabel.AutoSize = true;
        _holdDurationLabel.Font = new Font(Font, FontStyle.Bold);
        _holdDuration.Minimum = 0.5m;
        _holdDuration.Maximum = 10m;
        _holdDuration.Increment = 0.5m;
        _holdDuration.DecimalPlaces = 1;
        _holdDuration.Width = 120;
        _holdDuration.Value = Math.Clamp(shortcut.Gesture.HoldMs / 1000m, 0.5m, 10m);
        _holdDuration.ValueChanged += (_, _) => RefreshGesture();
        panel.Controls.Add(_holdDurationLabel);
        panel.Controls.Add(_holdDuration);

        var inputRow = new FlowLayoutPanel { AutoSize = true };
        var recordButton = new Button();
        var bindingButton = new Button();
        ConfigureDialogButton(recordButton, "Record controller inputs", true);
        ConfigureDialogButton(bindingButton, "Use SteamVR bindings", false);
        recordButton.Click += async (_, _) => await RecordAsync(recordButton);
        bindingButton.Click += (_, _) =>
        {
            _safety = ControllerInputBinding.SteamVrSafety;
            _actionInput = SelectedMode
                is ChordMode.SinglePress
                or ChordMode.LongPress
                or ChordMode.DoublePress
                ? ControllerInputBinding.SteamVrSafety
                : ControllerInputBinding.SteamVrAction;
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
        // StreamerBotAction is a record, so without an explicit display
        // member WinForms renders its generated ToString() value (the full
        // Id/Name/Group record) instead of the friendly action label.
        _action.DisplayMember = nameof(StreamerBotAction.FriendlyName);
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
        RefreshHoldDurationVisibility();
    }

    public ShortcutConfig Shortcut { get; private set; } = new();

    private async Task RecordAsync(Button button)
    {
        if (_record is null)
        {
            return;
        }

        button.Enabled = false;
        _recordingStatus.Text = SelectedMode
            is ChordMode.SinglePress
            or ChordMode.LongPress
            or ChordMode.DoublePress
            ? "In VR: release all buttons, then press the button you want to use."
            : "In VR: release all buttons, then hold the first input and press the second.";
        try
        {
            var recorded = await _record(SelectedMode);
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
                Mode = SelectedMode,
                WindowMs = SelectedMode switch
                {
                    ChordMode.DoublePress => 500,
                    ChordMode.Simultaneous => 300,
                    _ => 2000
                },
                CooldownMs = 250,
                HoldMs = (int)(_holdDuration.Value * 1000)
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
        _gesture.Text = SelectedMode switch
        {
            ChordMode.SinglePress =>
                $"Press {_safety.FriendlyName}",
            ChordMode.DoublePress =>
                $"Double press {_safety.FriendlyName}",
            ChordMode.LongPress =>
                $"Hold {_safety.FriendlyName} for {_holdDuration.Value:0.#} seconds",
            ChordMode.Simultaneous =>
                $"Press {_safety.FriendlyName} and {_actionInput.FriendlyName} together",
            _ =>
                $"Hold {_safety.FriendlyName}, then press {_actionInput.FriendlyName}"
        };
    }

    private ChordMode SelectedMode => _mode.SelectedIndex switch
    {
        0 => ChordMode.SinglePress,
        1 => ChordMode.DoublePress,
        2 => ChordMode.LongPress,
        4 => ChordMode.Simultaneous,
        _ => ChordMode.Modifier
    };

    private void RefreshHoldDurationVisibility()
    {
        var visible = SelectedMode == ChordMode.LongPress;
        _holdDurationLabel.Visible = visible;
        _holdDuration.Visible = visible;
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

/// <summary>
/// A small modal for one event's template override - §B2 of the Phase 7
/// plan. Deliberately just a multiline textbox and the dotted-path
/// explanation: the resolver itself (<see cref="StreamerBotEventTemplate"/>)
/// is generic, so there is nothing per-event to validate here beyond letting
/// the wearer type a path they read off Streamer.bot's own event
/// documentation.
/// </summary>
internal sealed class EventTemplateForm : Form
{
    private readonly TextBox _template = new();

    public EventTemplateForm(string eventKey, string existingTemplate)
    {
        Text = $"Template for {eventKey}";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(600, 520);
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
            Padding = new Padding(22)
        };
        panel.Controls.Add(new Label
        {
            Text = "Use {dotted.path} to read a field from the event's own data, e.g. "
                   + "{targetUser.name} just followed! A missing or null field resolves to "
                   + "nothing rather than an error.\n\n"
                   + "List alternatives with | and the first one present wins; end with a "
                   + "\"quoted\" literal as a last resort:\n"
                   + "    {user.name|targetUser.name|\"Someone\"} just subscribed!\n\n"
                   + "{eventName} is the event's readable name (\"Gift Sub\"), {eventSource} "
                   + "its source, {event} both.\n\n"
                   + "To find an event's real field names, either read them from "
                   + "docs.streamer.bot/api/websocket/events, or turn the event on here and let "
                   + "it happen once - every event that arrives records its whole payload "
                   + "verbatim in the Activity log as streamerbot.event_payload.\n\n"
                   + "Leave blank to use the default: for events where the platform writes its "
                   + "own sentence (subs and gift subs do) that sentence is used as-is; "
                   + "otherwise it names whoever the event is about, then the event.",
            AutoSize = true,
            MaximumSize = new Size(520, 0),
            ForeColor = Color.FromArgb(92, 101, 112),
            Margin = new Padding(0, 0, 0, 10)
        });
        _template.Text = existingTemplate;
        _template.Width = 520;
        _template.Multiline = true;
        _template.Height = 70;
        panel.Controls.Add(_template);

        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        var save = new Button();
        var cancel = new Button();
        ConfigureButtonStatic(save, "Save", true);
        ConfigureButtonStatic(cancel, "Cancel", false);
        save.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([save, cancel]);
        panel.Controls.Add(buttons);
        Controls.Add(panel);
        AcceptButton = save;
        CancelButton = cancel;
    }

    public string Template => _template.Text;

    private static void ConfigureButtonStatic(Button button, string text, bool primary)
    {
        button.Text = text;
        button.AutoSize = true;
        button.MinimumSize = new Size(112, 38);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White;
        button.ForeColor = primary ? Color.White : Color.FromArgb(32, 37, 43);
        button.Margin = new Padding(0, 0, 10, 0);
    }
}
