using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Maps a raw <c>Twitch.ChatMessage</c> websocket event - the parsed output of
/// Streamer.bot's own Twitch connection, OAuth and reconnection handling, not
/// anything this app talks to directly - into the same
/// <see cref="StreamerBotEventPayload"/> shape a hand-written
/// <c>WebsocketBroadcastJson</c> relay action would have produced, per §B6 of
/// the chat plan.
/// <para>
/// Streamer.bot has shipped at least two different shapes for this event
/// across versions, confirmed by comparing the current docs.streamer.bot
/// schema against the <c>@streamerbot/client</c> npm package's bundled type
/// definitions (still the latest published version at the time this was
/// written, and still describing the older shape):
/// </para>
/// <list type="bullet">
/// <item>Older: every field wrapped inside a <c>message</c> object -
/// <c>message.displayName</c>, <c>message.color</c>, <c>message.message</c>
/// (the text), <c>message.badges</c>.</item>
/// <item>Newer: fields at the top level and under a <c>user</c> object -
/// <c>user.name</c>, <c>user.color</c>, top-level <c>text</c>,
/// <c>user.badges</c>.</item>
/// </list>
/// <para>
/// Both are read defensively here rather than assuming one - a field that is
/// missing, renamed again, or the wrong type costs one dropped chat message,
/// never the feed, exactly like
/// <see cref="StreamerBotEventPayload.TryParse(JsonElement, out StreamerBotEventPayload, out string)"/>.
/// This has not been confirmed against a live payload from a running
/// Streamer.bot instance; treat that as outstanding until one is captured
/// from the activity log and checked against this mapping.
/// </para>
/// <para>
/// Deliberately isolated in its own file, importing nothing from
/// <see cref="StreamerBotEventStream"/> beyond the payload type it produces,
/// so a future YouTube or Kick mapper is an additional file rather than a
/// change to this one or to the stream's dispatch logic.
/// </para>
/// </summary>
public static class TwitchChatMessageMapper
{
    public static bool TryMap(
        JsonElement data,
        [NotNullWhen(true)] out StreamerBotEventPayload? payload,
        out string rejection)
    {
        payload = null;
        if (data.ValueKind != JsonValueKind.Object)
        {
            rejection = "the Twitch chat message payload was not an object";
            return false;
        }

        // Older shape wraps everything in "message"; newer shape does not,
        // so falling back to the root element handles both with one path.
        var record = data.TryGetProperty("message", out var wrapped)
            && wrapped.ValueKind == JsonValueKind.Object
                ? wrapped
                : data;

        // Newer shape nests user fields under "user"; older shape has no
        // such object and keeps them directly on "record" - falling back to
        // record itself handles both the same way.
        var user = record.TryGetProperty("user", out var userElement)
            && userElement.ValueKind == JsonValueKind.Object
                ? userElement
                : record;

        // Both shapes name the text field "text" or "message"; the older
        // shape's inner field happens to be named "message" too, confusingly
        // the same as its own wrapper, which is exactly why this is tried
        // second rather than assumed absent.
        var text = ReadString(record, "text");
        if (text.Length == 0)
        {
            text = ReadString(record, "message");
        }

        if (text.Length == 0)
        {
            rejection = "the Twitch chat message had no text";
            return false;
        }

        var displayName = ReadString(user, "name");
        if (displayName.Length == 0)
        {
            displayName = ReadString(user, "displayName");
        }

        if (displayName.Length == 0)
        {
            displayName = ReadString(user, "username");
        }

        if (displayName.Length == 0)
        {
            displayName = ReadString(user, "login");
        }

        var badges = ReadBadges(user, record);
        payload = new StreamerBotEventPayload
        {
            Target = StreamerBotEventTarget.Chat,
            User = displayName,
            Colour = StreamerBotEventPayload.NormaliseColour(ReadString(user, "color")),
            // First entry mirrored onto the singular fields for anything
            // still reading them directly - see StreamerBotEventPayload's
            // own remarks on why both exist.
            Badge = badges.Count > 0 ? badges[0].Label : "",
            BadgeImageUrl = badges.Count > 0 ? badges[0].ImageUrl : "",
            Badges = badges,
            Text = text,
            EmoteNames = ReadEmoteNames(record)
        };
        rejection = "";
        return true;
    }

