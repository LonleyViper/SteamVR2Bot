using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrDashboardController
{
    private readonly OpenVrInput _openVr;
    private readonly Action<ShortcutConfig> _shortcutCreated;
    private readonly List<ShortcutConfig> _shortcuts;
    private readonly VrActionBrowser _actionBrowser;
    private readonly VrDashboardScrollLimiter _scrollLimiter = new();
    private DashboardPage _page;
    private StreamerBotAction? _selectedAction;
    private ControllerInputBinding? _firstInput;
    private ChordMode _gestureMode;
    private int _holdMs = 1000;
    private ControllerHand _selectedHand;
    private ControllerSetup _setup = ControllerSetup.Unknown;

    public VrDashboardController(
        OpenVrInput openVr,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate,
        Action<ShortcutConfig> shortcutCreated)
    {
        _openVr = openVr;
        _shortcuts = shortcuts.ToList();
        _actionBrowser = new VrActionBrowser(actions);
        _shortcutCreated = shortcutCreated;
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
    }

    private void HandleClick(float x, float y)
    {
        if (x is < 0 or > 1400 || y is < 0 or > 900)
        {
            return;
        }

        switch (_page)
        {
            case DashboardPage.List when y >= 780:
                _actionBrowser.Reset();
                _page = DashboardPage.ActionPicker;
                ShowActionPicker();
                break;
            case DashboardPage.ActionPicker when y >= 780 && x < 440:
                if (_actionBrowser.BackToGroups())
                {
                    ShowActionPicker();
                }
                else
                {
                    ShowList();
                }

                break;
            case DashboardPage.ActionPicker when y >= 780 && x < 890:
                if (_actionBrowser.ScrollPage(-1))
                {
                    ShowActionPicker();
                }

                break;
            case DashboardPage.ActionPicker when y >= 780:
                if (_actionBrowser.ScrollPage(1))
                {
                    ShowActionPicker();
                }

                break;
            case DashboardPage.ActionPicker when y < 165:
                break;
            case DashboardPage.ActionPicker:
            {
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
                    _page = DashboardPage.QuickInputPicker;
                    ShowQuickInputPicker();
                }

                break;
            }
            case DashboardPage.QuickInputPicker when y >= 780 && x < 700:
                _page = DashboardPage.ActionPicker;
                ShowActionPicker();
                break;
            case DashboardPage.QuickInputPicker when y >= 780:
                _page = DashboardPage.GesturePicker;
                ShowGesturePicker();
                break;
            case DashboardPage.QuickInputPicker when y < 165:
                break;
            case DashboardPage.QuickInputPicker:
            {
                var index = (int)((y - 165) / 91);
                var options = QuickInputOptions();
                if (index >= 0 && index < options.Count)
                {
                    _gestureMode = ChordMode.LongPress;
                    _holdMs = 2000;
                    CompleteRecording(options[index], options[index]);
                }

                break;
            }
            case DashboardPage.GesturePicker when y >= 780:
                _page = DashboardPage.QuickInputPicker;
                ShowQuickInputPicker();
                break;
            case DashboardPage.GesturePicker when y < 165:
                break;
            case DashboardPage.GesturePicker:
            {
                var index = (int)((y - 165) / 105);
                switch (index)
                {
                    case 0:
                        StartInputSelection(ChordMode.LongPress, 1000);
                        break;
                    case 1:
                        StartInputSelection(ChordMode.LongPress, 2000);
                        break;
                    case 2:
                        StartInputSelection(ChordMode.LongPress, 3000);
                        break;
                    case 3:
                        StartInputSelection(ChordMode.Modifier);
                        break;
                    case 4:
                        StartInputSelection(ChordMode.Simultaneous);
                        break;
                }

                break;
            }
            case DashboardPage.InputHandPicker when y >= 780:
                if (_firstInput is null)
                {
                    _page = DashboardPage.GesturePicker;
                    ShowGesturePicker();
                }
                else
                {
                    _firstInput = null;
                    ShowHandPicker();
                }

                break;
            case DashboardPage.InputHandPicker when y is >= 230 and < 410:
                _selectedHand = ControllerHand.Left;
                _page = DashboardPage.InputButtonPicker;
                ShowButtonPicker();
                break;
            case DashboardPage.InputHandPicker when y is >= 430 and < 610:
                _selectedHand = ControllerHand.Right;
                _page = DashboardPage.InputButtonPicker;
                ShowButtonPicker();
                break;
            case DashboardPage.InputButtonPicker when y >= 780:
                _page = DashboardPage.InputHandPicker;
                ShowHandPicker();
                break;
            case DashboardPage.InputButtonPicker when y < 165:
                break;
            case DashboardPage.InputButtonPicker:
            {
                var index = (int)((y - 165) / 91);
                var options = CurrentButtonOptions();
                if (index >= 0 && index < options.Count)
                {
                    SelectInput(options[index]);
                }

                break;
            }
        }
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

    private void CompleteRecording(
        ControllerInputBinding safetyInput,
        ControllerInputBinding actionInput)
    {
        if (_selectedAction is null)
        {
            ShowList();
            return;
        }

        var shortcut = new ShortcutConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = _selectedAction.Name,
            Enabled = true,
            SafetyInput = safetyInput,
            ActionInput = actionInput,
            Gesture = new ChordConfig
            {
                Mode = _gestureMode,
                WindowMs = _gestureMode == ChordMode.Simultaneous ? 300 : 2000,
                CooldownMs = 250,
                HoldMs = _holdMs
            },
            ActionName = _selectedAction.Name,
            ActionId = _selectedAction.Id
        };
        _shortcuts.Add(shortcut);
        _shortcutCreated(shortcut);
        ShowList();
    }

    private void ShowList(bool activate = true)
    {
        _page = DashboardPage.List;
        _selectedAction = null;
        _firstInput = null;
        _openVr.UpdateDashboard(
            VrDashboardRenderer.Render(_shortcuts),
            activate);
    }

    private void ShowActionPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderActionPicker(_actionBrowser));

    private void ShowGesturePicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderGesturePicker(
                _selectedAction?.Name ?? "Selected action"));

    private void ShowQuickInputPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderQuickInputPicker(
                _selectedAction?.Name ?? "Selected action",
                QuickInputOptions()));

    private IReadOnlyList<ControllerInputBinding> QuickInputOptions()
    {
        var left = ControllerInputs.AvailableInputs(ControllerHand.Left, _setup);
        var right = ControllerInputs.AvailableInputs(ControllerHand.Right, _setup);
        return Enumerable.Range(0, 3)
            .SelectMany(index => new[]
            {
                left.ElementAtOrDefault(index),
                right.ElementAtOrDefault(index)
            })
            .Where(input => input is not null)
            .Cast<ControllerInputBinding>()
            .ToArray();
    }

    private void StartInputSelection(ChordMode mode, int holdMs = 1000)
    {
        _gestureMode = mode;
        _holdMs = holdMs;
        _firstInput = null;
        _page = DashboardPage.InputHandPicker;
        ShowHandPicker();
    }

    private void ShowHandPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderHandPicker(
                ControllerInputs.ControllerFamily(_setup),
                _firstInput?.FriendlyName));

    private void ShowButtonPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderButtonPicker(
                _selectedHand,
                CurrentButtonOptions(),
                _firstInput?.FriendlyName));

    private IReadOnlyList<ControllerInputBinding> CurrentButtonOptions() =>
        ControllerInputs.AvailableInputs(_selectedHand, _setup)
            .Where(input =>
                _firstInput is null
                || !input.Id.Equals(_firstInput.Id, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private void SelectInput(ControllerInputBinding input)
    {
        if (_gestureMode == ChordMode.LongPress)
        {
            CompleteRecording(input, input);
            return;
        }

        if (_firstInput is null)
        {
            _firstInput = input;
            _page = DashboardPage.InputHandPicker;
            ShowHandPicker();
            return;
        }

        CompleteRecording(_firstInput, input);
    }

    private enum DashboardPage
    {
        List,
        ActionPicker,
        QuickInputPicker,
        GesturePicker,
        InputHandPicker,
        InputButtonPicker
    }
}
