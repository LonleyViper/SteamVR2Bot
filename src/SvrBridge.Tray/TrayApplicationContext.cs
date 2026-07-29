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
    private StreamerBotEventStream? _eventStream;
    private Task? _eventPump;
    private EventStreamSettings? _appliedEventStream;
    private readonly object _emoteCatalogGate = new();
    private StreamerBotEventStream? _emoteCatalogFetchedFor;
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
        _engine.VrShutdownRequested += OnVrShutdownRequested;

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open SteamVR2Bot");
        var dashboard = new ToolStripMenuItem("Open SteamVR dashboard");
        var test = new ToolStripMenuItem("Test selected action");
        var bindings = new ToolStripMenuItem("SteamVR input bindings");
        var logs = new ToolStripMenuItem("Open logs");
        // A development aid, not a feature: unchecked at every launch, never
        // persisted, and only meaningful while SteamVR is running.
        var testOverlay = new ToolStripMenuItem("Show VR test overlay (developer)")
        {
            CheckOnClick = true,
            Checked = false
        };
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
        testOverlay.Click += (_, _) => ToggleTestOverlay(testOverlay);
        exit.Click += (_, _) => ExitApplication();

        menu.Items.AddRange(
        [
            open,
            dashboard,
            new ToolStripSeparator(),
            test,
            bindings,
            logs,
            testOverlay,
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

            // Deliberately ahead of the shortcut validation below: the event
            // feed is a display surface, not a delivery path, so it comes up
            // for a user who has configured a connection but no shortcuts yet,
            // and an unreachable feed never stops one being delivered.
            await RestartEventStreamLockedAsync();
            _ = EnsureEmoteCatalogAsync();

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

    /// <summary>
    /// Rebuilds the Streamer.bot event feed to match the saved settings.
    /// Called under the runtime gate so it cannot race a settings save.
    /// </summary>
    private async Task RestartEventStreamLockedAsync()
    {
        // Every settings save restarts the runtime, including saves that only
        // renamed a shortcut. Dropping a healthy long-lived connection for one
        // of those would reconnect, re-subscribe and log a line each time, so
        // only the settings the feed is actually made of are compared.
        var wanted = new EventStreamSettings(
            _settings.EventStreamEnabled,
            _settings.StreamerBotAddress.Trim(),
            _settings.Password);
        if (wanted == _appliedEventStream)
        {
            return;
        }

        await StopEventStreamLockedAsync();
        _appliedEventStream = wanted;

        if (!_settings.EventStreamEnabled)
        {
            _mainForm.UpdateEventStreamState(null);
            return;
        }

        var stream = new StreamerBotEventStream(
            _settings.ToAppConfig().StreamerBot,
            OnActivity);
        stream.StateChanged += state =>
        {
            _mainForm.UpdateEventStreamState(state);
            if (state == StreamerBotStreamState.Connected)
            {
                _ = EnsureEmoteCatalogAsync();
            }
        };
        _eventStream = stream;
        _eventPump = ConsumeEventStreamAsync(stream);
        _mainForm.UpdateEventStreamState(stream.State);
        stream.Start();
    }

    /// <summary>Bounded retry window for delivering a fetched catalog to a worker that is not up yet.</summary>
    private const int EmoteCatalogDeliveryAttempts = 15;

    private static readonly TimeSpan EmoteCatalogRetryDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Loads the Twitch/BetterTTV/FrankerFaceZ/7TV emote catalog once per
    /// connected event stream, via Streamer.bot's own <c>TwitchGetEmotes</c>
    /// request - confirmed live to aggregate all four sources with ready
    /// image URLs, so nothing platform-specific needs to live here beyond
    /// asking for it. Called both when a stream freshly connects and when
    /// settings change without the stream needing to restart (chat can be
    /// turned on while an already-connected stream keeps running), so it
    /// guards its own idempotency rather than relying on either caller to.
    /// <para>
    /// Fetching from Streamer.bot and delivering to the worker are guarded
    /// separately. The event stream routinely connects before the SteamVR
    /// worker has finished starting, so a single delivery attempt that loses
    /// that race would otherwise fetch a perfectly good catalog and then
    /// silently drop it forever - confirmed live: the very first run logged
    /// exactly that. Only the fetch is marked done-once; delivery retries on
    /// a bounded delay until a worker exists to receive it.
    /// </para>
    /// </summary>
    private async Task EnsureEmoteCatalogAsync()
    {
        var stream = _eventStream;
        if (stream is null || !_settings.ChatEnabled || stream.State != StreamerBotStreamState.Connected)
        {
            return;
        }

        lock (_emoteCatalogGate)
        {
            if (ReferenceEquals(_emoteCatalogFetchedFor, stream))
            {
                return;
            }

            _emoteCatalogFetchedFor = stream;
        }

        try
        {
            using var response = await stream.SendRequestAsync("TwitchGetEmotes", CancellationToken.None);
            var catalog = TwitchEmoteCatalog.Parse(response.RootElement);
            OnActivity(
                new BridgeActivity(
                    "streamerbot.emote_catalog",
                    $"Loaded {catalog.Count} Twitch/BTTV/FFZ/7TV emotes from Streamer.bot."));

            for (var attempt = 0; attempt < EmoteCatalogDeliveryAttempts; attempt++)
            {
                if (_engine.SetEmoteCatalog(catalog.ImageUrlsByName))
                {
                    return;
                }

                await Task.Delay(EmoteCatalogRetryDelay);
            }

            OnActivity(
                new BridgeActivity(
                    "openvr.emote_catalog_unavailable",
                    "The Twitch emote catalog could not be delivered - no SteamVR session became "
                    + "available in time.",
                    BridgeLogLevel.Debug));
        }
        catch (Exception exception)
        {
            // Emote images sit on top of chat, which already works without
            // them - a failure here must not affect chat itself. The guard
            // above means this will not retry until the stream reconnects,
            // an acceptable cadence for something this infrequently needed.
            OnActivity(
                new BridgeActivity(
                    "streamerbot.emote_catalog_failed",
                    $"Could not load the Twitch emote catalog: {exception.Message}",
                    BridgeLogLevel.Warning));
        }
    }

    private async Task StopEventStreamLockedAsync()
    {
        var stream = _eventStream;
        var pump = _eventPump;
        _eventStream = null;
        _eventPump = null;
        _appliedEventStream = null;

        if (stream is not null)
        {
            await stream.DisposeAsync();
        }

        if (pump is not null)
        {
            // Disposal completes the channel, so the pump is already ending;
            // waiting keeps two feeds from writing to the log at once when
            // settings are saved twice in quick succession.
            await pump;
        }
    }

    private async Task ConsumeEventStreamAsync(StreamerBotEventStream stream)
    {
        var count = 0L;

        try
        {
            await foreach (var received in stream.Events.ReadAllAsync())
            {
                count++;

                // What the feed is doing, with nothing in it that identifies a
                // viewer or repeats what they said. This is the line that is
                // kept, and it is enough to tell a dead feed from a quiet one.
                OnActivity(
                    new BridgeActivity(
                        "streamerbot.event",
                        $"Streamer.bot sent a {Describe(received.Payload.Target)} payload "
                        + $"({count} this session)."));

                // The payload itself, for watching chat arrive while setting the
                // Streamer.bot side up. Debug keeps it out of the window and
                // ContainsUserContent keeps it off disk, so it reaches only a
                // developer reading the console host.
                OnActivity(
                    new BridgeActivity(
                        "streamerbot.event_payload",
                        $"Streamer.bot {Summarise(received)}",
                        BridgeLogLevel.Debug,
                        ContainsUserContent: true));

                if (received.Payload.Target == StreamerBotEventTarget.Notification
                    && _settings.NotificationsEnabled
                    && !_engine.ShowNotification(received.Payload))
                {
                    // Routine rather than a warning: this only means no worker
                    // is currently running to draw on, which happens between
                    // launch and Ready like any other display surface.
                    OnActivity(
                        new BridgeActivity(
                            "openvr.notification_unavailable",
                            "A notification arrived with no SteamVR session to show it on.",
                            BridgeLogLevel.Debug));
                }

                if (received.Payload.Target == StreamerBotEventTarget.Chat
                    && _settings.ChatEnabled
                    && !_engine.ShowChatMessage(received.Payload))
                {
                    // Routine, for the same reason as the notification branch
                    // above.
                    OnActivity(
                        new BridgeActivity(
                            "openvr.chat_unavailable",
                            "A chat message arrived with no SteamVR session to show it on.",
                            BridgeLogLevel.Debug));
                }
            }
        }
        catch (Exception exception)
        {
            OnActivity(
                new BridgeActivity(
                    "streamerbot.event_pump_stopped",
                    $"Stopped reading Streamer.bot events: {exception.Message}",
                    BridgeLogLevel.Warning));
        }
    }

    /// <summary>
    /// The payload kind, which is this app's own routing decision rather than
    /// anything the viewer supplied, so it is safe to retain.
    /// </summary>
    private static string Describe(StreamerBotEventTarget target) => target switch
    {
        StreamerBotEventTarget.Chat => "chat",
        StreamerBotEventTarget.Notification => "notification",
        StreamerBotEventTarget.Control => "control",
        _ => "unrecognised"
    };

    /// <summary>
    /// One activity line per payload. Long messages are cut here rather than in
    /// the parser: the full text belongs to the VR window that Phase 1 onwards
    /// builds, and only this log line has a width to respect.
    /// </summary>
    private static string Summarise(StreamerBotEvent received)
    {
        const int maximumLength = 240;
        var description = received.Payload.Describe().ReplaceLineEndings(" ");
        return description.Length <= maximumLength
            ? description
            : description[..maximumLength] + "…";
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
            await StopEventStreamLockedAsync();
        }
        finally
        {
            _runtimeGate.Release();
        }

        _trayIcon.Visible = false;
        _mainForm.AllowCloseAndClose();
        ExitThread();
    }

    /// <summary>
    /// SteamVR is closing, and the app is registered to launch with it, so it
    /// closes too rather than lingering in the tray with nothing to talk to.
    /// </summary>
    private void OnVrShutdownRequested()
    {
        if (_mainForm.InvokeRequired)
        {
            _mainForm.BeginInvoke(OnVrShutdownRequested);
            return;
        }

        ExitApplication();
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

    /// <summary>
    /// Turns the Phase 1 test overlay on or off. The menu item's own check
    /// state is corrected from the result rather than trusted, so a failed
    /// request does not leave the menu claiming an overlay is on screen.
    /// </summary>
    private void ToggleTestOverlay(ToolStripMenuItem item)
    {
        try
        {
            if (_engine.SetTestOverlayEnabled(item.Checked))
            {
                return;
            }

            item.Checked = false;
            OnActivity(
                new BridgeActivity(
                    "openvr.test_overlay_unavailable",
                    "Start SteamVR before showing the VR test overlay.",
                    BridgeLogLevel.Warning));
        }
        catch (Exception exception)
        {
            item.Checked = false;
            OnActivity(
                new BridgeActivity(
                    "openvr.test_overlay_failed",
                    $"The VR test overlay could not be shown: {exception.Message}",
                    BridgeLogLevel.Warning));
        }
    }

    private void OnActivity(BridgeActivity activity)
    {
        // Debug lines are the dropped-frame diagnostics from the event feed.
        // A badly written Streamer.bot action can produce one per message, so
        // they go to the log file for support and stay out of the window.
        if (activity.Level != BridgeLogLevel.Debug)
        {
            _mainForm.AddActivity(activity.Message);
        }

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

    /// <summary>The only settings the event feed is built from.</summary>
    private sealed record EventStreamSettings(
        bool Enabled,
        string Address,
        string Password);

    private const int ShowWindowNormal = 1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
