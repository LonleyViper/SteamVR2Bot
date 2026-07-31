using System.Collections.Concurrent;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace SvrBridge.Tray;

/// <summary>
/// The WPF half of <see cref="SourceIconResource"/>: the same embedded
/// platform icons, decoded as a <see cref="BitmapSource"/> for the
/// notification renderer rather than a <c>System.Drawing.Image</c> for the
/// desktop picker.
/// <para>
/// Two loaders rather than one converted at the boundary, because converting
/// a GDI+ bitmap into a WPF one per render means marshalling pixels through a
/// memory stream on the render thread for something that never changes. Both
/// read the same files by the same rule - filename equals the <c>Source</c>
/// string <c>GetEvents</c> reported - so neither carries a list of platforms
/// and adding a logo still means dropping in a PNG.
/// </para>
/// </summary>
internal static class SourceIconImage
{
    private const string ResourcePrefix = "SvrBridge.Tray.assets.source_icons.";

    /// <summary>
    /// Decoded icons by source name, with a null entry recording "looked,
    /// found nothing". Frozen on the way in, so the same instance is safe to
    /// hand to the render thread on every notification.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon for <paramref name="source"/>, or null when this build ships
    /// none for it - in which case the notification simply draws its text
    /// without one. Never throws: a resource that fails to decode is treated
    /// exactly as a missing one, because a malformed asset must cost an icon,
    /// not the render thread.
    /// </summary>
    public static BitmapSource? TryResolve(string? source)
    {
        var key = (source ?? "").Trim();
        return key.Length == 0 ? null : Cache.GetOrAdd(key, Load);
    }

    private static BitmapSource? Load(string source)
    {
        try
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

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad so the bitmap does not keep the manifest stream alive
            // past this using block.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            // Frozen because it is cached and reused across renders, on a
            // different thread from the one that decoded it.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A malformed or undecodable icon. Swallowed for the same reason
            // a bad template is: it costs this notification its icon, not the
            // render thread.
            return null;
        }
    }
}
