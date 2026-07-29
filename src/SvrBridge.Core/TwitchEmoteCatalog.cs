using System.Text.Json;

namespace SvrBridge.Core;

/// <summary>
/// A name → image URL lookup built from Streamer.bot's <c>TwitchGetEmotes</c>
/// WebSocket request response.
/// <para>
/// That one request already aggregates Twitch's own emotes (globals,
/// subscriptions, prime, bits tiers, rewards, and more) alongside BetterTTV,
/// FrankerFaceZ and 7TV globals, each with a ready CDN <c>imageUrl</c> -
/// confirmed against a live Streamer.bot instance rather than assumed. That
/// means none of those platforms' own APIs need to be called from here:
/// Streamer.bot has already done the aggregation, keeping every byte of
/// platform-specific knowledge on its side of the line, exactly like every
/// other piece of this feature.
/// </para>
/// <para>
/// Deliberately just a lookup, not a fetcher - see <c>ChatImageCache</c> in
/// <c>SvrBridge.Tray</c> for the part that turns a URL into pixels, which
/// needs an HTTP client and a WPF decode and has no business living in a
/// project with neither.
/// </para>
/// </summary>
public sealed class TwitchEmoteCatalog
{
    private readonly IReadOnlyDictionary<string, string> _imageUrlsByName;

    private TwitchEmoteCatalog(IReadOnlyDictionary<string, string> imageUrlsByName)
    {
        _imageUrlsByName = imageUrlsByName;
    }

    public static TwitchEmoteCatalog Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal));

    public int Count => _imageUrlsByName.Count;

    public bool TryGetImageUrl(string name, out string imageUrl) =>
        _imageUrlsByName.TryGetValue(name, out imageUrl!);

    /// <summary>
    /// The lookup itself, for the tray process to hand to the OpenVR worker
    /// over the command channel - see <c>OpenVrWorkerCommand.EmoteCatalog</c>.
    /// The worker never parses a <c>TwitchGetEmotes</c> response itself, so it
    /// only ever needs the plain map, not this type.
    /// </summary>
    public IReadOnlyDictionary<string, string> ImageUrlsByName => _imageUrlsByName;

    /// <summary>
    /// Parses a <c>TwitchGetEmotes</c> response body:
    /// <c>{"emotes":{"userEmotes":[{"name":"...","imageUrl":"..."},...]}}</c>.
    /// Defensive per the same rule as every other parser downstream of a
    /// network response - one malformed entry costs one skipped emote, never
    /// the whole catalog, and an unexpected shape costs an empty catalog
    /// rather than an exception the caller would otherwise have to guard.
    /// </summary>
    public static TwitchEmoteCatalog Parse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("emotes", out var emotes)
            || emotes.ValueKind != JsonValueKind.Object
            || !emotes.TryGetProperty("userEmotes", out var userEmotes)
            || userEmotes.ValueKind != JsonValueKind.Array)
        {
            return Empty;
        }

        // Last one wins on a duplicate name, which only happens if Streamer.bot
        // itself returns the same emote name twice - not worth failing over.
        var imageUrlsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in userEmotes.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("name", out var nameElement)
                || nameElement.ValueKind != JsonValueKind.String
                || !entry.TryGetProperty("imageUrl", out var urlElement)
                || urlElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
            {
                continue;
            }

            imageUrlsByName[name] = url;
        }

        return imageUrlsByName.Count == 0 ? Empty : new TwitchEmoteCatalog(imageUrlsByName);
    }
}
