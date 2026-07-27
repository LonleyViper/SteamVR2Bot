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
    public event Action<BridgeStatus>? StatusChanged;
    public event Action<string>? Activity;

    public async Task RunAsync(AppConfig config, CancellationToken cancellationToken)
    {
        SetStatus(
            BridgeState.Starting,
            "Starting…",
            "Connecting to SteamVR and preparing your controller shortcut.");

        try
        {
            var actionManifest = OpenVrInput.ResolveActionManifest(config.ActionManifestPath);
            using var openVr = new OpenVrInput(config.OpenVrDllPath, actionManifest, Log);
            var detector = new ChordDetector(config.Chord);
            var stopwatch = Stopwatch.StartNew();
            var previous = new InputSnapshot(false, false);

            await using var streamerBot = new StreamerBotClient(config.StreamerBot, Log);

            SetStatus(
                BridgeState.Ready,
                "Ready for your shortcut",
                "Hold Left Grip, then press Right Trigger.");

            while (!cancellationToken.IsCancellationRequested)
            {
                var snapshot = openVr.Poll();

                if (config.LogRawInputChanges && snapshot != previous)
                {
                    Log(
                        $"Controller: left grip {(snapshot.ButtonOne ? "held" : "released")}; " +
                        $"right trigger {(snapshot.ButtonTwo ? "pressed" : "released")}.");
                    previous = snapshot;
                }

                if (detector.Update(
                        snapshot.ButtonOne,
                        snapshot.ButtonTwo,
                        stopwatch.ElapsedMilliseconds))
                {
                    SetStatus(
                        BridgeState.Sending,
                        "Running your action…",
                        $"Running “{FriendlyActionName(config.StreamerBot)}” in Streamer.bot.");

                    await streamerBot.TriggerAsync(
                        "left_grip+right_trigger",
                        cancellationToken);

                    Log($"Action confirmed: {FriendlyActionName(config.StreamerBot)}");
                    SetStatus(
                        BridgeState.Ready,
                        "Ready for your shortcut",
                        "Hold Left Grip, then press Right Trigger.");
                }

                await Task.Delay(config.PollIntervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        catch (Exception exception)
        {
            SetStatus(
                BridgeState.Error,
                "Needs attention",
                FriendlyErrorDetail(exception));
            throw;
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                SetStatus(
                    BridgeState.Stopped,
                    "Stopped",
                    "The controller shortcut is not running.");
            }
        }
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
            await using var streamerBot = new StreamerBotClient(config.StreamerBot, Log);
            await streamerBot.TriggerAsync("settings_test", cancellationToken);
            Log($"Test confirmed: {FriendlyActionName(config.StreamerBot)}");
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

    private void Log(string message)
    {
        Activity?.Invoke(message);
    }

    private void SetStatus(BridgeState state, string friendlyName, string detail)
    {
        StatusChanged?.Invoke(new BridgeStatus(state, friendlyName, detail));
    }

    private static string FriendlyActionName(StreamerBotConfig config) =>
        string.IsNullOrWhiteSpace(config.ActionName)
            ? "Selected action"
            : config.ActionName.Trim();

    private static string FriendlyErrorDetail(Exception exception)
    {
        if (exception.Message.Contains("openvr_api.dll", StringComparison.OrdinalIgnoreCase))
        {
            return "Start SteamVR, then try again.";
        }

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
