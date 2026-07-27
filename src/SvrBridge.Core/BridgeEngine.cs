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
    private IOpenVrSession? _currentInput;
    private IOpenVrSession? _dashboardInput;
    private CancellationTokenSource? _dashboardMonitorCancellation;
    private string? _streamerBotAttentionDetail;

    public BridgeEngine(IOpenVrSessionFactory? openVrSessionFactory = null)
    {
        _openVrSessionFactory =
            openVrSessionFactory ?? new InProcessOpenVrSessionFactory();
    }

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<BridgeActivity>? Activity;
    public event Action<ControllerSetup>? ControllerSetupChanged;
    public event Action<ShortcutConfig>? ShortcutCreated;

    public async Task RunAsync(AppConfig config, CancellationToken cancellationToken)
    {
        var actionManifest = OpenVrInput.ResolveActionManifest(config.ActionManifestPath);
        var retryAttempt = 0;
        _streamerBotAttentionDetail = null;

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
                    $"Start or restart SteamVR. SVR Bridge will retry in {delay.TotalSeconds:0} second(s).");
                await Task.Delay(delay, cancellationToken);
            }
        }

        SetStatus(
            BridgeState.Stopped,
            "Stopped",
            "The controller shortcut is not running.");
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
            "Listening for your controller inputs…",
            "Release the buttons, then hold your safety input and press the action input.");

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
            if (pressed.Count >= 2)
            {
                return FinishRecording(pressed[0], pressed[1], setup);
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
                return FinishRecording(first, second, setup);
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

    public async Task ShowDashboardAsync(
        AppConfig config,
        string imagePath,
        IReadOnlyList<ShortcutConfig> shortcuts,
        IReadOnlyList<StreamerBotAction> actions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_inputGate)
        {
            if (_currentInput is not null)
            {
                _currentInput.ShowDashboard(imagePath, shortcuts, actions);
                PublishCreatedShortcuts(_currentInput);
                return;
            }

            if (_dashboardInput is not null)
            {
                _dashboardInput.ShowDashboard(imagePath, shortcuts, actions);
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
            dashboardInput.ShowDashboard(imagePath, shortcuts, actions);
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
            var shortcuts = config.GetShortcuts()
                .Where(shortcut => shortcut.Enabled)
                .ToArray();
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

            PublishControllerSetup(currentSetup, config, ref previousSetupSignature);

            while (!cancellationToken.IsCancellationRequested)
            {
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
                        config,
                        ref previousSetupSignature);
                    bindingRefresh.Restart();
                }

                if (config.LogRawInputChanges && snapshot != previous)
                {
                    Log(
                        "controller.input",
                        $"Safety input {(snapshot.ButtonOne ? "held" : "released")}; " +
                        $"action input {(snapshot.ButtonTwo ? "pressed" : "released")}.");
                    previous = snapshot;
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
                            config,
                            shortcut,
                            currentSetup,
                            cancellationToken);
                    }
                }

                PublishCreatedShortcuts(openVr);

                await Task.Delay(config.PollIntervalMs, cancellationToken);
            }
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
    }

    private async Task DeliverActionAsync(
        StreamerBotClient streamerBot,
        AppConfig config,
        ShortcutConfig shortcut,
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
            SetReadyStatus(setup, config);
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
        AppConfig config,
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
        SetReadyStatus(setup, config);
    }

    private void SetReadyStatus(ControllerSetup setup, AppConfig config)
    {
        if (_streamerBotAttentionDetail is not null)
        {
            SetStatus(
                BridgeState.Error,
                "Streamer.bot needs attention",
                _streamerBotAttentionDetail);
            return;
        }

        switch (setup.Availability)
        {
            case BindingAvailability.NeedsSetup:
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
                    $"{config.GetShortcuts().Count(shortcut => shortcut.Enabled)} shortcut(s) ready. " +
                    setup.FriendlySummary);
                break;
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
        ControllerSetup setup)
    {
        var gesture = new RecordedGesture(
            safetyInput,
            actionInput,
            ControllerInputs.ControllerFamily(setup));
        Log(
            "controller.gesture_recorded",
            $"Recorded {safetyInput.FriendlyName} + {actionInput.FriendlyName}.");
        SetStatus(
            BridgeState.Stopped,
            "Inputs recorded",
            $"Hold {safetyInput.FriendlyName}, then press {actionInput.FriendlyName}.");
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
