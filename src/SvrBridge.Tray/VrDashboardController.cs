using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrDashboardController : IDisposable
{
    private readonly OpenVrInput _openVr;
    private readonly Action<ShortcutConfig> _shortcutSaved;
    private readonly Action<string> _shortcutDeleted;
    private readonly Action<VrSettingsSnapshot> _settingsChanged;
    private readonly Action<string> _log;
    private readonly List<ShortcutConfig> _shortcuts;
    private readonly IReadOnlyList<StreamerBotAction> _actions;
    private readonly VrActionBrowser _actionBrowser;
    private readonly VrDashboardScrollLimiter _scrollLimiter = new();
    private readonly OverlayTextureUploader _uploader;

    private DashboardPage _page;
    private ControllerSetup _setup = ControllerSetup.Unknown;
    private StreamerBotAction? _selectedAction;
    private ControllerInputBinding? _firstInput;
    private ControllerInputBinding? _secondInput;
    private ChordMode _gestureMode = ChordMode.SinglePress;
    private int _doublePressWindowMs = 500;
    private int _holdMs = 2000;
    private bool _recordingArmed;
    private string? _editingShortcutId;

    /// <summary>
    /// The settings snapshot currently shown on the Settings page, updated
    /// and re-reported on every applied change - see
    /// <see cref="ApplySettingsChange"/>. Unrelated to <see cref="_page"/>:
    /// tabs are peer navigation sitting above the wizard's page stack, not a
    /// page in it - see §"Key design decision" of the Phase 4b plan.
    /// </summary>
    private VrSettingsSnapshot _settings;

    public VrDashboardController(
        OpenVrInput openVr,
        IOverlayTextureSource textureSource,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate,
        VrSettingsSnapshot initialSettings,
        Action<ShortcutConfig> shortcutSaved,
        Action<string> shortcutDeleted,
        Action<VrSettingsSnapshot> settingsChanged,
        Action<string> log)
    {
        _openVr = openVr;
        _uploader = new OverlayTextureUploader(
            new DashboardUploadTarget(openVr),
            textureSource,
            "The SteamVR dashboard",
            log)
        {
            // A dashboard overlay handle accepts SetOverlayTexture and then
            // never shows the result - see OverlayTextureUploader.
            // TexturePathEnabled for the evidence. So the dashboard stays on
            // SetOverlayRaw, which works and blinks, while every regular
            // overlay keeps the blink-free texture path.
            TexturePathEnabled = false
        };
        _shortcuts = shortcuts.ToList();
        _actions = actions;
        _actionBrowser = new VrActionBrowser(actions);
        _settings = initialSettings;
        _shortcutSaved = shortcutSaved;
        _shortcutDeleted = shortcutDeleted;
        _settingsChanged = settingsChanged;
        _log = log;
        ShowList(activate, throwOnError: true);
    }

    public void Tick(InputSnapshot snapshot, ControllerSetup setup)
    {
        _setup = setup;
        if (_openVr.TryGetDashboardInteraction(out var interaction))
        {
            if (interaction.Kind == DashboardInteractionKind.Click)
            {
                HandleClick(interaction.X, interaction.Y);
            }
            else if (interaction.Kind == DashboardInteractionKind.Scroll)
            {
                HandleScroll(interaction.ScrollY);
            }
        }

        if (_page == DashboardPage.RecordInput)
        {
            CaptureRecordedInput(snapshot);
        }
    }

    private void HandleClick(float x, float y)
    {
        if (x is < 0 or > 1400 || y is < 0 or > 900)
        {
            return;
        }

        // Tabs are peer navigation between the two top-level pages
        // (Shortcuts and Settings), drawn only on those two - the five
        // wizard sub-pages never render a tab strip, and this band is
        // already dead space for every one of them, so checking it
        // unconditionally here cannot change their behaviour.
        if ((_page is DashboardPage.List or DashboardPage.Settings)
            && y >= VrDashboardLayout.TabStripY
            && y < VrDashboardLayout.TabStripY + VrDashboardLayout.TabStripHeight)
        {
            HandleTabClick(x);
            return;
        }

        switch (_page)
        {
            case DashboardPage.List:
                HandleListClick(x, y);
                break;
            case DashboardPage.GestureType:
                HandleGestureTypeClick(y);
                break;
            case DashboardPage.Tolerance:
                HandleToleranceClick(x, y);
                break;
            case DashboardPage.ActionPicker:
                HandleActionPickerClick(x, y);
                break;
            case DashboardPage.RecordInput:
                HandleRecordInputClick(x, y);
                break;
            case DashboardPage.Review:
                HandleReviewClick(x, y);
                break;
            case DashboardPage.Settings:
                HandleSettingsClick(x, y);
                break;
        }
    }

    private void HandleTabClick(float x)
    {
        switch (VrDashboardLayout.IndexAt(VrDashboardLayout.Tabs, x))
        {
            case 0:
                ShowList();
                break;
            default:
                ShowSettings();
                break;
        }
    }

    private void HandleListClick(float x, float y)
    {
        if (y >= 780)
        {
            BeginCreate();
            return;
        }

        if (y < VrDashboardLayout.ListRowsStartY)
        {
            return;
        }

        var index = (int)((y - VrDashboardLayout.ListRowsStartY) / VrDashboardLayout.ListRowHeight);
        var rowY = VrDashboardLayout.ListRowsStartY + (index * VrDashboardLayout.ListRowHeight);
        if (index < 0
            || index >= Math.Min(VrDashboardLayout.ListVisibleRowCount, _shortcuts.Count)
            || y >= rowY + 92)
        {
            return;
        }

        var shortcut = _shortcuts[index];
        if (x >= 1210)
        {
            _shortcuts.RemoveAt(index);
            _shortcutDeleted(shortcut.Id);
            ShowList();
        }
        else if (x >= 1090)
        {
            BeginEdit(shortcut);
        }
    }

    private void HandleGestureTypeClick(float y)
    {
        if (y >= 780)
        {
            ShowList();
            return;
        }

        if (y < 180)
        {
            return;
        }

        var index = (int)((y - 180) / 135);
        switch (index)
        {
            case 0:
                _gestureMode = ChordMode.SinglePress;
                StartRecording();
                break;
            case 1:
                _gestureMode = ChordMode.Simultaneous;
                StartRecording();
                break;
            case 2:
                _gestureMode = ChordMode.DoublePress;
                ShowTolerance();
                break;
            case 3:
                _gestureMode = ChordMode.LongPress;
                ShowTolerance();
                break;
        }
    }

    private void HandleToleranceClick(float x, float y)
    {
        if (y >= 780)
        {
            switch (VrDashboardLayout.IndexAt(VrDashboardLayout.Tolerance, x))
            {
                case 0:
                    ShowList();
                    break;
                case 1:
                    ShowGestureTypes();
                    break;
                default:
                    StartRecording();
                    break;
            }

            return;
        }

        if (y is < 300 or > 520 || x is < 180 or > 1220)
        {
            return;
        }

        var ratio = Math.Clamp((x - 180f) / 1040f, 0f, 1f);
        if (_gestureMode == ChordMode.DoublePress)
        {
            var nextValue =
                (int)Math.Round((200 + (ratio * 1000)) / 100d) * 100;
            if (nextValue == _doublePressWindowMs)
            {
                return;
            }

            _doublePressWindowMs = nextValue;
        }
        else
        {
            var nextValue =
                (int)Math.Round((500 + (ratio * 4500)) / 250d) * 250;
            if (nextValue == _holdMs)
            {
                return;
            }

            _holdMs = nextValue;
        }

        ShowTolerance();
    }

    private void HandleActionPickerClick(float x, float y)
    {
        if (y >= 780)
        {
            switch (VrDashboardLayout.IndexAt(VrDashboardLayout.ActionPicker, x))
            {
                case 0:
                    ShowList();
                    break;
                case 1:
                    if (_actionBrowser.BackToGroups())
                    {
                        ShowActionPicker();
                    }
                    else
                    {
                        ShowReview();
                    }

                    break;
                case 2:
                    if (_actionBrowser.ScrollPage(-1))
                    {
                        ShowActionPicker();
                    }

                    break;
                default:
                    if (_actionBrowser.ScrollPage(1))
                    {
                        ShowActionPicker();
                    }

                    break;
            }

            return;
        }

        if (y < 165)
        {
            return;
        }

        var index = (int)((y - 165) / 91);
        var wasShowingGroups = _actionBrowser.IsShowingGroups;
        var action = _actionBrowser.OpenRow(index);
        if (wasShowingGroups && !_actionBrowser.IsShowingGroups)
        {
            ShowActionPicker();
        }
        else if (action is not null)
        {
            _selectedAction = action;
            ShowReview();
        }
    }

    private void HandleReviewClick(float x, float y)
    {
        if (y < 780)
        {
            return;
        }

        switch (VrDashboardLayout.IndexAt(VrDashboardLayout.Review, x))
        {
            case 0:
                ShowList();
                break;
            case 1:
                StartRecording();
                break;
            case 2:
                _actionBrowser.Reset();
                ShowActionPicker();
                break;
            default:
                if (_selectedAction is not null)
                {
                    SaveShortcut();
                }

                break;
        }
    }

    private void HandleRecordInputClick(float x, float y)
    {
        if (y >= 780)
        {
            if (VrDashboardLayout.IndexAt(VrDashboardLayout.RecordInput, x) == 0)
            {
                ShowList();
            }
            else
            {
                ShowPreviousSetupPage();
            }

            return;
        }

        if (y < 180)
        {
            return;
        }

        var index = (int)((y - 180) / 88);
        var rowY = 180 + (index * 88);
        if (index < 0 || index >= 6 || y >= rowY + 76)
        {
            return;
        }

        ControllerHand hand;
        if (x is >= 60 and <= 680)
        {
            hand = ControllerHand.Left;
        }
        else if (x is >= 720 and <= 1340)
        {
            hand = ControllerHand.Right;
        }
        else
        {
            return;
        }

        var options = ControllerInputs.AvailableInputs(hand, _setup);
        if (index >= options.Count)
        {
            return;
        }

        // The trigger used to click this dashboard row may itself be one of
        // the physical inputs. Re-arm live capture only after that click is
        // released so it cannot become the second half of a combo.
        _recordingArmed = false;
        SelectRecordedInput(options[index]);
    }

    private void HandleScroll(float deltaY)
    {
        if (_page != DashboardPage.ActionPicker
            || deltaY == 0
            || !_scrollLimiter.TryAccept(Environment.TickCount64))
        {
            return;
        }

        if (_actionBrowser.ScrollPage(deltaY > 0 ? -1 : 1))
        {
            ShowActionPicker();
        }
    }

    private void BeginCreate()
    {
        _editingShortcutId = null;
        _selectedAction = null;
        _doublePressWindowMs = 500;
        _holdMs = 2000;
        ShowGestureTypes();
    }

    private void BeginEdit(ShortcutConfig shortcut)
    {
        _editingShortcutId = shortcut.Id;
        _selectedAction = _actions.FirstOrDefault(action =>
                              action.Id.Equals(
                                  shortcut.ActionId,
                                  StringComparison.OrdinalIgnoreCase))
                          ?? new StreamerBotAction(
                              shortcut.ActionId ?? "",
                              shortcut.ActionName,
                              "Saved action");
        _gestureMode = shortcut.Gesture.Mode;
        _doublePressWindowMs = shortcut.Gesture.WindowMs;
        _holdMs = shortcut.Gesture.HoldMs;
        ShowGestureTypes();
    }

    private void StartRecording()
    {
        _firstInput = null;
        _secondInput = null;
        _recordingArmed = false;
        ShowRecording();
    }

    private void CaptureRecordedInput(InputSnapshot snapshot)
    {
        var pressed = ControllerInputs.PressedInputs(snapshot, _setup);
        if (!_recordingArmed)
        {
            if (pressed.Count == 0)
            {
                _recordingArmed = true;
                ShowRecording();
            }

            return;
        }

        if (_firstInput is null)
        {
            var input = pressed.FirstOrDefault();
            if (input is null)
            {
                return;
            }

            var restoreDashboard = !_openVr.IsDashboardActive;
            SelectRecordedInput(input, restoreDashboard);
            if (_page != DashboardPage.RecordInput)
            {
                return;
            }

            foreach (var second in pressed.Where(candidate =>
                         !candidate.Id.Equals(
                             input.Id,
                             StringComparison.OrdinalIgnoreCase)))
            {
                SelectRecordedInput(second, restoreDashboard);
                break;
            }

            return;
        }

        if (_gestureMode == ChordMode.Simultaneous)
        {
            var second = pressed.FirstOrDefault(candidate =>
                !candidate.Id.Equals(
                    _firstInput.Id,
                    StringComparison.OrdinalIgnoreCase));
            if (second is not null)
            {
                SelectRecordedInput(second, !_openVr.IsDashboardActive);
            }
        }
    }

    private void SelectRecordedInput(
        ControllerInputBinding input,
        bool activateReview = false)
    {
        if (_gestureMode != ChordMode.Simultaneous)
        {
            _firstInput = input;
            _secondInput = input;
            ShowReview(activateReview);
            return;
        }

        if (_firstInput is null)
        {
            _firstInput = input;
            ShowRecording();
            return;
        }

        if (_firstInput.Id.Equals(input.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _secondInput = input;
        ShowReview(activateReview);
    }

    private void SaveShortcut()
    {
        if (_selectedAction is null
            || _firstInput is null
            || _secondInput is null)
        {
            return;
        }

        var shortcut = new ShortcutConfig
        {
            Id = _editingShortcutId ?? Guid.NewGuid().ToString("N"),
            Name = _selectedAction.Name,
            Enabled = true,
            SafetyInput = _firstInput,
            ActionInput = _secondInput,
            Gesture = new ChordConfig
            {
                Mode = _gestureMode,
                WindowMs = _gestureMode switch
                {
                    ChordMode.DoublePress => _doublePressWindowMs,
                    ChordMode.Simultaneous => 300,
                    _ => 2000
                },
                CooldownMs = 250,
                HoldMs = _gestureMode == ChordMode.LongPress ? _holdMs : 1000
            },
            ActionName = _selectedAction.Name,
            ActionId = _selectedAction.Id
        };

        var existingIndex = _shortcuts.FindIndex(item => item.Id == shortcut.Id);
        if (existingIndex >= 0)
        {
            _shortcuts[existingIndex] = shortcut;
        }
        else
        {
            _shortcuts.Add(shortcut);
        }

        _shortcutSaved(shortcut);
        ShowList();
    }

    private void ShowPreviousSetupPage()
    {
        if (_gestureMode is ChordMode.DoublePress or ChordMode.LongPress)
        {
            ShowTolerance();
        }
        else
        {
            ShowGestureTypes();
        }
    }

    private void ShowList(
        bool activate = false,
        bool throwOnError = false)
    {
        _selectedAction = null;
        _firstInput = null;
        _secondInput = null;
        _editingShortcutId = null;
        ShowPage(
            DashboardPage.List,
            () => VrDashboardRenderer.Render(_shortcuts),
            activate,
            throwOnError);
    }

    private void ShowGestureTypes()
    {
        ShowPage(
            DashboardPage.GestureType,
            () => VrDashboardRenderer.RenderGestureTypePicker(
                _editingShortcutId is not null));
    }

    private void ShowTolerance()
    {
        ShowPage(
            DashboardPage.Tolerance,
            () => VrDashboardRenderer.RenderTolerancePicker(
                _gestureMode,
                _gestureMode == ChordMode.DoublePress
                    ? _doublePressWindowMs
                    : _holdMs));
    }

    private void ShowRecording()
    {
        ShowPage(
            DashboardPage.RecordInput,
            () => VrDashboardRenderer.RenderInputRecorder(
                _gestureMode,
                _setup,
                _firstInput));
    }

    private void ShowReview(bool activate = false)
    {
        ShowPage(
            DashboardPage.Review,
            () => VrDashboardRenderer.RenderShortcutReview(
                _gestureMode,
                _firstInput,
                _secondInput,
                _selectedAction,
                _doublePressWindowMs,
                _holdMs,
                _editingShortcutId is not null),
            activate);
    }

    private void ShowActionPicker() =>
        ShowPage(
            DashboardPage.ActionPicker,
            () => VrDashboardRenderer.RenderActionPicker(_actionBrowser));

    private void ShowSettings() =>
        ShowPage(
            DashboardPage.Settings,
            () => VrDashboardRenderer.RenderSettings(_settings));

    private void HandleSettingsClick(float x, float y)
    {
        if (IsWithinRow(y, VrDashboardLayout.ChatControlsY, VrDashboardLayout.SettingsRowHeight))
        {
            HandleSurfaceControlsClick(x, isChat: true);
            return;
        }

        if (IsWithinRow(y, VrDashboardLayout.ChatSlidersY, VrDashboardLayout.SettingsSliderRowHeight))
        {
            HandleSlidersClick(x, isChat: true);
            return;
        }

        if (IsWithinRow(y, VrDashboardLayout.GazeSensitivityY, VrDashboardLayout.SettingsRowHeight))
        {
            HandleGazeSensitivityClick(x);
            return;
        }

        if (IsWithinRow(y, VrDashboardLayout.NotificationControlsY, VrDashboardLayout.SettingsRowHeight))
        {
            HandleSurfaceControlsClick(x, isChat: false);
            return;
        }

        if (IsWithinRow(y, VrDashboardLayout.NotificationSlidersY, VrDashboardLayout.SettingsSliderRowHeight))
        {
            HandleSlidersClick(x, isChat: false);
        }
    }

    private static bool IsWithinRow(float y, int rowY, int rowHeight) => y >= rowY && y < rowY + rowHeight;

    /// <summary>The on/off toggle and the anchor mode/hand segmented controls for one surface.</summary>
    private void HandleSurfaceControlsClick(float x, bool isChat)
    {
        var toggle = isChat ? VrDashboardLayout.ChatToggle : VrDashboardLayout.NotificationToggle;
        var anchorMode = isChat ? VrDashboardLayout.ChatAnchorMode : VrDashboardLayout.NotificationAnchorMode;
        var anchorHand = isChat ? VrDashboardLayout.ChatAnchorHand : VrDashboardLayout.NotificationAnchorHand;
        var currentAnchor = isChat ? _settings.ChatAnchor : _settings.NotificationAnchor;

        if (x >= toggle.Left && x <= toggle.Right)
        {
            _settings = isChat
                ? _settings with { ChatEnabled = !_settings.ChatEnabled }
                : _settings with { NotificationsEnabled = !_settings.NotificationsEnabled };
            ApplySettingsChange();
            return;
        }

        // Hand only means anything in Controller mode - drawn disabled
        // rather than hidden when Headset is selected, so it is also
        // non-interactive then, matching what the renderer shows.
        if (currentAnchor.Mode == OverlayAnchorMode.Controller
            && x >= anchorHand[0].Left && x <= anchorHand[^1].Right)
        {
            var hand = VrDashboardLayout.IndexAt(anchorHand, x) == 1
                ? OverlayAnchorHand.Right
                : OverlayAnchorHand.Left;
            SetAnchor(isChat, new OverlayAnchor(OverlayAnchorMode.Controller, hand));
            return;
        }

        if (x >= anchorMode[0].Left && x <= anchorMode[^1].Right)
        {
            var mode = VrDashboardLayout.IndexAt(anchorMode, x) == 1
                ? OverlayAnchorMode.Head
                : OverlayAnchorMode.Controller;
            SetAnchor(isChat, new OverlayAnchor(mode, currentAnchor.Hand));
        }
    }

    private void SetAnchor(bool isChat, OverlayAnchor anchor)
    {
        _settings = isChat
            ? _settings with { ChatAnchor = anchor }
            : _settings with { NotificationAnchor = anchor };
        ApplySettingsChange();
    }

    /// <summary>
    /// Click-to-position, exactly like <see cref="HandleToleranceClick"/>:
    /// the value comes from the click's x ratio across the track, snapped to
    /// a coarse increment so laser jitter cannot produce a silly value.
    /// </summary>
    private void HandleSlidersClick(float x, bool isChat)
    {
        var opacityTrack = isChat ? VrDashboardLayout.ChatOpacityTrack : VrDashboardLayout.NotificationOpacityTrack;
        var sizeTrack = isChat ? VrDashboardLayout.ChatSizeTrack : VrDashboardLayout.NotificationSizeTrack;

        if (x >= opacityTrack.Left && x <= opacityTrack.Right)
        {
            var ratio = Math.Clamp((x - opacityTrack.Left) / (float)opacityTrack.Width, 0f, 1f);
            var opacity = SnapToStep(0.2 + (ratio * 0.8));
            _settings = isChat
                ? _settings with { ChatOpacity = opacity }
                : _settings with { NotificationOpacity = opacity };
            ApplySettingsChange();
            return;
        }

        if (x >= sizeTrack.Left && x <= sizeTrack.Right)
        {
            var ratio = Math.Clamp((x - sizeTrack.Left) / (float)sizeTrack.Width, 0f, 1f);
            var size = SnapToStep(0.5 + (ratio * 1.5));
            _settings = isChat
                ? _settings with { ChatSizeScale = size }
                : _settings with { NotificationSizeScale = size };
            ApplySettingsChange();
        }
    }

    /// <summary>Snaps to 5% steps of whichever slider's own range - the same principle <see cref="HandleToleranceClick"/> applies.</summary>
    private static double SnapToStep(double value) => Math.Round(value / 0.05) * 0.05;

    private void HandleGazeSensitivityClick(float x)
    {
        var rectangles = VrDashboardLayout.GazeSensitivity;
        if (x < rectangles[0].Left || x > rectangles[^1].Right)
        {
            return;
        }

        _settings = _settings with
        {
            GazeSensitivity = (GazeSensitivity)VrDashboardLayout.IndexAt(rectangles, x)
        };
        ApplySettingsChange();
    }

    /// <summary>
    /// Reports the change (so the OpenVR worker applies it live and the tray
    /// persists it - see §"Live-apply architecture" of the Phase 4b plan)
    /// and repaints immediately, so the wearer sees the effect without
    /// leaving the settings page.
    /// </summary>
    private void ApplySettingsChange()
    {
        _settingsChanged(_settings);
        ShowSettings();
    }

    private void ShowPage(
        DashboardPage page,
        Func<RenderedPanel> render,
        bool activate = false,
        bool throwOnError = false)
    {
        try
        {
            // Create first, then upload, then show - the upload needs a handle
            // to land on, and showing an overlay with no texture yet would
            // flash an empty panel.
            _openVr.EnsureDashboardCreated();
            var rendered = render();
            _uploader.Upload(rendered.Rgba, rendered.Width, rendered.Height);
            if (activate)
            {
                _openVr.ShowDashboardOverlay();
            }

            _page = page;
            _log($"SteamVR dashboard page: {page}.");

            // Read-only diagnostics stay scoped to the recorder so the log
            // stays quiet during normal dashboard use.
            _openVr.SetInputProbeEnabled(page == DashboardPage.RecordInput);
        }
        catch (Exception exception)
        {
            _log($"SteamVR dashboard page update failed: {exception.Message}");
            if (throwOnError)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Developer-only: puts the dashboard back on <c>SetOverlayTexture</c> so
    /// the finding recorded against
    /// <see cref="OverlayTextureUploader.TexturePathEnabled"/> can be
    /// re-checked after a SteamVR update without a rebuild. Repaints
    /// immediately, since the current page is what the change has to show up
    /// on.
    /// </summary>
    public void SetTexturePathEnabled(bool enabled)
    {
        if (_uploader.TexturePathEnabled == enabled)
        {
            return;
        }

        _uploader.TexturePathEnabled = enabled;
        _log(enabled
            ? "The SteamVR dashboard is using SetOverlayTexture (developer override)."
            : "The SteamVR dashboard is using SetOverlayRaw.");
        RepaintCurrentPage();
    }

    /// <summary>
    /// Re-renders and re-uploads whatever page is showing, without changing
    /// which page that is.
    /// </summary>
    private void RepaintCurrentPage()
    {
        switch (_page)
        {
            case DashboardPage.List:
                ShowList();
                break;
            case DashboardPage.GestureType:
                ShowGestureTypes();
                break;
            case DashboardPage.Tolerance:
                ShowTolerance();
                break;
            case DashboardPage.ActionPicker:
                ShowActionPicker();
                break;
            case DashboardPage.RecordInput:
                ShowRecording();
                break;
            case DashboardPage.Review:
                ShowReview();
                break;
            case DashboardPage.Settings:
                ShowSettings();
                break;
        }
    }

    /// <summary>
    /// Releases the dashboard's GPU texture. The overlay handle itself belongs
    /// to <see cref="OpenVrInput"/> and is not touched here.
    /// </summary>
    public void Dispose() => _uploader.Dispose();

    private enum DashboardPage
    {
        List,
        GestureType,
        Tolerance,
        ActionPicker,
        RecordInput,
        Review,

        /// <summary>
        /// The Settings tab's own page. Not part of the shortcut wizard's
        /// page stack in any functional sense - it exists in this enum only
        /// so the existing single-page-at-a-time dashboard model can
        /// represent it, per §"Key design decision" of the Phase 4b plan.
        /// </summary>
        Settings
    }
}
