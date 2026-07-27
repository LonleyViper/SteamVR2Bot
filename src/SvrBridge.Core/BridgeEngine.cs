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

    private readonly object _inputGate = new();
    private OpenVrInput? _currentInput;
    private string? _streamerBotAttentionDetail;

    public event Action<BridgeStatus>? StatusChanged;
    public event Action<BridgeActivity>? Activity;
    public event Action<ControllerSetup>? ControllerSetupChanged;

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
    {
        SetStatus(
            BridgeState.Sending,
            "Testing Streamer.bot…",
            $"Running “{FriendlyActionName(config.StreamerBot)}”.");

        try
        {
            await using var streamerBot = new StreamerBotClient(
                config.StreamerBot,
                message => Log("streamerbot.connection", message));
            await streamerBot.TriggerAsync("settings_test", cancellationToken);
            Log(
                "streamerbot.test_confirmed",
                $"Test confirmed: {FriendlyActionName(config.StreamerBot)}");
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

    public Task OpenBindingUiAsync(
        AppConfig config,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
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
                using var temporaryInput = new OpenVrInput(
                    config.OpenVrDllPath,
                    actionManifest,
                    message => Log("openvr", message));
                _ = temporaryInput.Poll();
                temporaryInput.OpenBindingUi();
            },
            cancellationToken);

    private async Task RunSteamVrSessionAsync(
        AppConfig config,
        string actionManifest,
        CancellationToken cancellationToken)
    {
        var openVr = new OpenVrInput(
            config.OpenVrDllPath,
            actionManifest,
            message => Log("openvr", message));
        lock (_inputGate)
        {
            _currentInput = openVr;
        }

        try
        {
            var detector = new ChordDetector(config.Chord);
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

                if (detector.Update(
                        snapshot.ButtonOne,
                        snapshot.ButtonTwo,
                        stopwatch.ElapsedMilliseconds))
                {
                    await DeliverActionAsync(
                        streamerBot,
                        config,
                        currentSetup,
                        cancellationToken);
                }

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

    private async Task DeliverActionAsync(
        StreamerBotClient streamerBot,
        AppConfig config,
        ControllerSetup setup,
        CancellationToken cancellationToken)
    {
        SetStatus(
            BridgeState.Sending,
            "Running your action…",
            $"Running “{FriendlyActionName(config.StreamerBot)}” in Streamer.bot.");

        try
        {
            await streamerBot.TriggerAsync(
                "safety_input+action_input",
                cancellationToken);
            _streamerBotAttentionDetail = null;
            Log(
                "streamerbot.action_confirmed",
                $"Action confirmed: {FriendlyActionName(config.StreamerBot)}");
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
                    config.Chord.Mode == ChordMode.Modifier
                        ? setup.FriendlySummary
                        : "Press both chosen controller inputs together.");
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