    /// <summary>
    /// Both known schema shapes place an <c>emotes</c> array at the same
    /// level as the message text - see the field-name table in
    /// <c>LIVE_TEST_RESULTS.md</c> - so this needs no shape branching. The
    /// older shape additionally carries bit-cheer emotes separately; both
    /// are merged into one flat list since the renderer treats every emote
    /// name the same way.
    /// </summary>
    private static IReadOnlyList<string> ReadEmoteNames(JsonElement record)
    {
        var names = new List<string>();
        AppendEmoteNames(record, "emotes", names);
        AppendEmoteNames(record, "cheerEmotes", names);
        return names;
    }

    private static void AppendEmoteNames(JsonElement record, string propertyName, List<string> names)
    {
        if (!record.TryGetProperty(propertyName, out var emotes) || emotes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            var name = ReadString(emote, "name");
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }
    }

    /// <summary>
    /// Every badge on the message, in the order Twitch's own badges array
    /// gives them - not just one guessed "most important" one. An earlier
    /// version of this picked a single badge from four hardcoded name
    /// categories (broadcaster/moderator/vip/subscriber) and discarded
    /// everything else, which silently dropped Prime/Premium, bits, partner,
    /// turbo and - the case that actually cannot be enumerated in advance -
    /// a channel's own custom loyalty badge. Badge names are the one field
    /// both known schema shapes give as plain, unambiguous strings, so every
    /// entry is kept; only the display label is best-effort for names this
    /// app does not specifically recognise.
    /// <para>
    /// The numeric "role" field both shapes also carry is deliberately not
    /// used: neither the docs nor the npm package's type definitions
    /// document what its values mean, and mislabelling a viewer's role from
    /// a guessed enum would be worse than a plain badge name.
    /// </para>
    /// <para>
    /// Both shapes' badge entries also carry their own <c>imageUrl</c>
    /// (confirmed alongside the emote catalog research - <c>TwitchBadge</c>
    /// has <c>name</c>, <c>version</c> and <c>imageUrl</c> in both the docs
    /// and the npm client's bundled types), so the same pass that builds the
    /// label list also carries every badge's own icon - no separate request
    /// needed, unlike emotes.
    /// </para>
    /// </summary>
    private static IReadOnlyList<ChatBadge> ReadBadges(JsonElement user, JsonElement record)
    {
        var result = new List<ChatBadge>();
        if (user.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in badges.EnumerateArray())
            {
                var name = ReadString(entry, "name");
                if (name.Length == 0)
                {
                    continue;
                }

                result.Add(new ChatBadge(FriendlyBadgeLabel(name), ReadString(entry, "imageUrl")));
            }
        }

        // Badge lists are not guaranteed to include every role a chatter
        // holds - both shapes separately carry an explicit subscriber flag,
        // worth a fallback for the one role that matters most for a chat
        // window when the badges array did not already cover it. No image
        // is available through this fallback path.
        if (result.TrueForAll(badge => badge.Label != "Sub")
            && (ReadBool(user, "subscribed") || ReadBool(record, "subscriber")))
        {
            result.Add(new ChatBadge("Sub", ""));
        }

        return result;
    }

    /// <summary>
    /// A handful of Twitch's own badge-set names get a short, familiar
    /// label; anything else - most importantly a channel's own custom
    /// loyalty badge, which cannot be enumerated in advance - is shown under
    /// its own raw name rather than dropped. The image, not this label, is
    /// what most chatters will actually recognise once one is cached.
    /// </summary>
    private static string FriendlyBadgeLabel(string rawName)
    {
        var name = rawName.ToLowerInvariant();
        if (name.Contains("broadcaster"))
        {
            return "Broadcaster";
        }

        if (name.Contains("moderator"))
        {
            return "Mod";
        }

        if (name.Contains("vip"))
        {
            return "VIP";
        }

        if (name.Contains("subscriber") || name.Contains("founder"))
        {
            return "Sub";
        }

        if (name.Contains("premium"))
        {
            return "Prime";
        }

        if (name.Contains("partner"))
        {
            return "Partner";
        }

        if (name.Contains("staff"))
        {
            return "Staff";
        }

        if (name.Contains("turbo"))
        {
            return "Turbo";
        }

        if (name.Contains("bits"))
        {
            return "Bits";
        }

        return rawName;
    }

    private static string ReadString(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static bool ReadBool(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();
}
