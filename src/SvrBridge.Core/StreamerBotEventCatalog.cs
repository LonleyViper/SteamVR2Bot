using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>One event a connected Streamer.bot instance can emit, as <c>GetEvents</c> reported it.</summary>
public sealed record StreamerBotEventDescriptor(string Source, string Type)
{
    /// <summary>The "Source.Type" form every setting and template lookup in this app keys events by.</summary>
    public string Key => $"{Source}.{Type}";
}

/// <summary>
/// Parses a <c>GetEvents</c> response into the flat list of events the
/// Notifications tab's toggles are built from, per §B2 of the Phase 7 plan.
/// <para>
/// <c>GetEvents</c> itself carries no documentation on Streamer.bot's own
/// site - both the request/response reference and the events guide are
/// marked "Documentation Needed", the same gap this project already hit for
/// <c>TwitchGetEmotes</c>. The shape here - an object keyed by source name,
/// each value an array of event type names - mirrors the <c>Subscribe</c>
/// request's own <c>events</c> argument, which <b>is</b> documented and which
/// this app already sends successfully; that symmetry is the best evidence
/// available without a live instance to confirm against, so parsing stays
/// defensive rather than strict: a source whose value is not an array is
/// skipped, and an array entry that is not a plain string but carries its own
/// <c>name</c>/<c>type</c> property is still read rather than dropped, in
/// case a future Streamer.bot version enriches the shape the way its
/// <c>TwitchGetEmotes</c> response already does for emotes.
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
