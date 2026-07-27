using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrDashboardController
{
    private readonly OpenVrInput _openVr;
    private readonly Action<ShortcutConfig> _shortcutCreated;
    private readonly List<ShortcutConfig> _shortcuts;
    private readonly IReadOnlyList<StreamerBotAction> _actions;
    private DashboardPage _page;
    private StreamerBotAction? _selectedAction;
    private ControllerInputBinding? _firstInput;
    private bool _recordingArmed;
    private int _actionPage;

    public VrDashboardController(
        OpenVrInput openVr,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate,
        Action<ShortcutConfig> shortcutCreated)
    {
        _openVr = openVr;
        _shortcuts = shortcuts.ToList();
        _actions = actions;
        _shortcutCreated = shortcutCreated;
        ShowList(activate);
    }

    public void Tick(InputSnapshot snapshot, ControllerSetup setup)
    {
        if (_openVr.TryGetDashboardClick(out var x, out var y))
        {
            HandleClick(x, y);
        }

        if (_page != DashboardPage.Recording)
        {
            return;
        }

        var pressed = ControllerInputs.PressedInputs(snapshot, setup);
        if (!_recordingArmed)
        {
            if (pressed.Count == 0)
            {
                _recordingArmed = true;
            }

            return;
        }

        if (_firstInput is null)
        {
            if (pressed.Count >= 2)
            {
                CompleteRecording(pressed[0], pressed[1]);
                return;
            }

            if (pressed.FirstOrDefault() is { } first)
            {
                _firstInput = first;
                ShowRecording();
            }

            return;
        }

        var second = pressed.FirstOrDefault(input =>
            !input.Id.Equals(_firstInput.Id, StringComparison.OrdinalIgnoreCase));
        if (second is not null)
        {
            CompleteRecording(_firstInput, second);
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
                _actionPage = 0;
                _page = DashboardPage.ActionPicker;
                _openVr.ShowDashboard(
                    VrDashboardRenderer.RenderActionPicker(_actions, _actionPage));
                break;
            case DashboardPage.ActionPicker when y >= 780 && x < 700:
                ShowList();
                break;
            case DashboardPage.ActionPicker when y >= 780:
                _actionPage++;
                if (_actionPage * 6 >= _actions.Count)
                {
                    _actionPage = 0;
                }

                _openVr.ShowDashboard(
                    VrDashboardRenderer.RenderActionPicker(_actions, _actionPage));
                break;
            case DashboardPage.ActionPicker:
            {
                var index = (int)((y - 165) / 91);
                var actionIndex = _actionPage * 6 + index;
                if (index >= 0 && index < 6 && actionIndex < _actions.Count)
                {
                    _selectedAction = _actions[actionIndex];
                    _firstInput = null;
                    _recordingArmed = false;
                    _page = DashboardPage.Recording;
                    ShowRecording();
                }

                break;
            }
            case DashboardPage.Recording when y >= 780:
                ShowList();
                break;
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
                Mode = ChordMode.Modifier,
                WindowMs = 2000,
                CooldownMs = 250
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
        _recordingArmed = false;
        _openVr.UpdateDashboard(
            VrDashboardRenderer.Render(_shortcuts),
            activate);
    }

    private void ShowRecording() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderRecording(_firstInput?.FriendlyName));

    private enum DashboardPage
    {
        List,
        ActionPicker,
        Recording
    }
}
