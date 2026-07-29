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
/// One badge on a chat message: a friendly label and, when known, an image
/// URL. Twitch chatters can carry several at once (broadcaster, subscriber,
/// bits, Prime, a channel's own custom loyalty badge, ...), so this is a
/// list on the payload rather than a single guessed "most important" one -
/// picking just one silently discards the rest, which is exactly what an
/// earlier version of this app did.
/// </summary>
public sealed record ChatBadge(string Label, string ImageUrl);

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
    /// The exact substrings of <see cref="Text"/> a platform identified as
    /// emotes, e.g. <c>["Kappa", "PogChamp"]</c>. The renderer looks each one
    /// up in its own emote image cache and embeds the real image when one is
    /// available, falling back to styled text otherwise. Empty when the
    /// source has no emote knowledge, which a hand-authored
    /// <c>General.Custom</c> payload is free to leave unset.
    /// </summary>
    public IReadOnlyList<string> EmoteNames { get; init; } = [];

    /// <summary>
    /// The image URL for <see cref="Badge"/>, when the source has one. The
    /// renderer embeds this image in place of the bracketed text label once
    /// it is cached, falling back to the label until then or if this is
    /// empty. A hand-authored <c>General.Custom</c> payload is free to leave
    /// it unset.
    /// <para>
    /// Kept alongside <see cref="Badges"/> rather than replaced by it, so a
    /// simple hand-authored payload can still set one badge without building
    /// a list. <see cref="TwitchChatMessageMapper"/> populates both: this
    /// field from the first entry, for anything still reading it directly.
    /// </para>
    /// </summary>
    public string BadgeImageUrl { get; init; } = "";

    /// <summary>
    /// Every badge on the message, not just one. See <see cref="ChatBadge"/>
    /// for why this exists as a list. Empty for a hand-authored payload that
    /// only set the singular <see cref="Badge"/>/<see cref="BadgeImageUrl"/> -
    /// the renderer falls back to treating those as a one-item list.
    /// </summary>
    public IReadOnlyList<ChatBadge> Badges { get; init; } = [];

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
            Command = ReadString(body, "command").Trim(),
            EmoteNames = ReadStringArray(body, "emotes"),
            BadgeImageUrl = ReadString(body, "badgeImageUrl"),
            Badges = ReadBadges(body, "badges")
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
    /// Reads a JSON array of strings, silently skipping any element that is
    /// not a non-empty string rather than rejecting the whole payload over
    /// one malformed emote entry.
    /// </summary>
    private static IReadOnlyList<string> ReadStringArray(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } text)
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a JSON array of <c>{"label": "...", "imageUrl": "..."}</c>
    /// objects for the hand-authored <c>badges</c> field, letting a
    /// hand-written test/relay action drive the same multi-badge rendering
    /// path <see cref="TwitchChatMessageMapper"/> feeds from live Twitch
    /// data. A malformed entry (missing label, wrong types) is skipped
    /// rather than rejecting the whole payload; <c>imageUrl</c> may be
    /// empty to test the text-label fallback deliberately.
    /// </summary>
    private static IReadOnlyList<ChatBadge> ReadBadges(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<ChatBadge>();
        foreach (var entry in value.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var label = ReadString(entry, "label");
            if (label.Length == 0)
            {
                continue;
            }

            result.Add(new ChatBadge(label, ReadString(entry, "imageUrl")));
        }

        return result;
    }

    /// <summary>
    /// Normalises to <c>#RRGGBB</c> and discards anything else. The renderer is
    /// downstream of user-authored JSON, so an unparsable colour has to become
    /// "no colour given" here rather than an exception mid-repaint later.
    /// </summary>
    private static string ReadColour(JsonElement body, string name) =>
        NormaliseColour(ReadString(body, name));

    /// <summary>
    /// Normalises a raw colour string to <c>#RRGGBB</c>, or "" when it is not
    /// one. Public so <see cref="TwitchChatMessageMapper"/> applies the exact
    /// same rule to a Twitch-supplied colour that this type applies to a
    /// hand-authored one, and the two render identically.
    /// </summary>
    public static string NormaliseColour(string value)
    {
        var trimmed = value.Trim();
        var digits = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
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
