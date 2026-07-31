using System.Text;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>One event a connected Streamer.bot instance can emit, as <c>GetEvents</c> reported it.</summary>
public sealed record StreamerBotEventDescriptor(string Source, string Type)
{
    /// <summary>The "Source.Type" form every setting and template lookup in this app keys events by.</summary>
    public string Key => $"{Source}.{Type}";

    /// <summary>
    /// The event's own name with word breaks put back in - "GiftSub" reads
    /// "Gift Sub", "HypeTrainLevelUp" reads "Hype Train Level Up". See
    /// <see cref="StreamerBotEventCatalog.SpaceCamelCase"/> for why this is a
    /// pure string transform rather than a lookup table.
    /// </summary>
    public string DisplayName => StreamerBotEventCatalog.SpaceCamelCase(Type);
}

/// <summary>
/// Parses a <c>GetEvents</c> response into the flat list of events the
/// Notifications tab's picker is built from, per §B2 of the Phase 7 plan.
/// <para>
/// <b>Observed shape, Streamer.bot 1.0.4 (captured live 2026-07-31.)</b>
/// <c>GetEvents</c> still carries no documentation on Streamer.bot's own site
/// - both the request/response reference and the events guide are marked
/// "Documentation Needed" - so this was previously inferred from the
/// <c>Subscribe</c> request's own documented <c>events</c> argument. One real
/// response has now been captured and the inference was correct:
/// <code>
/// {"id":"probe-1",
///  "events":{"General":["Custom"],
///            "Twitch":["Follow","Cheer","Sub","ReSub","GiftSub", ...],
///            "Kick":["Follow","Subscription","GiftSubscription", ...],
///            ...},
///  "status":"ok"}
/// </code>
/// An object keyed by source name, each value a flat array of <b>plain JSON
/// strings</b>. That instance reported 44 sources and 467 events - 137 under
/// Twitch alone, 90 under Elgato, 29 under YouTube, 21 under Kick - which is
/// why the picker searches to add rather than asking anyone to browse.
/// </para>
/// <para>
/// Three things the capture settles, each of which had been guessed at:
/// <list type="number">
/// <item>Array entries are strings and nothing else. The object-entry
/// handling in <see cref="ReadEventType"/> is now purely forward-defensive -
/// it matched no entry in the live response - and is kept only so a future
/// version that enriches the shape does not silently lose events.</item>
/// <item><b>No icon, image, or display-name field exists anywhere in the
/// response</b>, at either level. The platform logos Streamer.bot shows in
/// its own chat and event viewer are its embedded UI assets, not API data, so
/// this app's picker has to supply its own - see
/// <see cref="StreamerBotSourceChip"/>.</item>
/// <item>Event names are run-together PascalCase - <c>GiftSub</c>,
/// <c>HypeTrainLevelUp</c>, <c>CommunityGoalContribution</c> - not the spaced
/// wording Streamer.bot's own UI displays. Its UI is applying the same word
/// break <see cref="SpaceCamelCase"/> does; the wire format is not already
/// readable the way an earlier screenshot suggested.</item>
/// </list>
/// </para>
/// <para>
/// Parsing stays defensive regardless: a source whose value is not an array is
/// skipped, an unusable entry is dropped rather than throwing, and an entirely
/// unrecognisable response yields an empty list - per §B2, a <c>GetEvents</c>
/// failure must not take down the feed.
/// </para>
/// </summary>
public static class StreamerBotEventCatalog
{
    /// <summary>
    /// Parses a full <c>GetEvents</c> response. Never throws - a response
    /// shape this cannot make sense of yields an empty list rather than
    /// taking the caller down, per §B2: "a GetEvents failure must not take
    /// down the feed."
    /// </summary>
    public static IReadOnlyList<StreamerBotEventDescriptor> Parse(JsonElement response)
    {
        if (!response.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<StreamerBotEventDescriptor>();
        foreach (var sourceProperty in events.EnumerateObject())
        {
            if (sourceProperty.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in sourceProperty.Value.EnumerateArray())
            {
                var type = ReadEventType(entry);
                if (!string.IsNullOrWhiteSpace(type))
                {
                    result.Add(new StreamerBotEventDescriptor(sourceProperty.Name, type));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Puts word breaks back into a run-together PascalCase event name, so
    /// <c>HypeTrainLevelUp</c> reads "Hype Train Level Up".
    /// <para>
    /// Deliberately a transform, not a mapping table. A table of "GiftSub" to
    /// "Gift Subscription" would be a hardcoded platform/event list in
    /// disguise - exactly what §5c's governing principle warns against - and
    /// would leave every event Streamer.bot adds after this was written
    /// looking worse than the ones somebody happened to type out. This costs
    /// slightly clumsier wording on a few names ("Gift Sub", not "Gift
    /// Subscription") in exchange for handling all 467 of them, and every
    /// future one, identically.
    /// </para>
    /// <para>
    /// A run of capitals is kept together and only broken before the last one
    /// when a lowercase letter follows it, so <c>SevenTVEmoteAdded</c> reads
    /// "Seven TV Emote Added" rather than "Seven T V Emote Added". A digit
    /// starts a new word for the same reason a capital does.
    /// </para>
    /// </summary>
    public static string SpaceCamelCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var result = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (index > 0 && NeedsBreakBefore(value, index))
            {
                result.Append(' ');
            }

            result.Append(current);
        }

        return result.ToString();
    }

    /// <summary>
    /// Whether a word break belongs immediately before <paramref name="index"/>:
    /// at a lowercase-to-uppercase or letter-to-digit step, and at the tail of
    /// a capital run that turns back into lowercase (the "TVEmote" case).
    /// </summary>
    private static bool NeedsBreakBefore(string value, int index)
    {
        var current = value[index];
        var previous = value[index - 1];

        if (char.IsDigit(current) != char.IsDigit(previous))
        {
            return true;
        }

        if (!char.IsUpper(current))
        {
            return false;
        }

        // "abC" - an ordinary word boundary.
        if (!char.IsUpper(previous))
        {
            return true;
        }

        // "ABc" - the last capital of a run starts the next word, not the
        // acronym: SevenTVEmoteAdded breaks before the E, not before the V.
        return index + 1 < value.Length && char.IsLower(value[index + 1]);
    }

    /// <summary>A bare string entry, or an object carrying <c>type</c> or <c>name</c> - whichever this version of Streamer.bot sends.</summary>
    private static string? ReadEventType(JsonElement entry) =>
        entry.ValueKind switch
        {
            JsonValueKind.String => entry.GetString(),
            JsonValueKind.Object when entry.TryGetProperty("type", out var type)
                                       && type.ValueKind == JsonValueKind.String => type.GetString(),
            JsonValueKind.Object when entry.TryGetProperty("name", out var name)
                                       && name.ValueKind == JsonValueKind.String => name.GetString(),
            _ => null
        };
}
