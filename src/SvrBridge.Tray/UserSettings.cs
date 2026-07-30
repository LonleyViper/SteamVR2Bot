using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed record UserSettings
{
    public string StreamerBotAddress { get; init; } = "ws://127.0.0.1:8080/1";
    public string ActionName { get; init; } = "";
    public string ActionId { get; init; } = "";
    public string Password { get; init; } = "";
    public ChordMode GestureMode { get; init; } = ChordMode.Modifier;
    public IReadOnlyList<ShortcutConfig> Shortcuts { get; init; } = [];
    public bool StartBridgeWhenAppOpens { get; init; }

    /// <summary>
    /// Whether to hold a second, permanently connected socket that listens for
    /// Streamer.bot broadcasts. Off by default: it is useless until the user
    /// has written an action that broadcasts, and it is the only part of the
    /// app that keeps a connection open when no shortcut is being delivered.
    /// </summary>
    public bool EventStreamEnabled { get; init; }

    /// <summary>
    /// Whether a Streamer.bot payload with <c>target: "notification"</c> draws
    /// a head-anchored panel in the headset. Off by default, and meaningless
    /// while <see cref="EventStreamEnabled"/> is off - there is no feed to draw
    /// a notification from - so the two are independent switches rather than
    /// one implying the other, the same way the feed itself does not imply a
    /// shortcut is configured.
    /// </summary>
    public bool NotificationsEnabled { get; init; }

    /// <summary>
    /// Whether a Streamer.bot payload with <c>target: "chat"</c> appends to
    /// the wrist chat window. Off by default and independent of
    /// <see cref="NotificationsEnabled"/>, for the same reasons that setting
    /// is independent of <see cref="EventStreamEnabled"/>.
    /// </summary>
    public bool ChatEnabled { get; init; }

    /// <summary>
    /// Where the chat window is pinned by default. Defaults to the left
    /// controller - the exact placement Phase 1/3 proved in the headset - so
    /// a settings file saved before this field existed loads with the
    /// behaviour it already had. See §B2 of the Phase 4 plan.
    /// </summary>
    public OverlayAnchorMode ChatAnchorMode { get; init; } = OverlayAnchorMode.Controller;

    /// <summary>Which hand <see cref="ChatAnchorMode"/> follows in Controller mode.</summary>
    public OverlayAnchorHand ChatAnchorHand { get; init; } = OverlayAnchorHand.Left;

    /// <summary>
    /// Where notifications are pinned by default. Defaults to the headset -
    /// the exact placement Phase 2 proved in the headset - for the same
    /// migration reason as <see cref="ChatAnchorMode"/>.
    /// </summary>
    public OverlayAnchorMode NotificationAnchorMode { get; init; } = OverlayAnchorMode.Head;

    /// <summary>Which hand <see cref="NotificationAnchorMode"/> follows in Controller mode.</summary>
    public OverlayAnchorHand NotificationAnchorHand { get; init; } = OverlayAnchorHand.Left;

    /// <summary>
    /// Where the chat window sits within its anchor, as last dragged by hand
    /// in the headset - one offset per anchor mode. Defaults to
    /// <see cref="OverlayPlacement.Default"/>, which is bit-for-bit the pair
    /// of transforms Phases 1/2/3 proved on hardware, so a settings file
    /// written before this field existed - and an install that has never
    /// dragged anything - looks exactly as it always did.
    /// </summary>
    public OverlayPlacement ChatPlacement { get; init; } = OverlayPlacement.Default;

    public OverlayAnchor ChatAnchor => new(ChatAnchorMode, ChatAnchorHand);

    public OverlayAnchor NotificationAnchor => new(NotificationAnchorMode, NotificationAnchorHand);

    /// <summary>
    /// The chat window's gazed-at alpha ceiling, 0.2-1.0. Defaults to 0.95 -
    /// the value <see cref="ChatOverlay"/> hardcoded before Phase 4b, so an
    /// old settings file keeps the exact behaviour it already had. The faint,
    /// not-gazed-at alpha scales from this proportionally rather than being a
    /// separate setting - see <see cref="ChatOverlay"/>'s own remarks.
    /// </summary>
    public double ChatOpacity { get; init; } = 0.95;

    /// <summary>
    /// Multiplies the chat window's gazed-at and faint widths, 0.5-2.0.
    /// Defaults to 1.0 - today's hardcoded widths, unscaled.
    /// </summary>
    public double ChatSizeScale { get; init; } = 1.0;

    /// <summary>How readily the chat window's gaze detection triggers. See <see cref="Core.GazeSensitivity"/>.</summary>
    public GazeSensitivity GazeSensitivity { get; init; } = GazeSensitivity.Normal;

    /// <summary>
    /// Whether the chat window grows and brightens when looked at. Off leaves
    /// it at its configured size and opacity permanently; gaze is still
    /// measured either way, since it is what gates the laser.
    /// <para>
    /// <b>Defaults to off, which deliberately breaks this file's usual
    /// migration rule.</b> Every other setting here defaults to whatever the
    /// app did before it existed, so an upgrade changes nothing. This one
    /// does not: the animation was tried in the headset across a full phase
    /// and found distracting to read against, and shipping a default that has
    /// to be turned off before the window is comfortable is the wrong way
    /// round. An install that has actually saved a preference keeps it - only
    /// a file that predates the setting takes the new default.
    /// </para>
    /// </summary>
    public bool ChatGazeScaleEnabled { get; init; }

    /// <summary>
    /// The notification panel's peak alpha while holding, 0.2-1.0. Defaults
    /// to 1.0 - the value <see cref="NotificationOverlay"/> hardcoded before
    /// Phase 4b.
    /// </summary>
    public double NotificationOpacity { get; init; } = 1.0;

    /// <summary>Multiplies the notification panel's width, 0.5-2.0. Defaults to 1.0 - today's hardcoded width, unscaled.</summary>
    public double NotificationSizeScale { get; init; } = 1.0;

    public IReadOnlyList<ShortcutConfig> GetShortcuts()
    {
        if (Shortcuts.Count > 0)
        {
            return Shortcuts;
        }

        if (string.IsNullOrWhiteSpace(ActionName)
            && string.IsNullOrWhiteSpace(ActionId))
        {
            return [];
        }

        return
        [
            ShortcutConfig.FromLegacy(
                new StreamerBotConfig
                {
                    ActionName = ActionName,
                    ActionId = string.IsNullOrWhiteSpace(ActionId) ? null : ActionId
                },
                new ChordConfig
                {
                    Mode = GestureMode,
                    WindowMs = GestureMode switch
                    {
                        ChordMode.DoublePress => 500,
                        ChordMode.Simultaneous => 300,
                        _ => 2000
                    },
                    CooldownMs = 250
                })
        ];
    }

    public AppConfig ToAppConfig() =>
        new()
        {
            ActionManifestPath = Path.Combine(AppContext.BaseDirectory, "actions.json"),
            PollIntervalMs = 10,
            LogRawInputChanges = true,
            StreamerBot = new StreamerBotConfig
            {
                WebSocketUrl = StreamerBotAddress.Trim(),
                Password = Password,
                ActionName = ActionName.Trim(),
                ActionId = string.IsNullOrWhiteSpace(ActionId) ? null : ActionId.Trim(),
                DryRun = false
            },
            Shortcuts = GetShortcuts(),
            Chord = new ChordConfig
            {
                Mode = GestureMode,
                WindowMs = GestureMode switch
                {
                    ChordMode.DoublePress => 500,
                    ChordMode.Simultaneous => 300,
                    _ => 2000
                },
                CooldownMs = 250
            },
            ChatAnchor = ChatAnchor,
            ChatPlacement = ChatPlacement,
            NotificationAnchor = NotificationAnchor,
            ChatEnabled = ChatEnabled,
            NotificationsEnabled = NotificationsEnabled,
            ChatOpacity = ChatOpacity,
            ChatSizeScale = ChatSizeScale,
            GazeSensitivity = GazeSensitivity,
            ChatGazeScaleEnabled = ChatGazeScaleEnabled,
            NotificationOpacity = NotificationOpacity,
            NotificationSizeScale = NotificationSizeScale
        };
}

