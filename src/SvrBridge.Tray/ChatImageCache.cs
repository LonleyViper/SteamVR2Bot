using System.Windows.Media.Imaging;
using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// Turns image URLs into decoded, frozen images, fetched lazily and cached
/// for the life of the worker process. Used for both emote images (looked up
/// by name through a catalog) and badge images (whose URL arrives directly
/// on each message, no catalog needed) - the fetch/decode/cache machinery is
/// identical either way, so one type serves both rather than forking it.
/// <para>
/// Split from <see cref="TwitchEmoteCatalog"/> deliberately: the catalog is a
/// pure name → URL lookup that needs no headset and no network to test, while
/// this type owns the HTTP client and the WPF decode - the part that has real
/// I/O and real failure modes. Neither <see cref="TryGet"/> nor
/// <see cref="TryGetByUrl"/> ever blocks: a cache miss kicks off a background
/// fetch and returns immediately, so a chat repaint is never held up waiting
/// on a network round trip.
/// </para>
/// <para>
/// A fetch failure is not cached - only the in-flight marker is cleared - so
/// a transient network error costs the current repaint one missing image
/// (the renderer's text fallback covers the gap) rather than permanently
/// giving up on that image for the rest of the session. Retries happen
/// naturally, paced by how often that emote or badge actually reappears in
/// chat, with no separate retry timer needed.
/// </para>
/// </summary>
internal sealed class ChatImageCache : IDisposable
{
    /// <summary>
    /// Emotes and badges both render inline at roughly chat-line height, not
    /// their native CDN resolution (Twitch and BTTV both serve up to ~112px
    /// for their largest emote size). Capping the decode here, rather than
    /// downscaling a full decode later, keeps memory bounded regardless of
    /// how many distinct images a session ends up caching.
    /// </summary>
    private const int MaxDecodePixelHeight = 64;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly object _gate = new();
    private readonly Dictionary<string, BitmapImage> _imagesByUrl = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string> _emoteCatalog =
        new Dictionary<string, string>(StringComparer.Ordinal);
    private long _version;
    private bool _disposed;

    /// <param name="httpClient">
    /// Injectable for self-tests, which point this at a fake handler rather
    /// than the network. Owned and disposed by this instance only when the
    /// caller did not supply one.
    /// </param>
    public ChatImageCache(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>
    /// Bumped once per newly-decoded image. Combined with
    /// <c>ChatRingBuffer.Version</c> by <c>ChatOverlay</c>'s repaint throttle,
    /// so an image that finishes downloading after its message was already
    /// painted as text still gets a repaint - not just new messages do.
    /// </summary>
    public long Version
    {
        get
        {
            lock (_gate)
            {
                return _version;
            }
        }
    }

    /// <summary>Replaces the known emote name → URL lookup. Safe to call repeatedly; the newest catalog wins.</summary>
    public void SetEmoteCatalog(IReadOnlyDictionary<string, string> catalog)
    {
        lock (_gate)
        {
            _emoteCatalog = catalog;
        }
    }

    /// <summary>
    /// Returns a cached, already-decoded, frozen emote image for
    /// <paramref name="emoteName"/> if one is ready, resolving the URL
    /// through the emote catalog. Never blocks - see the type-level remarks.
    /// </summary>
    public bool TryGet(string emoteName, out BitmapImage? image)
    {
        string? url;
        lock (_gate)
        {
            if (_disposed || !_emoteCatalog.TryGetValue(emoteName, out url))
            {
                image = null;
                return false;
            }
        }

        return TryGetByUrl(url, out image);
    }

    /// <summary>
    /// Returns a cached, already-decoded, frozen image for
    /// <paramref name="url"/> if one is ready. Used directly for badge
    /// images, whose URL is already known per-message with no catalog
    /// lookup needed. Never blocks - see the type-level remarks.
    /// </summary>
    public bool TryGetByUrl(string url, out BitmapImage? image)
    {
        var shouldFetch = false;
        lock (_gate)
        {
            if (_imagesByUrl.TryGetValue(url, out image))
            {
                return true;
            }

            if (!_disposed && _inFlight.Add(url))
            {
                shouldFetch = true;
            }
        }

        image = null;
        if (shouldFetch)
        {
            _ = FetchAsync(url);
        }

        return false;
    }

    private async Task FetchAsync(string url)
    {
        BitmapImage? decoded = null;
        try
        {
            var bytes = await _httpClient.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelHeight = MaxDecodePixelHeight;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            // Freeze makes it immutable and safe to hand to the WPF render
            // thread later, without that thread having created it.
            bitmap.Freeze();
            decoded = bitmap;
        }
        catch (Exception)
        {
            // Deliberately swallowed - see the type-level remarks on why a
            // failure is not cached as a permanent miss.
        }

        lock (_gate)
        {
            _inFlight.Remove(url);
            if (decoded is not null)
            {
                _imagesByUrl[url] = decoded;
                _version++;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
