using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// Field reads shared by the platform chat mappers. Pure JSON plumbing with no
/// platform knowledge in it, so <see cref="TwitchChatMessageMapper"/> and
/// <see cref="YouTubeChatMessageMapper"/> stay independent of each other while
/// still agreeing on what "read this field" means.
/// <para>
/// <b>Lookups are case-insensitive, deliberately.</b>
/// <see cref="JsonElement.TryGetProperty(string)"/> is exact-match, and
/// Streamer.bot's own published schema is not internally consistent about
/// casing: as of 1.0.5 every field on <c>TwitchUser</c> and <c>TwitchBadge</c>
/// is documented camelCase (<c>id</c>, <c>login</c>, <c>imageUrl</c>) while the
/// <c>Emote</c> entries inside the same payload are documented
/// <c>Type</c>/<c>Name</c>/<c>StartIndex</c>/<c>EndIndex</c>/<c>ImageUrl</c>.
/// The live <c>TwitchGetEmotes</c> capture behind <see cref="TwitchEmoteCatalog"/>
/// came back camelCase, so camelCase is very likely what is actually on the
/// wire and the schema page is showing declared C# property names for that one
/// type - but the cost of being wrong is every emote in chat silently losing
/// its image, and the cost of not caring is one dictionary scan per field on a
/// payload that is already being parsed. The exact-match fast path runs first,
/// so the scan only happens for a field that would otherwise have been missed
/// entirely.
/// </para>
/// </summary>
public static class ChatPayloadJson
{
    /// <summary>
    /// The named property, matched exactly if possible and case-insensitively
    /// otherwise. False for a non-object, an absent property, or an explicit
    /// JSON <c>null</c> - callers want "is there a usable value here", and
    /// Streamer.bot sends <c>null</c> rather than omitting fields.
    /// </summary>
    public static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        value = default;
        if (body.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (body.TryGetProperty(name, out var exact))
        {
            if (exact.ValueKind == JsonValueKind.Null)
            {
                return false;
            }

            value = exact;
            return true;
        }

        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind != JsonValueKind.Null)
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The named property when it is an object, otherwise <paramref name="fallback"/>.
    /// This is the "newer shape nests these under <c>user</c>, older shape keeps
    /// them at the root" pattern both mappers use: passing the record itself as
    /// the fallback handles both with one path, and a field Streamer.bot sends
    /// as <c>null</c> - <c>user</c> is nullable in its own schema - falls back
    /// rather than reading nothing.
    /// </summary>
    public static JsonElement ObjectOr(JsonElement body, string name, JsonElement fallback) =>
        TryGetProperty(body, name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : fallback;

    /// <summary>The named array, or false for anything else - including a <c>null</c> array, which Streamer.bot does send.</summary>
    public static bool TryGetArray(JsonElement body, string name, out JsonElement array) =>
        TryGetProperty(body, name, out array) && array.ValueKind == JsonValueKind.Array;

    public static string ReadString(JsonElement body, string name) =>
        TryGetProperty(body, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>The first of <paramref name="names"/> present as a non-empty string, or empty text.</summary>
    public static string ReadFirstString(JsonElement body, params string[] names)
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

    public static bool ReadBool(JsonElement body, string name) =>
        TryGetProperty(body, name, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False
        && value.GetBoolean();

    public static bool TryReadInt32(JsonElement body, string name, out int result)
    {
        result = 0;
        return TryGetProperty(body, name, out var value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out result);
    }

    /// <summary>
    /// The property names present on an object, for the shape probe in
    /// <c>StreamerBotEventStream</c>. Names only - never values - so a shape
    /// change can be confirmed from a log without putting a viewer's name or
    /// what they typed into it.
    /// </summary>
    public static string DescribeKeys(JsonElement body) =>
        body.ValueKind == JsonValueKind.Object
            ? string.Join(", ", body.EnumerateObject().Select(property => property.Name))
            : body.ValueKind.ToString();
}