internal sealed class UserSettingsStore
{
    // DPAPI entropy, not a display name. It is part of the key for every
    // password already protected on disk, so it survived the rename to
    // SteamVR2Bot deliberately; changing it makes saved passwords unreadable.
    private static readonly byte[] AdditionalEntropy =
        Encoding.UTF8.GetBytes("SVR Bridge settings v1");

    private readonly string _settingsDirectory;
    private readonly string _legacySettingsPath;

    private string SettingsPath => Path.Combine(_settingsDirectory, "settings.json");

    public UserSettingsStore(
        string? settingsDirectory = null,
        string? legacySettingsPath = null)
    {
        _settingsDirectory = settingsDirectory ?? AppPaths.DataDirectory;
        _legacySettingsPath = legacySettingsPath
                              ?? Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    public string FriendlyLocation => SettingsPath;

    public UserSettings Load()
    {
        if (File.Exists(SettingsPath))
        {
            return LoadSavedSettings();
        }

        var imported = TryImportLegacySettings();
        if (imported is not null)
        {
            Save(imported);
            return imported;
        }

        return new UserSettings();
    }

    public void Save(UserSettings settings)
    {
        ValidateForSave(settings);
        Directory.CreateDirectory(_settingsDirectory);

        var saved = new SavedSettings
        {
            StreamerBotAddress = settings.StreamerBotAddress.Trim(),
            ActionName = settings.ActionName.Trim(),
            ActionId = settings.ActionId.Trim(),
            ProtectedPassword = Protect(settings.Password),
            GestureMode = settings.GestureMode,
            Shortcuts = settings.GetShortcuts(),
            StartBridgeWhenAppOpens = settings.StartBridgeWhenAppOpens,
            EventStreamEnabled = settings.EventStreamEnabled,
            NotificationsEnabled = settings.NotificationsEnabled,
            ChatEnabled = settings.ChatEnabled,
            ChatAnchorMode = settings.ChatAnchorMode,
            ChatAnchorHand = settings.ChatAnchorHand,
            ChatPlacement = settings.ChatPlacement,
            NotificationAnchorMode = settings.NotificationAnchorMode,
            NotificationAnchorHand = settings.NotificationAnchorHand,
            ChatOpacity = settings.ChatOpacity,
            ChatSizeScale = settings.ChatSizeScale,
            GazeSensitivity = settings.GazeSensitivity,
            ChatGazeScaleEnabled = settings.ChatGazeScaleEnabled,
            NotificationOpacity = settings.NotificationOpacity,
            NotificationSizeScale = settings.NotificationSizeScale
        };

        var json = JsonSerializer.Serialize(
            saved,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static void Validate(UserSettings settings)
    {
        ValidateForSave(settings);

        if (settings.GetShortcuts().Count == 0)
        {
            throw new InvalidDataException("Add at least one controller shortcut.");
        }
    }

    public static void ValidateForSave(UserSettings settings)
    {
        ValidateConnection(settings);
        foreach (var shortcut in settings.GetShortcuts())
        {
            shortcut.Validate();
        }
    }

    public static void ValidateConnection(UserSettings settings)
    {
        if (!Uri.TryCreate(settings.StreamerBotAddress.Trim(), UriKind.Absolute, out var address)
            || (address.Scheme != Uri.UriSchemeWs && address.Scheme != Uri.UriSchemeWss))
        {
            throw new InvalidDataException(
                "Enter a Streamer.bot address beginning with ws:// or wss://.");
        }
    }

    private UserSettings LoadSavedSettings()
    {
        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(
                            File.ReadAllText(SettingsPath),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? throw new InvalidDataException("The settings file is empty.");

            return new UserSettings
            {
                StreamerBotAddress = saved.StreamerBotAddress,
                ActionName = saved.ActionName,
                ActionId = saved.ActionId,
                Password = Unprotect(saved.ProtectedPassword),
                GestureMode = saved.GestureMode,
                Shortcuts = saved.Shortcuts ?? [],
                StartBridgeWhenAppOpens = saved.StartBridgeWhenAppOpens,
                // Absent from every settings file written before this feature
                // existed, which is exactly the "off" the default describes.
                EventStreamEnabled = saved.EventStreamEnabled,
                NotificationsEnabled = saved.NotificationsEnabled,
                ChatEnabled = saved.ChatEnabled,
                // Absent from every settings file written before Phase 4,
                // which is exactly the hardcoded placement those phases
                // already had - see the defaults on SavedSettings below.
                ChatAnchorMode = saved.ChatAnchorMode,
                ChatAnchorHand = saved.ChatAnchorHand,
                // Absent from every settings file written before Phase 5,
                // which is exactly the hardware-proven placement the chat
                // window always had - see OverlayPlacement.Default.
                //
                // Sanitised rather than trusted, because "absent" is not the
                // only way this arrives wrong. This field's shape changed once
                // already, from three numbers per anchor mode to a full
                // transform, and a file written before that reads back as an
                // all-zero matrix - which is not absent, is not a
                // deserialisation error, and collapses the chat window to
                // nothing. See OverlayPlacement.Sanitised.
                ChatPlacement = saved.ChatPlacement.Sanitised(),
                NotificationAnchorMode = saved.NotificationAnchorMode,
                NotificationAnchorHand = saved.NotificationAnchorHand,
                // Absent from every settings file written before Phase 4b,
                // which is exactly the hardcoded opacity/size/sensitivity
                // those phases already had - see the defaults on
                // SavedSettings below.
                ChatOpacity = saved.ChatOpacity,
                ChatSizeScale = saved.ChatSizeScale,
                GazeSensitivity = saved.GazeSensitivity,
                ChatGazeScaleEnabled = saved.ChatGazeScaleEnabled,
                NotificationOpacity = saved.NotificationOpacity,
                NotificationSizeScale = saved.NotificationSizeScale
            };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "SteamVR2Bot could not read its saved settings. Open Settings and save them again.",
                exception);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "Windows could not unlock the saved Streamer.bot password. Enter it again.",
                exception);
        }
    }

    private UserSettings? TryImportLegacySettings()
    {
        if (!File.Exists(_legacySettingsPath))
        {
            return null;
        }

        try
        {
            var legacy = AppConfig.Load(_legacySettingsPath);
            return new UserSettings
            {
                StreamerBotAddress = legacy.StreamerBot.WebSocketUrl,
                ActionName = legacy.StreamerBot.ActionName,
                ActionId = legacy.StreamerBot.ActionId ?? "",
                Password = legacy.StreamerBot.Password,
                GestureMode = legacy.Chord.Mode,
                StartBridgeWhenAppOpens = false
            };
        }
        catch
        {
            return null;
        }
    }

    private static string Protect(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return "";
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            AdditionalEntropy,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string protectedPassword)
    {
        if (string.IsNullOrEmpty(protectedPassword))
        {
            return "";
        }

        var passwordBytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedPassword),
            AdditionalEntropy,
            DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(passwordBytes);
    }

