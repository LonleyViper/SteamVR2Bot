using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>Which surface a Streamer.bot payload is asking for.</summary>
public enum StreamerBotEventTarget
{
    Chat,
    Notification,
    Control
}

/// <summary>
/// One display instruction broadcast by a Streamer.bot action through
/// <c>CPH.WebsocketBroadcastJson</c>.
/// <para>
/// Streamer.bot owns every platform integration and all filtering, so this app
/// never sees a Twitch, YouTube or Kick payload — only this shape, authored by
/// the user in a Streamer.bot action. That is the reason parsing is defensive
/// rather than strict: the body is hand-written C# on the other side and will
/// be wrong sometimes, and a typo in an action must cost the user one dropped
/// message rather than the whole feed.
/// </para>
/// <para>
/// <see cref="Version"/> exists so the Streamer.bot side, which will evolve
/// faster than the app, can be told apart later. A missing or unrecognised
/// version reads as version 1 because every payload written before the field
/// existed is a version 1 payload.
/// </para>
/// </summary>
public sealed record StreamerBotEventPayload
{
    public const int CurrentVersion = 1;

    /// <summary>How long a notification stays on screen when it does not say.</summary>
    public const int DefaultDurationMs = 5000;

    private const int MinimumDurationMs = 500;
    private const int MaximumDurationMs = 60_000;

    public int Version { get; init; } = CurrentVersion;
    public StreamerBotEventTarget Target { get; init; }
    public string User { get; init; } = "";
    public string Colour { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Text { get; init; } = "";
    public string Title { get; init; } = "";
    public int DurationMs { get; init; } = DefaultDurationMs;
    public string Accent { get; init; } = "";
    public string Command { get; init; } = "";

    /// <summary>
    /// Parses a payload body, reporting why it was rejected rather than
    /// throwing. Callers are receive loops that must survive bad input.
    /// </summary>
    public static bool TryParse(
        string json,
        [NotNullWhen(true)] out StreamerBotEventPayload? payload,
        out string rejection)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            rejection = "the payload was empty";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return TryParse(document.RootElement, out payload, out rejection);
        }
        catch (JsonException exception)
        {
            rejection = $"the payload was not valid JSON ({exception.Message})";
            return false;
        }
    }

    /// <inheritdoc cref="TryParse(string, out StreamerBotEventPayload, out string)"/>
    public static bool TryParse(
        JsonElement body,
        [NotNullWhen(true)] out StreamerBotEventPayload? payload,
        out string rejection)
    {
        payload = null;

        // WebsocketBroadcastJson carries an object, but an action that built
        // its payload with WebsocketBroadcastString, or that serialised twice,
        // sends the same object as a JSON string. Unwrapping it once is
        // cheaper than a support thread about a silently ignored feed.
        if (body.ValueKind == JsonValueKind.String)
        {
            return TryParse(body.GetString() ?? "", out payload, out rejection);
        }

        if (body.ValueKind != JsonValueKind.Object)
        {
            rejection = $"the payload was {DescribeKind(body.ValueKind)}, not an object";
            return false;
        }

        if (!TryReadTarget(body, out var target))
        {
            rejection = body.TryGetProperty("target", out var raw)
                ? $"the target “{raw.ToString()}” is not chat, notification or control"
                : "the payload has no target";
            return false;
        }

        payload = new StreamerBotEventPayload
        {
            Version = ReadVersion(body),
            Target = target,
            User = ReadString(body, "user"),
            Colour = ReadColour(body, "colour"),
            Badge = ReadString(body, "badge"),
            Text = ReadString(body, "text"),
            Title = ReadString(body, "title"),
            DurationMs = ReadDuration(body),
            Accent = ReadColour(body, "accent"),
            Command = ReadString(body, "command").Trim()
        };
        rejection = "";
        return true;
    }

    /// <summary>A one-line description for the activity log.</summary>
    public string Describe() =>
        Target switch
        {
            StreamerBotEventTarget.Chat =>
                $"chat — {(User.Length == 0 ? "(no name)" : User)}"
                + $"{(Badge.Length == 0 ? "" : $" [{Badge}]")}: {Text}",
            StreamerBotEventTarget.Notification =>
                $"notification — {(Title.Length == 0 ? "" : $"{Title}: ")}{Text}",
            _ => $"control — {(Command.Length == 0 ? "(no command)" : Command)}"
        };

    private static bool TryReadTarget(JsonElement body, out StreamerBotEventTarget target)
    {
        target = default;
        if (!body.TryGetProperty("target", out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch ((value.GetString() ?? "").Trim().ToLowerInvariant())
        {
            case "chat":
                target = StreamerBotEventTarget.Chat;
                return true;
            case "notification":
                target = StreamerBotEventTarget.Notification;
                return true;
            case "control":
                target = StreamerBotEventTarget.Control;
                return true;
            default:
                return false;
        }
    }

    private static int ReadVersion(JsonElement body) =>
        body.TryGetProperty("v", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var version)
        && version > 0
            ? version
            : CurrentVersion;

    /// <summary>
    /// Reads a string field, accepting the numbers and booleans that a hand
    /// written Streamer.bot payload produces when a variable is not a string.
    /// </summary>
    private static string ReadString(JsonElement body, string name) =>
        !body.TryGetProperty(name, out var value)
            ? ""
            : value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False =>
                    value.ToString(),
                _ => ""
            };

    /// <summary>
    /// Normalises to <c>#RRGGBB</c> and discards anything else. The renderer is
    /// downstream of user-authored JSON, so an unparsable colour has to become
    /// "no colour given" here rather than an exception mid-repaint later.
    /// </summary>
    private static string ReadColour(JsonElement body, string name)
    {
        var value = ReadString(body, name).Trim();
        var digits = value.StartsWith('#') ? value[1..] : value;
        if (digits.Length != 6)
        {
            return "";
        }

        foreach (var character in digits)
        {
            if (!Uri.IsHexDigit(character))
            {
                return "";
            }
        }

        return "#" + digits.ToUpperInvariant();
    }

    /// <summary>
    /// Clamped rather than trusted: a zero or a missing decimal point would
    /// otherwise mean a notification that never leaves the screen or one that
    /// is gone before it can be read.
    /// </summary>
    private static int ReadDuration(JsonElement body)
    {
        if (!body.TryGetProperty("duration", out var value))
        {
            return DefaultDurationMs;
        }

        var duration = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => DefaultDurationMs
        };

        return (int)Math.Clamp(duration, MinimumDurationMs, MaximumDurationMs);
    }

    private static string DescribeKind(JsonValueKind kind) =>
        kind switch
        {
            JsonValueKind.Undefined => "missing",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "an array",
            _ => $"a {kind.ToString().ToLowerInvariant()}"
        };
}
