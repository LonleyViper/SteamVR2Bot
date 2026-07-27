using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrDashboardController
{
    private readonly OpenVrInput _openVr;
    private readonly Action<ShortcutConfig> _shortcutSaved;
    private readonly Action<string> _shortcutDeleted;
    private readonly List<ShortcutConfig> _shortcuts;
    private readonly IReadOnlyList<StreamerBotAction> _actions;
    private readonly VrActionBrowser _actionBrowser;
    private readonly VrDashboardScrollLimiter _scrollLimiter = new();

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

    public VrDashboardController(
        OpenVrInput openVr,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate,
        Action<ShortcutConfig> shortcutSaved,
        Action<string> shortcutDeleted)
    {
        _openVr = openVr;
        _shortcuts = shortcuts.ToList();
        _actions = actions;
        _actionBrowser = new VrActionBrowser(actions);
        _shortcutSaved = shortcutSaved;
        _shortcutDeleted = shortcutDeleted;
        ShowList(activate);
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
        }
    }

    private void HandleListClick(float x, float y)
    {
        if (y >= 780)
        {
            BeginCreate();
            return;
        }

        if (y < 160)
        {
            return;
        }

        var index = (int)((y - 160) / 105);
        var rowY = 160 + (index * 105);
        if (index < 0
            || index >= Math.Min(6, _shortcuts.Count)
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
            if (x < 600)
            {
                ShowGestureTypes();
            }
            else
            {
                StartRecording();
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
            _doublePressWindowMs =
                (int)Math.Round((200 + (ratio * 1000)) / 100d) * 100;
        }
        else
        {
            _holdMs =
                (int)Math.Round((500 + (ratio * 4500)) / 250d) * 250;
        }

        ShowTolerance();
    }

    private void HandleActionPickerClick(float x, float y)
    {
        if (y >= 780 && x < 440)
        {
            if (_actionBrowser.BackToGroups())
            {
                ShowActionPicker();
            }
            else
            {
                ShowReview();
            }

            return;
        }

        if (y >= 780 && x < 890)
        {
            if (_actionBrowser.ScrollPage(-1))
            {
                ShowActionPicker();
            }

            return;
        }

        if (y >= 780)
        {
            if (_actionBrowser.ScrollPage(1))
            {
                ShowActionPicker();
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

        if (x < 400)
        {
            StartRecording();
        }
        else if (x < 930)
        {
            _actionBrowser.Reset();
            _page = DashboardPage.ActionPicker;
            ShowActionPicker();
        }
        else if (_selectedAction is not null)
        {
            SaveShortcut();
        }
    }

    private void HandleRecordInputClick(float x, float y)
    {
        if (y >= 780)
        {
            ShowPreviousSetupPage();
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
        _page = DashboardPage.RecordInput;
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

            SelectRecordedInput(input);
            if (_page != DashboardPage.RecordInput)
            {
                return;
            }

            foreach (var second in pressed.Where(candidate =>
                         !candidate.Id.Equals(
                             input.Id,
                             StringComparison.OrdinalIgnoreCase)))
            {
                SelectRecordedInput(second);
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
                SelectRecordedInput(second);
            }
        }
    }

    private void SelectRecordedInput(ControllerInputBinding input)
    {
        if (_gestureMode != ChordMode.Simultaneous)
        {
            _firstInput = input;
            _secondInput = input;
            ShowReview();
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
        ShowReview();
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

    private void ShowList(bool activate = true)
    {
        _page = DashboardPage.List;
        _selectedAction = null;
        _firstInput = null;
        _secondInput = null;
        _editingShortcutId = null;
        _openVr.UpdateDashboard(
            VrDashboardRenderer.Render(_shortcuts),
            activate);
    }

    private void ShowGestureTypes()
    {
        _page = DashboardPage.GestureType;
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderGestureTypePicker(
                _editingShortcutId is not null));
    }

    private void ShowTolerance()
    {
        _page = DashboardPage.Tolerance;
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderTolerancePicker(
                _gestureMode,
                _gestureMode == ChordMode.DoublePress
                    ? _doublePressWindowMs
                    : _holdMs));
    }

    private void ShowRecording()
    {
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderInputRecorder(
                _gestureMode,
                _setup,
                _firstInput));
    }

    private void ShowReview()
    {
        _page = DashboardPage.Review;
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderShortcutReview(
                _gestureMode,
                _firstInput,
                _secondInput,
                _selectedAction,
                _doublePressWindowMs,
                _holdMs,
                _editingShortcutId is not null));
    }

    private void ShowActionPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderActionPicker(_actionBrowser));

    private enum DashboardPage
    {
        List,
        GestureType,
        Tolerance,
        ActionPicker,
        RecordInput,
        Review
    }
}
