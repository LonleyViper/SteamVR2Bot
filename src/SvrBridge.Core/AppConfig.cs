using System.Text.Json;
using System.Text.Json.Serialization;

namespace SvrBridge.Core;

public sealed class AppConfig
{
    public string? OpenVrDllPath { get; init; }
    public string? ActionManifestPath { get; init; }
    public int PollIntervalMs { get; init; } = 10;
    public bool LogRawInputChanges { get; init; } = true;
    public StreamerBotConfig StreamerBot { get; init; } = new();
    public ChordConfig Chord { get; init; } = new();
    public IReadOnlyList<ShortcutConfig> Shortcuts { get; init; } = [];

    /// <summary>
    /// The saved default anchor for the chat window. Baked into the OpenVR
    /// worker at spawn (see <c>OpenVrWorkerSession.CreateStartInfo</c>)
    /// rather than pushed at runtime, so a desktop anchor change takes effect
    /// on the next worker restart - the same as an address or password
    /// change already does. A Streamer.bot <c>anchor</c> control command can
    /// still override it in memory for the life of that worker; see
    /// <see cref="SurfaceOverrideState"/>.
    /// </summary>
    public OverlayAnchor ChatAnchor { get; init; } = new(OverlayAnchorMode.Controller, OverlayAnchorHand.Left);

    /// <summary>The saved default anchor for notifications. See <see cref="ChatAnchor"/>.</summary>
    public OverlayAnchor NotificationAnchor { get; init; } = OverlayAnchor.Head;

    /// <summary>
    /// Where the chat window sits within whichever anchor it follows, as
    /// last placed by hand in the headset. Baked in at spawn like
    /// <see cref="ChatAnchor"/>; unlike it, this is the one setting the wearer
    /// normally changes from inside VR rather than on the desktop, so the
    /// worker also reports changes back the other way - see
    /// <c>OpenVrWorker</c>'s <c>"vrSettingsChanged"</c> message.
    /// </summary>
    public OverlayPlacement ChatPlacement { get; init; } = OverlayPlacement.Default;

    /// <summary>
    /// Whether the chat window starts on. Baked in at spawn like the anchor
    /// fields above, so the OpenVR worker knows the initial state without
    /// waiting for a "chat" command - needed so the VR settings page's on/off
    /// toggle can create or dispose the overlay in place, immediately,
    /// without a worker restart.
    /// </summary>
    public bool ChatEnabled { get; init; }

    /// <summary>Whether notifications start on. See <see cref="ChatEnabled"/>.</summary>
    public bool NotificationsEnabled { get; init; }

    /// <summary>The chat window's saved default gazed-at alpha ceiling. See <see cref="ChatAnchor"/> for why this is baked in at spawn.</summary>
    public double ChatOpacity { get; init; } = 0.95;

    /// <summary>Multiplies the chat window's saved default widths. See <see cref="ChatOpacity"/>.</summary>
    public double ChatSizeScale { get; init; } = 1.0;

    /// <summary>The chat window's saved default gaze sensitivity. See <see cref="ChatOpacity"/>.</summary>
    public GazeSensitivity GazeSensitivity { get; init; } = GazeSensitivity.Normal;

    /// <summary>
    /// Whether the chat window grows and brightens on gaze. Off by default -
    /// see <see cref="UserSettings.ChatGazeScaleEnabled"/> in the tray for
    /// why, and <see cref="ChatOpacity"/> for why this is baked in at spawn.
    /// </summary>
    public bool ChatGazeScaleEnabled { get; init; }

    /// <summary>A wearer-calibrated centre for the chat gaze cone; <see cref="GazeReference.None"/> keeps the original direct-to-panel behaviour.</summary>
    public GazeReference ChatGazeReference { get; init; } = GazeReference.None;

    /// <summary>The notification panel's saved default peak alpha. See <see cref="ChatOpacity"/>.</summary>
    public double NotificationOpacity { get; init; } = 1.0;

    /// <summary>Multiplies the notification panel's saved default width. See <see cref="ChatOpacity"/>.</summary>
    public double NotificationSizeScale { get; init; } = 1.0;

    /// <summary>The notification panel's saved default placement - see <see cref="ChatPlacement"/>, added for §B1 of the Phase 7 plan.</summary>
    public OverlayPlacement NotificationPlacement { get; init; } = OverlayPlacement.Default;

    /// <summary>Everything §B3/§B4/§B5 of the Phase 7 plan added - baked in at spawn like every other appearance field above.</summary>
    public NotificationAppearanceSettings NotificationAppearance { get; init; } = NotificationAppearanceSettings.Default;

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration not found: {path}{Environment.NewLine}" +
                "Copy appsettings.example.json to appsettings.json and edit it.");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)
                     ?? throw new InvalidDataException("The configuration file is empty.");

        if (config.PollIntervalMs is < 1 or > 1000)
        {
            throw new InvalidDataException("pollIntervalMs must be between 1 and 1000.");
        }

        config.StreamerBot.Validate();
        config.Chord.Validate();
        foreach (var shortcut in config.GetShortcuts())
        {
            shortcut.Validate();
        }
        return config;
    }

    public IReadOnlyList<ShortcutConfig> GetShortcuts() =>
        Shortcuts.Count > 0
            ? Shortcuts
            : string.IsNullOrWhiteSpace(StreamerBot.ActionName)
              && string.IsNullOrWhiteSpace(StreamerBot.ActionId)
                ? []
                :
            [
                ShortcutConfig.FromLegacy(StreamerBot, Chord)
            ];
}

public sealed class StreamerBotConfig
{
    public string WebSocketUrl { get; init; } = "ws://127.0.0.1:8080/";
    public string Password { get; init; } = "";
    public string ActionName { get; init; } = "SVR POC Test";
    public string? ActionId { get; init; }
    public bool DryRun { get; init; } = true;

