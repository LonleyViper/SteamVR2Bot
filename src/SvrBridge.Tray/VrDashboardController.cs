using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class VrDashboardController
{
    private readonly OpenVrInput _openVr;
    private readonly Action<ShortcutConfig> _shortcutCreated;
    private readonly List<ShortcutConfig> _shortcuts;
    private readonly VrActionBrowser _actionBrowser;
    private DashboardPage _page;
    private StreamerBotAction? _selectedAction;
    private ControllerInputBinding? _firstInput;
    private bool _recordingArmed;

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

    private void HandleScroll(float deltaY)
    {
        if (_page != DashboardPage.ActionPicker || deltaY == 0)
        {
            return;
        }

        if (_actionBrowser.ScrollRows(deltaY > 0 ? -1 : 1))
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

    private void ShowActionPicker() =>
        _openVr.ShowDashboard(
            VrDashboardRenderer.RenderActionPicker(_actionBrowser));

    private enum DashboardPage
    {
        List,
        ActionPicker,
        Recording
    }
}
