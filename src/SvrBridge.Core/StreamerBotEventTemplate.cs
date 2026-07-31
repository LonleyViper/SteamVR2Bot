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
/// One token is not a path into <c>data</c>: <c>{event}</c> substitutes the
/// event's own "Source.Type" label, since that is metadata this type is
/// handed separately rather than something living inside the payload itself -
/// it is what the generic default template falls back to for an event with
/// no more specific per-event override.
/// </para>
/// </summary>
public static class StreamerBotEventTemplate
{
    private const string EventToken = "event";

    /// <summary>
    /// Substitutes every <c>{dotted.path}</c> token in <paramref name="template"/>
    /// against <paramref name="data"/>, plus the special <c>{event}</c> token
    /// against <paramref name="eventLabel"/>. An unterminated <c>{</c> (no
    /// closing brace) is copied through literally rather than treated as a
    /// token, so a template that is not perfectly balanced still renders
    /// something instead of losing its tail.
    /// </summary>
    public static string Resolve(string template, JsonElement data, string eventLabel)
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

            var path = template.Substring(open + 1, close - open - 1).Trim();
            result.Append(
                path.Equals(EventToken, StringComparison.OrdinalIgnoreCase)
                    ? eventLabel
                    : ResolvePath(data, path));
            index = close + 1;
        }

        return result.ToString();
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