    public void Validate()
    {
        if (!Uri.TryCreate(WebSocketUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeWs && uri.Scheme != Uri.UriSchemeWss))
        {
            throw new InvalidDataException("streamerBot.webSocketUrl must be a ws:// or wss:// URL.");
        }

    }
}

public enum ChordMode
{
    Simultaneous,
    Modifier,
    LongPress,
    DoublePress,
    SinglePress
}

public sealed class ChordConfig
{
    public ChordMode Mode { get; init; } = ChordMode.Modifier;
    public int WindowMs { get; init; } = 2000;
    public int CooldownMs { get; init; } = 250;
    public int HoldMs { get; init; } = 1000;

    public void Validate()
    {
        if (WindowMs is < 1 or > 30_000)
        {
            throw new InvalidDataException("chord.windowMs must be between 1 and 30000.");
        }

        if (CooldownMs is < 0 or > 30_000)
        {
            throw new InvalidDataException("chord.cooldownMs must be between 0 and 30000.");
        }

        if (HoldMs is < 250 or > 30_000)
        {
            throw new InvalidDataException("chord.holdMs must be between 250 and 30000.");
        }
    }
}

public sealed record ControllerInputBinding
{
    public string Id { get; init; } = "";
    public string FriendlyName { get; init; } = "";

    public bool IsPressed(InputSnapshot snapshot) =>
        Id.ToLowerInvariant() switch
        {
            "steamvr:safety" => snapshot.ButtonOne,
            "steamvr:action" => snapshot.ButtonTwo,
            _ when TryParsePhysical(Id, out var hand, out var button) =>
                ((hand == ControllerHand.Left
                    ? snapshot.LeftButtons
                    : snapshot.RightButtons) & (1UL << (int)button)) != 0,
            _ => false
        };

    public static ControllerInputBinding SteamVrSafety { get; } =
        new() { Id = "steamvr:safety", FriendlyName = "Left Grip (SteamVR)" };

    public static ControllerInputBinding SteamVrAction { get; } =
        new() { Id = "steamvr:action", FriendlyName = "Right Trigger (SteamVR)" };

    public static ControllerInputBinding Physical(
        ControllerHand hand,
        uint button,
        string friendlyName) =>
        new()
        {
            Id = $"{hand.ToString().ToLowerInvariant()}:{button}",
            FriendlyName = friendlyName
        };

    public static bool TryParsePhysical(
        string id,
        out ControllerHand hand,
        out uint button)
    {
        hand = ControllerHand.Left;
        button = 0;
        var pieces = id.Split(':', 2);
        return pieces.Length == 2
               && Enum.TryParse(pieces[0], true, out hand)
               && uint.TryParse(pieces[1], out button)
               && button < 64;
    }
}

public enum ControllerHand
{
    Left,
    Right
}

public sealed record ShortcutConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New shortcut";
    public bool Enabled { get; init; } = true;
    public ControllerInputBinding SafetyInput { get; init; } =
        ControllerInputBinding.SteamVrSafety;
    public ControllerInputBinding ActionInput { get; init; } =
        ControllerInputBinding.SteamVrAction;
    public ChordConfig Gesture { get; init; } = new();
    public string ActionName { get; init; } = "";
    public string? ActionId { get; init; }

    public string FriendlyGesture =>
        Gesture.Mode switch
        {
            ChordMode.SinglePress =>
                $"Press {SafetyInput.FriendlyName}",
            ChordMode.DoublePress =>
                $"Double press {SafetyInput.FriendlyName}",
            ChordMode.LongPress =>
                $"Hold {SafetyInput.FriendlyName} for {FriendlyDuration(Gesture.HoldMs)}",
            ChordMode.Modifier =>
                $"Hold {SafetyInput.FriendlyName}, then press {ActionInput.FriendlyName}",
            _ =>
                $"Press {SafetyInput.FriendlyName} and {ActionInput.FriendlyName} together"
        };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidDataException("Give every shortcut a friendly name.");
        }

        if (string.IsNullOrWhiteSpace(ActionName) && string.IsNullOrWhiteSpace(ActionId))
        {
            throw new InvalidDataException($"Choose a Streamer.bot action for “{Name}”.");
        }

        if (string.IsNullOrWhiteSpace(SafetyInput.Id))
        {
            throw new InvalidDataException(
                $"Choose a controller input for “{Name}”.");
        }

        if (Gesture.Mode
                is not (ChordMode.SinglePress
                or ChordMode.LongPress
                or ChordMode.DoublePress)
            && (string.IsNullOrWhiteSpace(ActionInput.Id)
                || SafetyInput.Id.Equals(ActionInput.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                $"Choose two different controller inputs for “{Name}”.");
        }

        Gesture.Validate();
    }

    private static string FriendlyDuration(int milliseconds) =>
        milliseconds % 1000 == 0
            ? $"{milliseconds / 1000} second{(milliseconds == 1000 ? "" : "s")}"
            : $"{milliseconds / 1000d:0.#} seconds";

    public static ShortcutConfig FromLegacy(
        StreamerBotConfig streamerBot,
        ChordConfig chord) =>
        new()
        {
            Id = "legacy-steamvr-shortcut",
            Name = string.IsNullOrWhiteSpace(streamerBot.ActionName)
                ? "My VR shortcut"
                : streamerBot.ActionName,
            SafetyInput = ControllerInputBinding.SteamVrSafety,
            ActionInput = ControllerInputBinding.SteamVrAction,
            Gesture = chord,
            ActionName = streamerBot.ActionName,
            ActionId = streamerBot.ActionId
        };
}
