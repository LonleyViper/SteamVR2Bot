using System.Diagnostics;

namespace SvrBridge.Core;

public enum BridgeState
{
    Stopped,
    Starting,
    Ready,
    Sending,
    Error
}

public sealed record BridgeStatus(
    BridgeState State,
    string FriendlyName,
    string Detail);

public sealed class BridgeEngine
{
    private static readonly TimeSpan[] SteamVrRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly IOpenVrSessionFactory _openVrSessionFactory;
    private readonly object _inputGate = new();
    private readonly object _shortcutGate = new();
    private IOpenVrSession? _currentInput;
    private IOpenVrSession? _dashboardInput;
    private CancellationTokenSource? _dashboardMonitorCancellation;
    private string? _streamerBotAttentionDetail;
    private ShortcutConfig[] _runtimeShortcuts = [];
    private long _runtimeShortcutVersion;

    public BridgeEngine(IOpenVrSessionFactory? openVrSessionFactory = null)
    {
        _openVrSessionFactory =
            openVrSessionFactory ?? new InProcessOpenVrSessionFactory();
    }

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<BridgeActivity>? Activity;
    public event Action<ControllerSetup>? ControllerSetupChanged;
    public event Action<ShortcutConfig>? ShortcutCreated;
    public event Action<string>? ShortcutDeleted;
    public event Action<VrSettingsSnapshot>? VrSettingsChanged;
    public event Action? VrShutdownRequested;

    public async Task RunAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var actionManifest = OpenVrInput.ResolveActionManifest(config.ActionManifestPath);
        var retryAttempt = 0;
        _streamerBotAttentionDetail = null;
        ReplaceRuntimeShortcuts(config.GetShortcuts());

        while (!cancellationToken.IsCancellationRequested)
        {
            SetStatus(
                BridgeState.Starting,
                retryAttempt == 0 ? "Starting…" : "Reconnecting to SteamVR…",
                "Preparing your controller shortcut.");

            try
            {
                await RunSteamVrSessionAsync(
                    config,
                    actionManifest,
                    cancellationToken);
                retryAttempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SteamVrShutdownException)
            {
                Log("steamvr.shutdown", "SteamVR closed, so SteamVR2Bot is closing too.");
                SetStatus(
                    BridgeState.Stopped,
                    "SteamVR closed",
                    "SteamVR2Bot closed with SteamVR.");
                VrShutdownRequested?.Invoke();
                return;
            }
            catch (Exception exception)
            {
                var delay = SteamVrRetryDelays[
                    Math.Min(retryAttempt, SteamVrRetryDelays.Length - 1)];
                retryAttempt++;
                Log(
                    "steamvr.reconnect",
                    $"SteamVR connection lost: {exception.Message}",
                    BridgeLogLevel.Warning);
                SetStatus(
                    BridgeState.Error,
                    "Waiting for SteamVR",
                    $"Start or restart SteamVR. SteamVR2Bot will retry in {delay.TotalSeconds:0} second(s).");
                await Task.Delay(delay, cancellationToken);
            }
        }

