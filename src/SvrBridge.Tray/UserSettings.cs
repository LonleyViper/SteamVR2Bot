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
            }
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
            StartBridgeWhenAppOpens = settings.StartBridgeWhenAppOpens
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
                StartBridgeWhenAppOpens = saved.StartBridgeWhenAppOpens
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
    }
}
