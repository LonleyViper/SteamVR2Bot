using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// The head-anchored notification surface: one persistent
/// <see cref="VrOverlaySurface"/>, a <see cref="NotificationPlayer"/> queue and
/// timeline, and a <see cref="IVrPanelRenderer"/> that paints once per item.
/// <para>
/// Lives entirely inside the OpenVR worker process, alongside
/// <see cref="VrTestOverlay"/> and <c>VrDashboardController</c> - it is ticked
/// from the same single thread that owns every other OpenVR call, because that
/// is the only thread allowed to make them.
/// </para>
/// <para>
/// Created lazily on the first notification rather than at worker start-up,
/// but never recreated after that: the surface, the renderer and its render
/// thread all live for the rest of the worker's life. Per §B2, a create/destroy
/// cycle per notification is the pattern most likely to leak an overlay handle
/// under load, so this type exists specifically to avoid one.
/// </para>
/// </summary>
internal sealed class NotificationOverlay : IDisposable
{
    private const string OverlayKey = "ie.lonelyviper.svrbridge.notifications";
    private const float WidthInMeters = 0.5f;
    private const uint HmdDeviceIndex = 0;

    /// <summary>
    /// Metres, in the HMD's own frame: +X right, +Y up, +Z back toward the
    /// wearer (see <see cref="VrOverlayTransform.Translation"/>). 0.6 m ahead
    /// is far enough to read comfortably without eye strain; 0.12 m down keeps
    /// it clear of the centre of view, which is "in front and slightly below
    /// centre" per §B3. No rotation is composed in: unlike a controller-relative
    /// panel, SteamVR's tracked-device-relative overlay already faces back
    /// toward the device that owns it, so an HMD-relative panel needs only a
    /// translation to face the wearer.
    /// </summary>
    private static readonly VrOverlayTransform HeadAnchorOffset =
        VrOverlayTransform.Translation(0f, -0.12f, -0.6f);

    private readonly VrOverlaySurface _surface;
    private readonly IVrPanelRenderer _renderer;
    private readonly NotificationPlayer _player;
    private readonly Action<string> _log;
    private bool _shown;
    private bool _disposed;

    private NotificationOverlay(
        VrOverlaySurface surface,
        IVrPanelRenderer renderer,
        NotificationPlayer player,
        Action<string> log)
    {
        _surface = surface;
        _renderer = renderer;
        _player = player;
        _log = log;
    }

    /// <summary>
    /// Creates the surface, attaches it to the HMD, and leaves it hidden until
    /// the first notification arrives. Returns null when this SteamVR version
    /// has no overlay interface, which is not worth failing the worker over.
    /// </summary>
    public static NotificationOverlay? TryCreate(OpenVrInput openVr, Action<string> log)
    {
        if (!openVr.SupportsOverlaySurfaces)
        {
            log("Notifications were skipped: this SteamVR version has no overlay interface.");
            return null;
        }

        var surface = openVr.CreateOverlaySurface(OverlayKey, "SteamVR2Bot notifications");
        IVrPanelRenderer? renderer = null;
        try
        {
            surface.SetWidthInMeters(WidthInMeters);
            surface.SetCurvature(0.05f);
            surface.AttachToDevice(HmdDeviceIndex, HeadAnchorOffset);
            surface.SetAlpha(0f);
            renderer = new WpfNotificationRenderer();
            return new NotificationOverlay(surface, renderer, new NotificationPlayer(), log);
        }
        catch
        {
            // Never leave a handle or a render thread behind when construction
            // fails partway - both hold resources that outlive this call.
            renderer?.Dispose();
            surface.Dispose();
            throw;
        }
    }

    /// <summary>Queues a notification. Safe to call whether or not one is currently showing.</summary>
    public void Enqueue(StreamerBotEventPayload payload)
    {
        if (_disposed)
        {
            return;
        }

        _player.Enqueue(payload);
    }

    /// <summary>
    /// Advances the fade timeline and, only when a new item just started,
    /// paints its texture. While idle this makes no OpenVR call at all - the
    /// "animation timer" is this method's own no-op path, not a separate timer
    /// racing the 10 ms input poll that calls it.
    /// </summary>
    public void Tick(long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        var frame = _player.Tick(nowMs);
        if (frame.Phase == NotificationPhase.Idle)
        {
            if (_shown)
            {
                _surface.Hide();
                _shown = false;
            }

            return;
        }

        if (frame.IsNewItem && frame.Current is { } payload)
        {
            var rendered = _renderer.Render(
                new NotificationContent(payload.Title, payload.Text, payload.Accent));
            _surface.SetTexture(rendered.Rgba, rendered.Width, rendered.Height);
            if (!_shown)
            {
                _surface.Show();
                _shown = true;
                _log("A notification is showing.");
            }
        }

        _surface.SetAlpha(frame.Alpha);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderer.Dispose();
        _surface.Dispose();
    }
}
