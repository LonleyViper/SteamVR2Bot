using System.Diagnostics;
using System.Runtime.InteropServices;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BridgeEngine _engine = new(new OpenVrWorkerSessionFactory());
    private readonly UserSettingsStore _settingsStore = new();
    private readonly StructuredActivityLog _structuredLog = new();
    private readonly Icon _applicationIcon = LoadApplicationIcon();
    private readonly MainForm _mainForm = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _settingsTimer =
        new() { Interval = 600 };
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _dashboardGate = new(1, 1);
    private CancellationTokenSource? _bridgeCancellation;
    private Task? _bridgeTask;
    private UserSettings _settings;
    private bool _dashboardAvailable;
    private bool _isExiting;

    public TrayApplicationContext()
    {
        _mainForm.Icon = _applicationIcon;

        try
        {
            _settings = _settingsStore.Load();
        }
        catch (InvalidDataException exception)
        {
            _settings = new UserSettings();
            MessageBox.Show(
                exception.Message,
                "SteamVR2Bot settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        _mainForm.ApplySettings(_settings);
        _mainForm.SettingsChanged += QueueSettingsApply;
        _mainForm.TestRequested += TestStreamerBot;
        _mainForm.FindActionsRequested += FindStreamerBotActions;
        _mainForm.SteamVrSetupRequested += RepairSteamVrSetup;
        _mainForm.BindingsRequested += OpenControllerBindings;
        _mainForm.DashboardRequested += OpenVrDashboard;
        _mainForm.RecordRequested += RecordControllerGestureAsync;
        _mainForm.LogsRequested += OpenLogs;
        _mainForm.ExitRequested += ExitApplication;
        _mainForm.Shown += async (_, _) => await InitializeAsync();

        _settingsTimer.Tick += async (_, _) =>
        {
            _settingsTimer.Stop();
            await SaveAndApplySettingsAsync();
        };

        _engine.StatusChanged += OnStatusChanged;
        _engine.Activity += OnActivity;
        _engine.ControllerSetupChanged += _mainForm.UpdateControllerSetup;
        _engine.ShortcutCreated += SaveDashboardShortcut;
        _engine.ShortcutDeleted += DeleteDashboardShortcut;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open SteamVR2Bot");
        var dashboard = new ToolStripMenuItem("Open SteamVR dashboard");
        var test = new ToolStripMenuItem("Test selected action");
        var bindings = new ToolStripMenuItem("SteamVR input bindings");
        var logs = new ToolStripMenuItem("Open logs");
        var exit = new ToolStripMenuItem("Exit");

        open.Click += (_, _) => ShowMainWindow();
        dashboard.Click += (_, _) => OpenVrDashboard();
        test.Click += (_, _) =>
        {
            if (_mainForm.SelectedShortcut is { } shortcut)
            {
                TestStreamerBot(shortcut);
            }
            else
            {
                ShowMainWindow();
            }
        };
        bindings.Click += (_, _) => OpenControllerBindings();
        logs.Click += (_, _) => OpenLogs();
        exit.Click += (_, _) => ExitApplication();

        menu.Items.AddRange(
        [
            open,
            dashboard,
            new ToolStripSeparator(),
            test,
            bindings,
            logs,
            new ToolStripSeparator(),
            exit
        ]);

        _trayIcon = new NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "SteamVR2Bot — Starting automatically",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        // Not a plain Show: a process launched with a hidden window state would
        // otherwise come up with no window and no way to reach one.
        ShowMainWindow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _settingsTimer.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _bridgeCancellation?.Dispose();
            _runtimeGate.Dispose();
            _dashboardGate.Dispose();
            _mainForm.Dispose();
            _applicationIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Icon LoadApplicationIcon()
    {
        if (Environment.ProcessPath is { } executablePath
            && Icon.ExtractAssociatedIcon(executablePath) is { } icon)
        {
            return icon;
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private async Task InitializeAsync()
    {
        await RegisterSteamVrAsync(showSuccess: false);
        await RefreshStreamerBotActionsAsync(
            showErrors: false,
            refreshDashboard: false);
        await RestartRuntimeAsync();
    }

    private void QueueSettingsApply()
    {
        _settingsTimer.Stop();
        _settingsTimer.Start();
    }

    private async Task SaveAndApplySettingsAsync()
    {
        UserSettings updated;
        try
        {
            updated = _mainForm.ReadSettings();
            UserSettingsStore.ValidateForSave(updated);
            _settingsStore.Save(updated);
            _settings = updated;
            OnActivity(
                new BridgeActivity(
                    "settings.saved",
                    "Changes saved automatically."));
        }
        catch (InvalidDataException exception)
        {
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Error,
                    "Finish this setting",
                    exception.Message));
            return;
        }

        await RestartRuntimeAsync();
    }

    private async Task RestartRuntimeAsync()
    {
        await _runtimeGate.WaitAsync();
        try
        {
            _dashboardAvailable = false;
            await StopRuntimeLockedAsync();
            try
            {
                UserSettingsStore.Validate(_settings);
            }
            catch (InvalidDataException exception)
            {
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Error,
                        "Finish this setting",
                        exception.Message));
                return;
            }

            _bridgeCancellation = new CancellationTokenSource();
            var cancellation = _bridgeCancellation;
            _bridgeTask = _engine.RunAsync(
                _settings.ToAppConfig(),
                cancellation.Token);
            SetRunning(true);
            _ = ObserveRuntimeAsync(_bridgeTask, cancellation);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task ObserveRuntimeAsync(
        Task runtimeTask,
        CancellationTokenSource cancellation)
    {
        try
        {
            await runtimeTask;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Normal automatic reload or app exit.
        }
        catch (Exception exception)
        {
            OnActivity(
                new BridgeActivity(
                    "bridge.stopped_with_error",
                    $"Runtime stopped: {exception.Message}",
                    BridgeLogLevel.Error));
        }
        finally
        {
            if (ReferenceEquals(_bridgeTask, runtimeTask))
            {
                SetRunning(false);
            }
        }
    }

    private async Task StopRuntimeLockedAsync()
    {
        var cancellation = _bridgeCancellation;
        var task = _bridgeTask;
        _bridgeCancellation = null;
        _bridgeTask = null;
        if (cancellation is null || task is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Normal automatic reload.
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async void TestStreamerBot(ShortcutConfig shortcut)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _engine.TestActionAsync(
                _settings.ToAppConfig(),
                shortcut,
                timeout.Token);
        }
        catch (Exception exception)
        {
            OnActivity(
                new BridgeActivity(
                    "streamerbot.test_failed",
                    $"Test failed: {exception.Message}",
                    BridgeLogLevel.Warning));
            ShowMainWindow();
        }
    }

    private async Task<RecordedGesture?> RecordControllerGestureAsync(ChordMode mode)
    {
        await _runtimeGate.WaitAsync();
        try
        {
            await StopRuntimeLockedAsync();
        }
        finally
        {
            _runtimeGate.Release();
        }

        try
        {
            var settings = _mainForm.ReadSettings();
            UserSettingsStore.ValidateConnection(settings);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            return await _engine.RecordGestureAsync(
                settings.ToAppConfig(),
                mode,
                TimeSpan.FromSeconds(20),
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var detail = mode
                is ChordMode.SinglePress
                or ChordMode.LongPress
                or ChordMode.DoublePress
                ? "No controller input was detected. Make sure the controller is on, then try again."
                : "No two-input gesture was detected. Make sure both controllers are on, " +
                  "then try again. For unsupported controllers, use SteamVR input bindings.";
            _mainForm.ShowSettingsError(detail);
            return null;
        }
        catch (Exception exception)
        {
            _mainForm.ShowSettingsError(
                $"Could not record those inputs. {exception.Message}");
            return null;
        }
        finally
        {
            await RestartRuntimeAsync();
        }
    }

    private async void OpenVrDashboard() =>
        await EnsureDashboardAvailableAsync(true);

    private async Task EnsureDashboardAvailableAsync(bool activate)
    {
        if (_isExiting)
        {
            return;
        }

        await _dashboardGate.WaitAsync();
        try
        {
            if (!activate && _dashboardAvailable)
            {
                return;
            }

            var dashboardImage = VrDashboardRenderer.Render(
                _settings.GetShortcuts());
            await _engine.ShowDashboardAsync(
                _settings.ToAppConfig(),
                dashboardImage,
                _settings.GetShortcuts(),
                _mainForm.AvailableActions,
                activate,
                CancellationToken.None);
            _dashboardAvailable = true;
            OnActivity(
                new BridgeActivity(
                    activate ? "dashboard.shown" : "dashboard.available",
                    activate
                        ? "Opened the SteamVR2Bot dashboard in SteamVR."
                        : "SteamVR2Bot is available in the SteamVR dashboard."));
        }
        catch (Exception exception)
        {
            _dashboardAvailable = false;
            OnActivity(
                new BridgeActivity(
                    "dashboard.unavailable",
                    $"SteamVR dashboard unavailable: {exception.Message}",
                    BridgeLogLevel.Warning));
        }
        finally
        {
            _dashboardGate.Release();
        }
    }

    private void SaveDashboardShortcut(ShortcutConfig shortcut)
    {
        _mainForm.BeginInvoke(() =>
        {
            var shortcuts = _settings.GetShortcuts()
                .Where(item => item.Id != shortcut.Id)
                .Append(shortcut)
                .ToArray();
            var updated = _settings with
            {
                Shortcuts = shortcuts,
                StartBridgeWhenAppOpens = true
            };

            try
            {
                _settingsStore.Save(updated);
                _settings = updated;
                _mainForm.ApplySettings(updated);
                OnActivity(
                    new BridgeActivity(
                        "dashboard.shortcut_saved",
                        $"Saved “{shortcut.Name}” automatically."));
                _engine.UpdateShortcuts(updated.GetShortcuts());
            }
            catch (Exception exception)
            {
                _mainForm.ShowSettingsError(
                    $"The VR shortcut was recorded but could not be saved. {exception.Message}");
            }
        });
    }

    private void DeleteDashboardShortcut(string shortcutId)
    {
        _mainForm.BeginInvoke(() =>
        {
            var existing = _settings.GetShortcuts().FirstOrDefault(item =>
                item.Id.Equals(shortcutId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return;
            }

            var remaining = _settings.GetShortcuts()
                .Where(item => !item.Id.Equals(
                    shortcutId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var updated = _settings with
            {
                Shortcuts = remaining,
                ActionName = remaining.Length == 0 ? "" : _settings.ActionName,
                ActionId = remaining.Length == 0 ? "" : _settings.ActionId,
                StartBridgeWhenAppOpens = true
            };

            try
            {
                _settingsStore.Save(updated);
                _settings = updated;
                _mainForm.ApplySettings(updated);
                OnActivity(
                    new BridgeActivity(
                        "dashboard.shortcut_deleted",
                        $"Deleted “{existing.Name}” automatically."));
                _engine.UpdateShortcuts(updated.GetShortcuts());
            }
            catch (Exception exception)
            {
                _mainForm.ShowSettingsError(
                    $"The VR shortcut could not be deleted. {exception.Message}");
            }
        });
    }

    private async void FindStreamerBotActions() =>
        await RefreshStreamerBotActionsAsync(
            showErrors: true,
            refreshDashboard: true);

    private async Task RefreshStreamerBotActionsAsync(
        bool showErrors,
        bool refreshDashboard)
    {
        try
        {
            var settings = _mainForm.ReadSettings();
            UserSettingsStore.ValidateConnection(settings);
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Starting,
                    "Loading Streamer.bot actions…",
                    "Reading friendly action names."));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = new StreamerBotClient(
                settings.ToAppConfig().StreamerBot,
                message => OnActivity(
                    new BridgeActivity("streamerbot.connection", message)));
            var actions = await client.GetActionsAsync(timeout.Token);
            _mainForm.ShowActions(actions);
            OnActivity(
                new BridgeActivity(
                    "streamerbot.actions_found",
                    $"Loaded {actions.Count} enabled Streamer.bot actions."));
            if (refreshDashboard)
            {
                await EnsureDashboardAvailableAsync(false);
            }
        }
        catch (Exception exception)
        {
            OnActivity(
                new BridgeActivity(
                    "streamerbot.actions_unavailable",
                    $"Could not load Streamer.bot actions: {exception.Message}",
                    BridgeLogLevel.Warning));
            if (showErrors)
            {
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Error,
                        "Could not load actions",
                        "Check the Streamer.bot address and password."));
                ShowMainWindow();
            }
        }
    }

    private async void RepairSteamVrSetup() =>
        await RegisterSteamVrAsync(showSuccess: true);

    private async Task RegisterSteamVrAsync(bool showSuccess)
    {
        try
        {
            await Task.Run(
                () => SteamVrApplications.Register(
                    Path.Combine(AppContext.BaseDirectory, "app.vrmanifest"),
                    Path.Combine(AppContext.BaseDirectory, "actions.json"),
                    message => OnActivity(
                        new BridgeActivity("steamvr.setup", message))));
            // SteamVR may still be completing the binding load started by the
            // short-lived registration connection. Let that finish before
            // replacing an old user/workshop binding with the app-owned map.
            await Task.Delay(750);
            await SteamVrApplications.SelectPackagedViveBindingAsync(
                Path.Combine(AppContext.BaseDirectory, "bindings_vive_controller.json"));
            await Task.Delay(500);
            OnActivity(
                new BridgeActivity(
                    "steamvr.inputs_ready",
                    "Installed the built-in Vive controller input map."));
            if (showSuccess)
            {
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Ready,
                        "SteamVR setup repaired",
                        "SteamVR2Bot is registered and stays available while the app is open."));
            }
        }
        catch (Exception exception)
        {
            OnActivity(
                new BridgeActivity(
                    "steamvr.setup_unavailable",
                    $"SteamVR registration will retry next launch: {exception.Message}",
                    BridgeLogLevel.Warning));
            if (showSuccess)
            {
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Error,
                        "SteamVR setup failed",
                        exception.Message));
            }
        }
    }

    private async void OpenControllerBindings()
    {
        try
        {
            await _engine.OpenBindingUiAsync(_settings.ToAppConfig());
            OnActivity(
                new BridgeActivity(
                    "controller.binding_ui_opened",
                    "Opened SteamVR controller bindings."));
        }
        catch (Exception exception)
        {
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Error,
                    "Could not open controller inputs",
                    $"Start SteamVR, then try again. {exception.Message}"));
            ShowMainWindow();
        }
    }

    private void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(_structuredLog.LogDirectory);
            Process.Start(
                new ProcessStartInfo("explorer.exe", _structuredLog.LogDirectory)
                {
                    UseShellExecute = true
                });
        }
        catch (Exception exception)
        {
            _mainForm.ShowSettingsError($"Could not open the log folder. {exception.Message}");
        }
    }

    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _settingsTimer.Stop();
        await _runtimeGate.WaitAsync();
        try
        {
            await StopRuntimeLockedAsync();
        }
        finally
        {
            _runtimeGate.Release();
        }

        _trayIcon.Visible = false;
        _mainForm.AllowCloseAndClose();
        ExitThread();
    }

    private void OnStatusChanged(BridgeStatus status)
    {
        if (_mainForm.InvokeRequired)
        {
            _mainForm.BeginInvoke(() => OnStatusChanged(status));
            return;
        }

        _mainForm.UpdateStatus(status);
        if (status.State == BridgeState.Error
            && status.FriendlyName == "Waiting for SteamVR")
        {
            _dashboardAvailable = false;
        }
        _structuredLog.Write(
            "bridge.status",
            $"{status.FriendlyName}: {status.Detail}",
            status.State == BridgeState.Error
                ? BridgeLogLevel.Warning
                : BridgeLogLevel.Info);
        var trayText = $"SteamVR2Bot — {status.FriendlyName}";
        _trayIcon.Text = trayText.Length <= 63
            ? trayText
            : trayText[..63];

        if (status.State == BridgeState.Ready)
        {
            _ = EnsureDashboardAvailableAsync(false);
        }
    }

    private void OnActivity(BridgeActivity activity)
    {
        _mainForm.AddActivity(activity.Message);
        _structuredLog.Write(activity);
    }

    private void SetRunning(bool running) =>
        _mainForm.SetRunning(running);

    /// <summary>
    /// Brings the desktop window up, asking Windows whether it is really on
    /// screen rather than trusting <see cref="Form.Visible"/>.
    /// <para>
    /// When a process is started with a hidden window state, Windows ignores
    /// the show command of the first ShowWindow call and hides that window
    /// instead. WinForms still records the form as visible, so the tray icon
    /// and its Open item would both quietly do nothing and the user would have
    /// no way back to the app. Re-showing repairs that, because only the first
    /// call is overridden.
    /// </para>
    /// </summary>
    private void ShowMainWindow()
    {
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }

        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        // Ask Windows rather than WinForms. Show() above may have been the
        // process's first ShowWindow call, and that one keeps the hidden state
        // it was launched with instead of the state it asked for. A second call
        // is honoured, so this is what actually puts the window on screen.
        if (!IsWindowVisible(_mainForm.Handle))
        {
            ShowWindow(_mainForm.Handle, ShowWindowNormal);
        }

        _mainForm.Activate();
        _mainForm.BringToFront();
    }

    private const int ShowWindowNormal = 1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