        SetStatus(
            BridgeState.Stopped,
            "Stopped",
            "The controller shortcut is not running.");
    }

    public void UpdateShortcuts(IReadOnlyList<ShortcutConfig> shortcuts)
    {
        ReplaceRuntimeShortcuts(shortcuts);
        Log(
            "runtime.shortcuts_updated",
            $"{shortcuts.Count} shortcut{(shortcuts.Count == 1 ? "" : "s")} applied without restarting SteamVR.");
    }

    public async Task TestActionAsync(
        AppConfig config,
        CancellationToken cancellationToken)
        => await TestActionAsync(
            config,
            config.GetShortcuts().First(),
            cancellationToken);

    public async Task TestActionAsync(
        AppConfig config,
        ShortcutConfig shortcut,
        CancellationToken cancellationToken)
    {
        SetStatus(
            BridgeState.Sending,
            "Testing Streamer.bot…",
            $"Running “{FriendlyActionName(shortcut)}”.");

        try
        {
            await using var streamerBot = new StreamerBotClient(
                config.StreamerBot,
                message => Log("streamerbot.connection", message));
            await streamerBot.TriggerAsync(
                $"test:{shortcut.Id}",
                shortcut.ActionId,
                shortcut.ActionName,
                cancellationToken);
            Log(
                "streamerbot.test_confirmed",
                $"Test confirmed: {FriendlyActionName(shortcut)}");
            SetStatus(
                BridgeState.Ready,
                "Test successful",
                "Streamer.bot confirmed the action.");
        }
        catch (Exception exception)
        {
            SetStatus(
                BridgeState.Error,
                "Test failed",
                FriendlyErrorDetail(exception));
            throw;
        }
    }

    public async Task<RecordedGesture> RecordGestureAsync(
        AppConfig config,
        ChordMode mode,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        lock (_inputGate)
        {
            _dashboardMonitorCancellation?.Cancel();
            _dashboardMonitorCancellation?.Dispose();
            _dashboardMonitorCancellation = null;
            _dashboardInput?.Dispose();
            _dashboardInput = null;
        }

        var actionManifest = OpenVrInput.ResolveActionManifest(config.ActionManifestPath);
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(timeout);
        var token = timeoutCancellation.Token;

        SetStatus(
            BridgeState.Starting,
            "Listening for your controller input…",
            mode
                is ChordMode.SinglePress
                or ChordMode.LongPress
                or ChordMode.DoublePress
                ? "Release all buttons, then press the button you want to use."
                : "Release the buttons, then hold the first input and press the second.");

        using var input = await _openVrSessionFactory.ConnectAsync(
            config,
            actionManifest,
            message => Log("openvr.recording", message),
            token);
        var setup = input.GetControllerSetup();

        // Begin from a released state so an already-held system button is not
        // mistaken for part of the shortcut.
        while (ControllerInputs.PressedInputs(input.Poll(), setup).Count > 0)
        {
            await Task.Delay(20, token);
        }

        ControllerInputBinding? first = null;
        while (first is null)
        {
            var pressed = ControllerInputs.PressedInputs(input.Poll(), setup);
            if (mode
                    is ChordMode.SinglePress
                    or ChordMode.LongPress
                    or ChordMode.DoublePress
                && pressed.FirstOrDefault() is { } heldInput)
            {
                return FinishRecording(heldInput, heldInput, setup, mode);
            }

            if (pressed.Count >= 2)
            {
                return FinishRecording(pressed[0], pressed[1], setup, mode);
            }

            first = pressed.FirstOrDefault();
            await Task.Delay(20, token);
        }

        while (true)
        {
            var pressed = ControllerInputs.PressedInputs(input.Poll(), setup);
            var second = pressed.FirstOrDefault(candidate =>
                !candidate.Id.Equals(first.Id, StringComparison.OrdinalIgnoreCase));
            if (second is not null)
            {
                return FinishRecording(first, second, setup, mode);
            }

            await Task.Delay(20, token);
        }
    }

    public async Task OpenBindingUiAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_inputGate)
        {
            if (_currentInput is not null)
            {
                _currentInput.OpenBindingUi();
                return;
            }
        }

        var actionManifest = OpenVrInput.ResolveActionManifest(
            config.ActionManifestPath);
        using var temporaryInput = await _openVrSessionFactory.ConnectAsync(
            config,
            actionManifest,
            message => Log("openvr", message),
            cancellationToken);
        _ = temporaryInput.Poll();
        temporaryInput.OpenBindingUi();
    }

    /// <summary>
    /// Turns the development test overlay on or off on the running worker.
    /// <para>
    /// Unlike the binding UI this does not fall back to a temporary session.
    /// The overlay only means anything while it is on screen, and a session
    /// created here would be disposed immediately - taking the overlay with it.
    /// </para>
    /// </summary>
    /// <returns>False when no SteamVR session is running to show it on.</returns>
    public bool SetTestOverlayEnabled(bool enabled)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.SetTestOverlayEnabled(enabled);
            return true;
        }
    }

    /// <summary>
    /// Queues a notification on the running worker's overlay.
    /// <para>
    /// Like <see cref="SetTestOverlayEnabled"/> this never falls back to a
    /// temporary session: a notification means nothing once the session
    /// showing it is immediately disposed, and only the worker actually
    /// driving the headset can display one.
    /// </para>
    /// </summary>
    /// <returns>False when no SteamVR session is running to show it on.</returns>
    public bool ShowNotification(StreamerBotEventPayload payload)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.ShowNotification(payload);
            return true;
        }
    }

    /// <summary>
    /// Appends one message to the running worker's chat window.
    /// <para>
    /// Like <see cref="ShowNotification"/> this never falls back to a
    /// temporary session: a chat message means nothing once the session
    /// showing it is immediately disposed, and only the worker actually
    /// driving the headset owns the chat overlay's ring buffer.
    /// </para>
    /// </summary>
    /// <returns>False when no SteamVR session is running to show it on.</returns>
    public bool ShowChatMessage(StreamerBotEventPayload payload)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.ShowChatMessage(payload);
            return true;
        }
    }

    /// <summary>
    /// Developer-only override: puts the SteamVR dashboard back on
    /// <c>SetOverlayTexture</c> so the finding that a dashboard overlay handle
    /// never displays one can be re-checked after a SteamVR update.
    /// </summary>
    /// <returns>False when no SteamVR session is running to switch.</returns>
    public bool SetDashboardTexturePathEnabled(bool enabled)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.SetDashboardTexturePathEnabled(enabled);
            return true;
        }
    }

    /// <summary>
    /// Developer-only override: switches the chat window, notifications and
    /// the VR test overlay between the default <c>SetOverlayRaw</c> path and
    /// the persistent-texture path, together - see
    /// <c>OverlayTextureUploader.TexturePathEnabled</c>. Unlike the dashboard
    /// override this defaults false-to-true, not true-to-false: those three
    /// surfaces ship with the texture path off until a developer opts in.
    /// </summary>
    /// <returns>False when no SteamVR session is running to switch.</returns>
    public bool SetOverlayTexturePathEnabled(bool enabled)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.SetOverlayTexturePathEnabled(enabled);
            return true;
        }
    }

    /// <summary>
    /// Hands the worker a fresh Twitch/BetterTTV/FrankerFaceZ/7TV emote name
    /// → image URL lookup, fetched once by the tray process from
    /// Streamer.bot's own <c>TwitchGetEmotes</c> request.
    /// </summary>
    /// <returns>False when no SteamVR session is running to use it.</returns>
    public bool SetEmoteCatalog(IReadOnlyDictionary<string, string> catalog)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.SetEmoteCatalog(catalog);
            return true;
        }
    }

    /// <summary>
    /// Applies a Streamer.bot control payload (show/hide/clear/anchor/reset)
    /// to the running worker's overlay surfaces. Like
    /// <see cref="ShowNotification"/> this never falls back to a temporary
    /// session: a control command means nothing once the session applying it
    /// is immediately disposed.
    /// </summary>
    /// <returns>False when no SteamVR session is running to apply it to.</returns>
    public bool ApplyControlCommand(StreamerBotEventPayload payload)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.ApplyControlCommand(payload);
            return true;
        }
    }

    /// <summary>
    /// Pushes a desktop-made appearance/anchor/enable change to the running
    /// worker live, without a restart - the opposite direction of a VR
    /// settings-page edit. Like <see cref="ApplyControlCommand"/> this never
    /// falls back to a temporary session.
    /// </summary>
    /// <returns>False when no SteamVR session is running to apply it to.</returns>
    public bool ApplySettingsChange(VrSettingsSnapshot settings)
    {
        lock (_inputGate)
        {
            if (_currentInput is null)
            {
                return false;
            }

            _currentInput.ApplySettingsChange(settings);
            return true;
        }
    }

    public async Task ShowDashboardAsync(
        AppConfig config,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        bool activate = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_inputGate)
        {
            if (_currentInput is not null)
            {
                _currentInput.ShowDashboard(shortcuts, actions, activate);
                PublishCreatedShortcuts(_currentInput);
                return;
            }

            if (_dashboardInput is not null)
            {
                _dashboardInput.ShowDashboard(shortcuts, actions, activate);
                PublishCreatedShortcuts(_dashboardInput);
                return;
            }
        }

        var actionManifest = OpenVrInput.ResolveActionManifest(
            config.ActionManifestPath);
        var dashboardInput = await _openVrSessionFactory.ConnectAsync(
            config,
            actionManifest,
            message => Log("openvr.dashboard", message),
            cancellationToken);
        try
        {
            dashboardInput.ShowDashboard(shortcuts, actions, activate);
            lock (_inputGate)
            {
                _dashboardInput = dashboardInput;
                _dashboardMonitorCancellation?.Cancel();
                _dashboardMonitorCancellation?.Dispose();
                _dashboardMonitorCancellation = new CancellationTokenSource();
                _ = MonitorDashboardAsync(
                    dashboardInput,
                    _dashboardMonitorCancellation.Token);
            }
        }
        catch
        {
            dashboardInput.Dispose();
            throw;
        }
    }

    private async Task RunSteamVrSessionAsync(
        AppConfig config,
        string actionManifest,
        CancellationToken cancellationToken)
    {
        lock (_inputGate)
        {
            _dashboardMonitorCancellation?.Cancel();
            _dashboardMonitorCancellation?.Dispose();
            _dashboardMonitorCancellation = null;
            _dashboardInput?.Dispose();
            _dashboardInput = null;
        }

        var openVr = await _openVrSessionFactory.ConnectAsync(
            config,
            actionManifest,
            message => Log("openvr", message),
            cancellationToken);
        lock (_inputGate)
        {
            _currentInput = openVr;
        }

        try
        {
            var (shortcuts, shortcutVersion) = RuntimeShortcuts();
            var detectors = shortcuts.ToDictionary(
                shortcut => shortcut.Id,
                shortcut => new ChordDetector(shortcut.Gesture));
            var stopwatch = Stopwatch.StartNew();
            var bindingRefresh = Stopwatch.StartNew();
            var previous = new InputSnapshot(false, false);
            string? previousSetupSignature = null;
            ControllerSetup currentSetup;

            await using var streamerBot = new StreamerBotClient(
                config.StreamerBot,
                message => Log("streamerbot.connection", message));

            lock (_inputGate)
            {
                _ = openVr.Poll();
                currentSetup = openVr.GetControllerSetup();
            }

            PublishControllerSetup(
                currentSetup,
                shortcuts,
                ref previousSetupSignature);

            while (!cancellationToken.IsCancellationRequested)
            {
                if (openVr.IsQuitRequested())
                {
                    throw new SteamVrShutdownException();
                }

                InputSnapshot snapshot;
                lock (_inputGate)
                {
                    snapshot = openVr.Poll();
                }

                if (bindingRefresh.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    lock (_inputGate)
                    {
                        currentSetup = openVr.GetControllerSetup();
                    }

                    PublishControllerSetup(
                        currentSetup,
                        shortcuts,
                        ref previousSetupSignature);
                    bindingRefresh.Restart();
                }

                if (config.LogRawInputChanges && snapshot != previous)
                {
                    var physicalInputs = ControllerInputs.PressedInputs(
                        snapshot,
                        currentSetup);
                    Log(
                        "controller.input",
                        $"Safety input {(snapshot.ButtonOne ? "held" : "released")}; " +
                        $"action input {(snapshot.ButtonTwo ? "pressed" : "released")}; " +
                        $"physical inputs: " +
                        $"{(physicalInputs.Count == 0 ? "none" : string.Join(", ", physicalInputs.Select(input => input.FriendlyName)))}.");
                    previous = snapshot;
                }

                var latestShortcuts = RuntimeShortcuts();
                if (latestShortcuts.Version != shortcutVersion)
                {
                    shortcuts = latestShortcuts.Shortcuts;
                    shortcutVersion = latestShortcuts.Version;
                    detectors = shortcuts.ToDictionary(
                        shortcut => shortcut.Id,
                        shortcut => new ChordDetector(
                            shortcut.Gesture,
                            requireReleaseBeforeArmed: true));
                    SetReadyStatus(currentSetup, shortcuts);
                }

                foreach (var shortcut in shortcuts)
                {
                    if (detectors[shortcut.Id].Update(
                            shortcut.SafetyInput.IsPressed(snapshot),
                            shortcut.ActionInput.IsPressed(snapshot),
                            stopwatch.ElapsedMilliseconds))
                    {
                        await DeliverActionAsync(
                            streamerBot,
                            shortcut,
                            shortcuts,
                            currentSetup,
                            cancellationToken);
                    }
                }

                PublishCreatedShortcuts(openVr);

                await Task.Delay(config.PollIntervalMs, cancellationToken);
            }
        }
        catch (Exception exception)
            when (exception is not (OperationCanceledException
                      or SteamVrShutdownException)
                  && openVr.IsQuitRequested())
        {
            // The worker reports the quit and then exits at once, so the
            // session can fail before the loop notices. That is still a
            // shutdown rather than a dropped connection worth retrying.
            throw new SteamVrShutdownException();
        }
        finally
        {
            lock (_inputGate)
            {
                if (ReferenceEquals(_currentInput, openVr))
                {
                    _currentInput = null;
                }

                openVr.Dispose();
            }
        }
    }

    private async Task MonitorDashboardAsync(
        IOpenVrSession input,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_inputGate)
                {
                    if (!ReferenceEquals(_dashboardInput, input))
                    {
                        return;
                    }

                    _ = input.Poll();
                    PublishCreatedShortcuts(input);
                }

                await Task.Delay(100, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Dashboard monitoring stopped normally.
        }
        catch (Exception exception)
        {
            Log(
                "dashboard.monitor_stopped",
                $"VR dashboard closed: {exception.Message}",
                BridgeLogLevel.Warning);
        }
    }

    private void PublishCreatedShortcuts(IOpenVrSession input)
    {
        foreach (var shortcut in input.DrainCreatedShortcuts())
        {
            ShortcutCreated?.Invoke(shortcut);
        }

        foreach (var shortcutId in input.DrainDeletedShortcutIds())
        {
            ShortcutDeleted?.Invoke(shortcutId);
        }

        foreach (var settings in input.DrainVrSettingsChanges())
        {
            VrSettingsChanged?.Invoke(settings);
        }
    }

    private async Task DeliverActionAsync(
        StreamerBotClient streamerBot,
        ShortcutConfig shortcut,
        ShortcutConfig[] shortcuts,
        ControllerSetup setup,
        CancellationToken cancellationToken)
    {
        SetStatus(
            BridgeState.Sending,
            "Running your action…",
            $"Running “{FriendlyActionName(shortcut)}” in Streamer.bot.");

        try
        {
            await streamerBot.TriggerAsync(
                shortcut.Id,
                shortcut.ActionId,
                shortcut.ActionName,
                cancellationToken);
            _streamerBotAttentionDetail = null;
            Log(
                "streamerbot.action_confirmed",
                $"Action confirmed: {FriendlyActionName(shortcut)}");
            SetReadyStatus(setup, shortcuts);
        }
        catch (StreamerBotDeliveryException exception)
        {
            var detail = exception.DeliveryMayHaveOccurred
                ? "Streamer.bot did not confirm the action. It was not retried automatically, preventing a possible duplicate."
                : "Streamer.bot is unavailable. The next controller shortcut will try to reconnect.";
            _streamerBotAttentionDetail = detail;
            Log(
                "streamerbot.delivery_failed",
                detail,
                BridgeLogLevel.Warning);
            SetStatus(
                BridgeState.Error,
                "Streamer.bot needs attention",
                detail);
        }
    }

    private void PublishControllerSetup(
        ControllerSetup setup,
        ShortcutConfig[] shortcuts,
        ref string? previousSignature)
    {
        var signature = string.Join(
            "|",
            setup.Availability,
            setup.FriendlySummary,
            string.Join(
                ",",
                setup.Controllers.Select(controller =>
                    $"{controller.Hand}:{controller.ControllerType}:{controller.Model}")));
        if (signature == previousSignature)
        {
            return;
        }

        previousSignature = signature;
        ControllerSetupChanged?.Invoke(setup);
        Log(
            "controller.setup",
            setup.FriendlySummary,
            setup.Availability == BindingAvailability.NeedsSetup
                ? BridgeLogLevel.Warning
                : BridgeLogLevel.Info);
        SetReadyStatus(setup, shortcuts);
    }

    private void SetReadyStatus(
        ControllerSetup setup,
        ShortcutConfig[] enabledShortcuts)
    {
        if (_streamerBotAttentionDetail is not null)
        {
            SetStatus(
                BridgeState.Error,
                "Streamer.bot needs attention",
                _streamerBotAttentionDetail);
            return;
        }

        var directPhysicalShortcuts = enabledShortcuts.Length > 0
                                      && enabledShortcuts.All(shortcut =>
                                          ControllerInputBinding.TryParsePhysical(
                                              shortcut.SafetyInput.Id,
                                              out _,
                                              out _)
                                          && (shortcut.Gesture.Mode
                                                  is ChordMode.SinglePress
                                                  or ChordMode.LongPress
                                                  or ChordMode.DoublePress
                                              || ControllerInputBinding.TryParsePhysical(
                                                  shortcut.ActionInput.Id,
                                                  out _,
                                                  out _)));

        switch (setup.Availability)
        {
            case BindingAvailability.NeedsSetup
                when enabledShortcuts.Length > 0 && !directPhysicalShortcuts:
                SetStatus(
                    BridgeState.Error,
                    "Controller setup needed",
                    setup.FriendlySummary);
                break;
            case BindingAvailability.Unknown:
                SetStatus(
                    BridgeState.Ready,
                    "Waiting for controllers",
                    setup.FriendlySummary);
                break;
            default:
                SetStatus(
                    BridgeState.Ready,
                    "Ready for your shortcut",
                    enabledShortcuts.Length switch
                    {
                        0 => "No shortcuts yet. Add one on the desktop or in the SteamVR dashboard.",
                        1 => $"{enabledShortcuts[0].FriendlyGesture} → " +
                             $"{FriendlyActionName(enabledShortcuts[0])}.",
                        _ => $"{enabledShortcuts.Length} shortcuts are ready. " +
                             $"Example: {enabledShortcuts[0].FriendlyGesture}."
                    });
                break;
        }
    }

    private void ReplaceRuntimeShortcuts(
        IReadOnlyList<ShortcutConfig> shortcuts)
    {
        lock (_shortcutGate)
        {
            _runtimeShortcuts = shortcuts
                .Where(shortcut => shortcut.Enabled)
                .ToArray();
            _runtimeShortcutVersion++;
        }
    }

    private (ShortcutConfig[] Shortcuts, long Version) RuntimeShortcuts()
    {
        lock (_shortcutGate)
        {
            return (_runtimeShortcuts, _runtimeShortcutVersion);
        }
    }

    private void Log(
        string eventName,
        string message,
        BridgeLogLevel level = BridgeLogLevel.Info) =>
        Activity?.Invoke(new BridgeActivity(eventName, message, level));

    private void SetStatus(BridgeState state, string friendlyName, string detail) =>
        StatusChanged?.Invoke(new BridgeStatus(state, friendlyName, detail));

    private static string FriendlyActionName(StreamerBotConfig config) =>
        string.IsNullOrWhiteSpace(config.ActionName)
            ? "Selected action"
            : config.ActionName.Trim();

    private static string FriendlyActionName(ShortcutConfig shortcut) =>
        string.IsNullOrWhiteSpace(shortcut.ActionName)
            ? "Selected action"
            : shortcut.ActionName.Trim();

    private RecordedGesture FinishRecording(
        ControllerInputBinding safetyInput,
        ControllerInputBinding actionInput,
        ControllerSetup setup,
        ChordMode mode)
    {
        var gesture = new RecordedGesture(
            safetyInput,
            actionInput,
            ControllerInputs.ControllerFamily(setup));
        Log(
            "controller.gesture_recorded",
            mode switch
            {
                ChordMode.SinglePress =>
                    $"Recorded press of {safetyInput.FriendlyName}.",
                ChordMode.LongPress =>
                    $"Recorded long press of {safetyInput.FriendlyName}.",
                ChordMode.DoublePress =>
                    $"Recorded double press of {safetyInput.FriendlyName}.",
                _ => $"Recorded {safetyInput.FriendlyName} + {actionInput.FriendlyName}."
            });
        var singleInput = mode
            is ChordMode.SinglePress
            or ChordMode.LongPress
            or ChordMode.DoublePress;
        SetStatus(
            BridgeState.Stopped,
            singleInput ? "Input recorded" : "Inputs recorded",
            mode switch
            {
                ChordMode.SinglePress =>
                    $"Press {safetyInput.FriendlyName} to run the action.",
                ChordMode.LongPress =>
                    $"Hold {safetyInput.FriendlyName} to run the action.",
                ChordMode.DoublePress =>
                    $"Double press {safetyInput.FriendlyName} to run the action.",
                _ =>
                    $"Hold {safetyInput.FriendlyName}, then press {actionInput.FriendlyName}."
            });
        return gesture;
    }

    private static string FriendlyErrorDetail(Exception exception)
    {
        if (exception.Message.Contains("authentication", StringComparison.OrdinalIgnoreCase))
        {
            return "Check the optional Streamer.bot password in Settings.";
        }

        if (exception.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase)
            || exception.Message.Contains("connection", StringComparison.OrdinalIgnoreCase))
        {
            return "Check that Streamer.bot is running and its WebSocket address is correct.";
        }

        return exception.Message;
    }
}
