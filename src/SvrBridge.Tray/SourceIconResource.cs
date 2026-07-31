using System.Collections.Concurrent;
using System.Reflection;

namespace SvrBridge.Tray;

/// <summary>
/// Looks up a platform icon for one <c>GetEvents</c> source name among this
/// assembly's embedded resources, or reports that there is none.
/// <para>
/// A live <c>GetEvents</c> capture (Streamer.bot 1.0.4) confirmed the response
/// carries no icon, image or logo field at any level - the platform glyphs
/// Streamer.bot shows in its own chat and event viewer are its embedded UI
/// assets, not API data. So any icon here has to be one this app ships. Note
/// the contrast with chat, where image references genuinely do arrive in the
/// payload (Twitch badges as URLs, emotes via <c>TwitchGetEmotes</c>) and
/// <c>ChatImageCache</c> fetches them: that pattern covers message content,
/// not platform identity, and there is no URL to reuse for this.
/// </para>
/// <para>
/// The lookup is by name and nothing else - <c>assets/source-icons/twitch.png</c>
/// serves the source "Twitch" - so this file contains no list of platforms
/// and needs no edit when one is added. A source with no icon file is not an
/// error: the caller falls back to <see cref="Core.StreamerBotSourceChip"/>'s
/// coloured text chip, which is what all 44 sources the live capture reported
/// currently draw, since no icon files ship yet. That makes the fallback the
/// exercised path rather than the theoretical one.
/// </para>
/// </summary>
internal static class SourceIconResource
{
    private const string ResourcePrefix = "SvrBridge.Tray.assets.source-icons.";

    /// <summary>
    /// Decoded icons by lowercased source name, with a null entry recording
    /// "looked, found nothing" so a miss costs one manifest scan rather than
    /// one per row rendered.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Image?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon for <paramref name="source"/>, or null when this build ships
    /// none for it. Never throws: a resource that fails to decode is treated
    /// exactly as a missing one, because a malformed asset must cost a chip,
    /// not the settings window.
    /// </summary>
    public static Image? TryResolve(string? source)
    {
        var key = (source ?? "").Trim();
        return key.Length == 0 ? null : Cache.GetOrAdd(key, Load);
    }

    private static Image? Load(string source)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(candidate =>
                candidate.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)
                && candidate.Length > ResourcePrefix.Length
                && string.Equals(
                    Path.GetFileNameWithoutExtension(candidate[ResourcePrefix.Length..]),
                    source,
                    StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return null;
        }

        try
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            // Copied twice on purpose. Once out of the manifest stream, which
            // is not worth holding open for the life of the process; then out
            // of the decoded image, because Image.FromStream keeps its stream
            // alive for the bitmap's lifetime and would fail on the first
            // paint after the MemoryStream below is disposed.
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            using var decoded = Image.FromStream(buffer, useEmbeddedColorManagement: false, validateImageData: true);
            return new Bitmap(decoded);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or OutOfMemoryException)
        {
            return null;
        }
    }
}
