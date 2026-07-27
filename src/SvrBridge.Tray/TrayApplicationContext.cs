using System.Diagnostics;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BridgeEngine _engine = new(new OpenVrWorkerSessionFactory());
    private readonly UserSettingsStore _settingsStore = new();
    private readonly StructuredActivityLog _structuredLog = new();
    private readonly MainForm _mainForm = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _settingsTimer =
        new() { Interval = 600 };
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _dashboardGate = new(1, 1);
    private CancellationTokenSource? _bridgeCancellation;
    private Task? _bridgeTask;
    private UserSettings _settings;
    private bool _isExiting;

    public TrayApplicationContext()
    {
        try
        {
            _settings = _settingsStore.Load();
        }
        catch (InvalidDataException exception)
        {
            _settings = new UserSettings();
            MessageBox.Show(
                exception.Message,
                "SVR Bridge settings",
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

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open SVR Bridge");
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
            Icon = SystemIcons.Application,
            Text = "SVR Bridge — Starting automatically",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
        _mainForm.Show();
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
        }

        base.Dispose(disposing);
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
            await StopRuntimeLockedAsync();
            if (_settings.GetShortcuts().Count == 0)
            {
                SetRunning(false);
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Ready,
                        "Available in SteamVR",
                        "Add your first shortcut on the desktop or in the SteamVR dashboard."));
                await EnsureDashboardAvailableAsync(false);
                return;
            }

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

    private async Task<RecordedGesture?> RecordControllerGestureAsync()
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
                TimeSpan.FromSeconds(20),
                timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _mainForm.ShowSettingsError(
                "No two-input gesture was detected. Make sure both controllers are on, " +
                "then try again. For unsupported controllers, use SteamVR input bindings.");
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
            var dashboardImage = VrDashboardRenderer.Render(
                _settings.GetShortcuts());
            await _engine.ShowDashboardAsync(
                _settings.ToAppConfig(),
                dashboardImage,
                _settings.GetShortcuts(),
                _mainForm.AvailableActions,
                activate,
                CancellationToken.None);
            OnActivity(
                new BridgeActivity(
                    activate ? "dashboard.shown" : "dashboard.available",
                    activate
                        ? "Opened the SVR Bridge dashboard in SteamVR."
                        : "SVR Bridge is available in the SteamVR dashboard."));
        }
        catch (Exception exception)
        {
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
        _mainForm.BeginInvoke(async () =>
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
                await RestartRuntimeAsync();
                await EnsureDashboardAvailableAsync(true);
            }
            catch (Exception exception)
            {
                _mainForm.ShowSettingsError(
                    $"The VR shortcut was recorded but could not be saved. {exception.Message}");
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
            if (showSuccess)
            {
                OnStatusChanged(
                    new BridgeStatus(
                        BridgeState.Ready,
                        "SteamVR setup repaired",
                        "SVR Bridge is registered and stays available while the app is open."));
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
        _structuredLog.Write(
            "bridge.status",
            $"{status.FriendlyName}: {status.Detail}",
            status.State == BridgeState.Error
                ? BridgeLogLevel.Warning
                : BridgeLogLevel.Info);
        var trayText = $"SVR Bridge — {status.FriendlyName}";
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

        _mainForm.Activate();
    }
}
