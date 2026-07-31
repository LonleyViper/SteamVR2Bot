using System.Text;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Resolves a notification text template against one Streamer.bot event's
/// <c>data</c> object, per §B2 of the Phase 7 plan - the replacement for a
/// hand-written mapper per event type. A template such as
/// <c>"{targetUser.name} just followed!"</c> covers <c>Twitch.Follow</c>,
/// <c>Twitch.Raid</c> or an event that does not exist yet equally well,
/// because this reads the path out of whatever JSON arrived rather than
/// knowing the event's shape in advance.
/// <para>
/// A dotted path indexes into nested objects (<c>targetUser.name</c> reads
/// <c>data.targetUser.name</c>). A path that is missing at any segment, or
/// whose leaf is JSON <c>null</c>, or whose <em>intermediate</em> segment is
/// <c>null</c> (Twitch's own schema declares <c>targetUser</c> itself as
/// nullable, not just its fields) all resolve to an empty string - never an
/// exception. A malformed Streamer.bot payload has to cost this app one
/// blank substitution, not a dropped feed.
/// </para>
/// <para>
/// Three tokens are not paths into <c>data</c>, because they are metadata
/// this type is handed separately rather than anything living inside the
/// payload: <c>{event}</c> substitutes the "Source.Type" label,
/// <c>{eventName}</c> the event's own name in readable form ("Gift Sub"), and
/// <c>{eventSource}</c> the source alone ("Twitch").
/// </para>
/// <para>
/// A token may list <b>alternatives separated by <c>|</c></b>, and the first
/// one that resolves to something non-empty wins:
/// <c>{user.name|targetUser.name|"Someone"}</c>. A segment in double quotes
/// is a literal rather than a path, which is what gives a template a last
/// resort instead of a blank gap. This exists because payload shapes differ
/// per event - a follow names its actor in one field, a raid in another - and
/// alternatives let one template cover all of them without this app carrying
/// a table of which event uses which field.
/// </para>
/// </summary>
public static class StreamerBotEventTemplate
{
    private const string EventToken = "event";
    private const string EventNameToken = "eventName";
    private const string EventSourceToken = "eventSource";

    /// <summary>
    /// Substitutes every <c>{…}</c> token in <paramref name="template"/>
    /// against <paramref name="data"/>. An unterminated <c>{</c> (no closing
    /// brace) is copied through literally rather than treated as a token, so
    /// a template that is not perfectly balanced still renders something
    /// instead of losing its tail.
    /// </summary>
    public static string Resolve(string template, JsonElement data, string source, string type)
    {
        if (string.IsNullOrEmpty(template))
        {
            return "";
        }

        var result = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var open = template.IndexOf('{', index);
            if (open < 0)
            {
                result.Append(template, index, template.Length - index);
                break;
            }

            result.Append(template, index, open - index);

            var close = template.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(template, open, template.Length - open);
                break;
            }

            result.Append(
                ResolveToken(
                    template.Substring(open + 1, close - open - 1),
                    data,
                    source,
                    type));
            index = close + 1;
        }

        return result.ToString();
    }

    /// <summary>Kept so a caller with only the "Source.Type" label - the self-tests, mostly - does not have to split it itself.</summary>
    public static string Resolve(string template, JsonElement data, string eventLabel)
    {
        var separator = (eventLabel ?? "").IndexOf('.');
        return separator > 0
            ? Resolve(template, data, eventLabel![..separator], eventLabel[(separator + 1)..])
            : Resolve(template, data, "", eventLabel ?? "");
    }

    /// <summary>One token's alternatives, left to right, stopping at the first that yields something.</summary>
    private static string ResolveToken(string token, JsonElement data, string source, string type)
    {
        foreach (var alternative in token.Split('|'))
        {
            var trimmed = alternative.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // A quoted segment is a literal, so a chain of paths can end in
            // something to say when none of them were present.
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            {
                return trimmed[1..^1];
            }

            var resolved = trimmed switch
            {
                _ when trimmed.Equals(EventToken, StringComparison.OrdinalIgnoreCase) =>
                    string.IsNullOrEmpty(source) ? type : $"{source}.{type}",
                _ when trimmed.Equals(EventNameToken, StringComparison.OrdinalIgnoreCase) =>
                    StreamerBotEventCatalog.SpaceCamelCase(type),
                _ when trimmed.Equals(EventSourceToken, StringComparison.OrdinalIgnoreCase) => source,
                _ => ResolvePath(data, trimmed)
            };

            if (resolved.Length > 0)
            {
                return resolved;
            }
        }

        return "";
    }

    /// <summary>Walks one dotted path, returning "" the moment any segment is missing, non-object, or null.</summary>
    private static string ResolvePath(JsonElement data, string path)
    {
        if (path.Length == 0)
        {
            return "";
        }

        var current = data;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return "";
            }

            current = next;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString() ?? "",
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            // Null (an absent targetUser, exactly as Twitch.Follow's own
            // schema allows), object, array and undefined all mean "nothing
            // sensible to print here" - none of them are worth an exception.
            _ => ""
        };
    }
}
