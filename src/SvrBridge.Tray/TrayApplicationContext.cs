using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly BridgeEngine _engine = new();
    private readonly UserSettingsStore _settingsStore = new();
    private readonly MainForm _mainForm = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _startMenu = new("Start controller shortcut");
    private readonly ToolStripMenuItem _stopMenu = new("Stop controller shortcut");
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
        _mainForm.StartRequested += StartBridge;
        _mainForm.StopRequested += StopBridge;
        _mainForm.TestRequested += TestStreamerBot;
        _mainForm.FindActionsRequested += FindStreamerBotActions;
        _mainForm.SteamVrSetupRequested += SetUpSteamVr;
        _mainForm.ExitRequested += ExitApplication;
        _mainForm.Shown += (_, _) =>
        {
            if (_settings.StartBridgeWhenAppOpens)
            {
                StartBridge();
            }
        };

        _engine.StatusChanged += OnStatusChanged;
        _engine.Activity += OnActivity;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open SVR Bridge");
        var test = new ToolStripMenuItem("Test Streamer.bot action");
        var exit = new ToolStripMenuItem("Exit");

        open.Click += (_, _) => ShowMainWindow();
        _startMenu.Click += (_, _) => StartBridge();
        _stopMenu.Click += (_, _) => StopBridge();
        test.Click += (_, _) => TestStreamerBot();
        exit.Click += (_, _) => ExitApplication();
        _stopMenu.Enabled = false;

        menu.Items.AddRange(
        [
            open,
            new ToolStripSeparator(),
            _startMenu,
            _stopMenu,
            test,
            new ToolStripSeparator(),
            exit
        ]);

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "SVR Bridge — Stopped",
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
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _bridgeCancellation?.Dispose();
            _mainForm.Dispose();
        }

        base.Dispose(disposing);
    }

    private async void StartBridge()
    {
        if (_bridgeTask is { IsCompleted: false })
        {
            ShowMainWindow();
            return;
        }

        try
        {
            _settings = _mainForm.ReadSettings();
            UserSettingsStore.Validate(_settings);
            _settingsStore.Save(_settings);
        }
        catch (InvalidDataException exception)
        {
            _mainForm.ShowSettingsError(exception.Message);
            ShowMainWindow();
            return;
        }

        _bridgeCancellation = new CancellationTokenSource();
        SetRunning(true);

        try
        {
            _bridgeTask = _engine.RunAsync(
                _settings.ToAppConfig(),
                _bridgeCancellation.Token);
            await _bridgeTask;
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            OnActivity($"Stopped: {exception.Message}");
            ShowMainWindow();
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async void StopBridge()
    {
        var cancellation = _bridgeCancellation;
        var task = _bridgeTask;
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
            // Normal stop.
        }
    }

    private async void TestStreamerBot()
    {
        UserSettings settings;
        try
        {
            settings = _mainForm.ReadSettings();
            UserSettingsStore.Validate(settings);
            _settingsStore.Save(settings);
            _settings = settings;
        }
        catch (InvalidDataException exception)
        {
            _mainForm.ShowSettingsError(exception.Message);
            ShowMainWindow();
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await _engine.TestActionAsync(settings.ToAppConfig(), timeout.Token);
        }
        catch (Exception exception)
        {
            OnActivity($"Test failed: {exception.Message}");
            ShowMainWindow();
        }
    }

    private async void FindStreamerBotActions()
    {
        var settings = _mainForm.ReadSettings();
        try
        {
            UserSettingsStore.ValidateConnection(settings);
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Starting,
                    "Finding your actions…",
                    "Connecting to Streamer.bot and reading its action names."));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = new StreamerBotClient(
                settings.ToAppConfig().StreamerBot,
                OnActivity);
            var actions = await client.GetActionsAsync(timeout.Token);
            _mainForm.ShowActions(actions);
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Stopped,
                    $"{actions.Count} actions found",
                    "Choose the action you want, then use Test Streamer.bot."));
            OnActivity($"Found {actions.Count} enabled Streamer.bot actions.");
        }
        catch (Exception exception)
        {
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Error,
                    "Could not find actions",
                    $"Check the Streamer.bot address and password. {exception.Message}"));
            ShowMainWindow();
        }
    }

    private async void SetUpSteamVr()
    {
        try
        {
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Starting,
                    "Setting up SteamVR…",
                    "Registering SVR Bridge and its controller shortcut."));

            await Task.Run(
                () => SteamVrApplications.Register(
                    Path.Combine(AppContext.BaseDirectory, "app.vrmanifest"),
                    Path.Combine(AppContext.BaseDirectory, "actions.json"),
                    OnActivity));

            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Ready,
                    "SteamVR setup complete",
                    "SVR Bridge is registered and ready to start."));
        }
        catch (Exception exception)
        {
            OnStatusChanged(
                new BridgeStatus(
                    BridgeState.Error,
                    "SteamVR setup failed",
                    exception.Message));
            ShowMainWindow();
        }
    }

    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        StopBridge();
        if (_bridgeTask is not null)
        {
            try
            {
                await _bridgeTask;
            }
            catch
            {
                // The error was already surfaced through the status area.
            }
        }

        _trayIcon.Visible = false;
        _mainForm.AllowCloseAndClose();
        ExitThread();
    }

    private void OnStatusChanged(BridgeStatus status)
    {
        _mainForm.UpdateStatus(status);
        var trayText = $"SVR Bridge — {status.FriendlyName}";
        _trayIcon.Text = trayText.Length <= 63
            ? trayText
            : trayText[..63];
    }

    private void OnActivity(string message)
    {
        _mainForm.AddActivity(message);
    }

    private void SetRunning(bool running)
    {
        _mainForm.SetRunning(running);
        _startMenu.Enabled = !running;
        _stopMenu.Enabled = running;
    }

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
