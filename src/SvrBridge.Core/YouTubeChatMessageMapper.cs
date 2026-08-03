using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Maps Streamer.bot's parsed <c>YouTube.Message</c> event into the chat
/// payload consumed by the wrist overlay. This remains deliberately separate
/// from <see cref="TwitchChatMessageMapper"/>: Streamer.bot owns each
/// platform connection, while this app only translates its already-normalised
/// websocket events at the boundary.
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

        var text = ReadString(data, "message");
        if (text.Length == 0)
        {
            text = ReadString(data, "text");
        }

        if (text.Length == 0)
        {
            rejection = "the YouTube chat message had no text";
            return false;
        }

        var user = data.TryGetProperty("user", out var userElement)
                   && userElement.ValueKind == JsonValueKind.Object
            ? userElement
            : data;
        var displayName = ReadFirstString(user, "display", "name", "displayName", "username", "login");

        payload = new StreamerBotEventPayload
        {
            Target = StreamerBotEventTarget.Chat,
            User = displayName,
            Colour = StreamerBotEventPayload.NormaliseColour(ReadFirstString(user, "color", "colour")),
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
        if (!data.TryGetProperty("emotes", out var emotes) || emotes.ValueKind != JsonValueKind.Array)
        {
            return names;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            var name = ReadString(emote, "name");
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
        if (!data.TryGetProperty("emotes", out var emotes) || emotes.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var emote in emotes.EnumerateArray())
        {
            var name = ReadString(emote, "name");
            if (name.Length == 0
                || !emote.TryGetProperty("startIndex", out var startValue)
                || !startValue.TryGetInt32(out var startIndex)
                || !emote.TryGetProperty("endIndex", out var endValue)
                || !endValue.TryGetInt32(out var endIndex)
                || startIndex < 0
                || endIndex < startIndex
                || endIndex >= text.Length)
            {
                continue;
            }

            result.Add(new ChatEmote(name, startIndex, endIndex, ReadString(emote, "imageUrl")));
        }

        return result;
    }

    private static string ReadFirstString(JsonElement body, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ReadString(body, name);
            if (value.Length > 0)
            {
                return value;
            }
        }

        return "";
    }

    private static string ReadString(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object
        && body.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
