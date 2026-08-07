using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Maps Streamer.bot's parsed <c>YouTube.Message</c> event into the chat
/// payload consumed by the wrist overlay. This remains deliberately separate
/// from <see cref="TwitchChatMessageMapper"/>: Streamer.bot owns each
/// platform connection, while this app only translates its already-normalised
/// websocket events at the boundary.
/// <para>
/// Streamer.bot 1.0.5's Twitch chat rework - IRC to EventSub, legacy
/// <c>message</c> wrapper removed - does not touch this event, and 1.0.5's own
/// YouTube additions (Jewel gifting, subscriber polling) are new event types
/// rather than changes to <c>YouTube.Message</c>. Field reads still go through
/// <see cref="ChatPayloadJson"/> for the same case-insensitivity reason the
/// Twitch mapper does.
/// </para>
/// </summary>
public static class YouTubeChatMessageMapper
{
    public static bool TryMap(
        JsonElement data,
        [NotNullWhen(true)] out StreamerBotEventPayload? payload,
        out string rejection)
    {
        payload = null;
        if (data.ValueKind != JsonValueKind.Object)
        {
            rejection = "the YouTube chat message payload was not an object";
            return false;
        }

        var text = ChatPayloadJson.ReadFirstString(data, "message", "text");
        if (text.Length == 0)
        {
            rejection = "the YouTube chat message had no text";
            return false;
        }

        var user = ChatPayloadJson.ObjectOr(data, "user", data);
        var displayName = ChatPayloadJson.ReadFirstString(
            user, "display", "name", "displayName", "username", "login");

        payload = new StreamerBotEventPayload
        {
            Target = StreamerBotEventTarget.Chat,
            User = displayName,
            Colour = StreamerBotEventPayload.NormaliseColour(
                ChatPayloadJson.ReadFirstString(user, "color", "colour")),
            Text = text,
            EmoteNames = ReadEmoteNames(data),
            Emotes = ReadEmotes(data, text)
        };
        rejection = "";
        return true;
    }

    private static IReadOnlyList<string> ReadEmoteNames(JsonElement data)
    {
        var names = new List<string>();
        if (!ChatPayloadJson.TryGetArray(data, "emotes", out var emotes))
        {
            return names;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            var name = ChatPayloadJson.ReadString(emote, "name");
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static IReadOnlyList<ChatEmote> ReadEmotes(JsonElement data, string text)
    {
        var result = new List<ChatEmote>();
        if (!ChatPayloadJson.TryGetArray(data, "emotes", out var emotes))
        {
            return result;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            var name = ChatPayloadJson.ReadString(emote, "name");
            if (name.Length == 0
                || !ChatPayloadJson.TryReadInt32(emote, "startIndex", out var startIndex)
                || !ChatPayloadJson.TryReadInt32(emote, "endIndex", out var endIndex)
                || startIndex < 0
                || endIndex < startIndex
                || endIndex >= text.Length)
            {
                continue;
            }

            result.Add(
                new ChatEmote(name, startIndex, endIndex, ChatPayloadJson.ReadString(emote, "imageUrl")));
        }

        return result;
    }
}
