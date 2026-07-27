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

        config.Chord.Validate();
        config.StreamerBot.Validate();
        return config;
    }
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

        if (string.IsNullOrWhiteSpace(ActionName) && string.IsNullOrWhiteSpace(ActionId))
        {
            throw new InvalidDataException("Set streamerBot.actionName, streamerBot.actionId, or both.");
        }
    }
}

public enum ChordMode
{
    Simultaneous,
    Modifier
}

public sealed class ChordConfig
{
    public ChordMode Mode { get; init; } = ChordMode.Modifier;
    public int WindowMs { get; init; } = 2000;
    public int CooldownMs { get; init; } = 250;

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
    }
}