    private sealed record SavedSettings
    {
        public string StreamerBotAddress { get; init; } = "ws://127.0.0.1:8080/1";
        public string ActionName { get; init; } = "";
        public string ActionId { get; init; } = "";
        public string ProtectedPassword { get; init; } = "";
        public ChordMode GestureMode { get; init; } = ChordMode.Modifier;
        public IReadOnlyList<ShortcutConfig>? Shortcuts { get; init; }
        public bool StartBridgeWhenAppOpens { get; init; }
        public bool EventStreamEnabled { get; init; }
        public bool NotificationsEnabled { get; init; }
        public bool ChatEnabled { get; init; }
        public OverlayAnchorMode ChatAnchorMode { get; init; } = OverlayAnchorMode.Controller;
        public OverlayAnchorHand ChatAnchorHand { get; init; } = OverlayAnchorHand.Left;
        public OverlayPlacement ChatPlacement { get; init; } = OverlayPlacement.Default;
        public OverlayAnchorMode NotificationAnchorMode { get; init; } = OverlayAnchorMode.Head;
        public OverlayAnchorHand NotificationAnchorHand { get; init; } = OverlayAnchorHand.Left;
        public double ChatOpacity { get; init; } = 0.95;
        public double ChatSizeScale { get; init; } = 1.0;
        public GazeSensitivity GazeSensitivity { get; init; } = GazeSensitivity.Normal;
        public bool ChatGazeScaleEnabled { get; init; }
        public double NotificationOpacity { get; init; } = 1.0;
        public double NotificationSizeScale { get; init; } = 1.0;
    }
}
