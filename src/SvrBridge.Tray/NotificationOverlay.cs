using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// The notification surface: one persistent <see cref="VrOverlaySurface"/>,
/// a <see cref="NotificationPlayer"/> queue and timeline, and a
/// <see cref="IVrPanelRenderer{TContent}"/> that paints once per item.
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
/// <para>
/// Anchoring is head-relative by default (Phase 2's proven placement), but is
/// no longer hardcoded: it is driven by an <see cref="OverlayAnchorTracker"/>
/// so a Streamer.bot <c>anchor</c> control command can move it to a
/// controller instead - see §B2 and §B3 of the Phase 4 plan.
/// </para>
/// </summary>
internal sealed class NotificationOverlay : IDisposable
{
    private const string OverlayKey = "ie.lonelyviper.svrbridge.notifications";
    private const float WidthInMeters = 0.5f;

    private readonly VrOverlaySurface _surface;
    private readonly IVrPanelRenderer<NotificationContent> _renderer;
    private readonly NotificationPlayer _player;
    private readonly OverlayAnchorTracker _anchorTracker;
    private readonly Action<string> _log;

    // Opacity scales the peak alpha the fade curve holds at; size multiplies
    // WidthInMeters. Applied live via SetOpacity/SetSizeScale so a VR or
    // desktop settings change is visible on the very next notification.
    private double _opacity = 1.0;
    private double _sizeScale = 1.0;

    private bool _hidden;
    private bool _shown;
    private bool _disposed;

    private NotificationOverlay(
        VrOverlaySurface surface,
        IVrPanelRenderer<NotificationContent> renderer,
        NotificationPlayer player,
        OverlayAnchorTracker anchorTracker,
        Action<string> log)
    {
        _surface = surface;
        _renderer = renderer;
        _player = player;
        _anchorTracker = anchorTracker;
        _log = log;
    }

    /// <summary>
    /// Creates the surface, attaches it at <paramref name="defaultAnchor"/>,
    /// and leaves it hidden until the first notification arrives. Returns
    /// null when this SteamVR version has no overlay interface, which is not
    /// worth failing the worker over.
    /// </summary>
    public static NotificationOverlay? TryCreate(
        OpenVrInput openVr,
        OverlayAnchor defaultAnchor,
        double opacity,
        double sizeScale,
        Action<string> log)
    {
        if (!openVr.SupportsOverlaySurfaces)
        {
            log("Notifications were skipped: this SteamVR version has no overlay interface.");
            return null;
        }

        var surface = openVr.CreateOverlaySurface(OverlayKey, "SteamVR2Bot notifications");
        IVrPanelRenderer<NotificationContent>? renderer = null;
        try
        {
            surface.SetWidthInMeters(WidthInMeters * (float)sizeScale);
            surface.SetCurvature(0.05f);
            surface.SetAlpha(0f);
            renderer = new WpfNotificationRenderer();
            var anchorTracker = new OverlayAnchorTracker("Notifications", defaultAnchor, log);
            anchorTracker.Tick(openVr, surface);
            return new NotificationOverlay(surface, renderer, new NotificationPlayer(), anchorTracker, log)
            {
                _opacity = opacity,
                _sizeScale = sizeScale
            };
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
    /// Moves this surface to a different anchor - the effect of a
    /// Streamer.bot <c>anchor</c> control command, or a <c>reset</c> handing
    /// back the saved default. A no-op if it is already there.
    /// </summary>
    public void SetAnchorOverride(OverlayAnchor anchor) => _anchorTracker.SetAnchor(anchor);

    /// <summary>
    /// Forces this surface hidden regardless of the notification timeline, or
    /// clears that override - the effect of a Streamer.bot <c>hide</c>/<c>show</c>
    /// control command.
    /// </summary>
    public void SetHidden(bool hidden) => _hidden = hidden;

    /// <summary>
    /// Drops every queued notification behind whatever is showing right now,
    /// without interrupting it - the effect of a <c>clear</c> control command.
    /// </summary>
    public void ClearQueue() => _player.ClearQueue();

    /// <summary>Sets the peak hold alpha, 0.2-1.0 - a VR or desktop settings change, visible on the next notification.</summary>
    public void SetOpacity(double opacity) => _opacity = Math.Clamp(opacity, 0.2, 1.0);

    /// <summary>Sets the width multiplier, 0.5-2.0 - applied immediately, since width is not animated.</summary>
    public void SetSizeScale(double sizeScale)
    {
        _sizeScale = Math.Clamp(sizeScale, 0.5, 2.0);
        _surface.SetWidthInMeters(WidthInMeters * (float)_sizeScale);
    }

    /// <summary>
    /// Re-resolves the anchor, advances the fade timeline and, only when a
    /// new item just started, paints its texture. While idle and not hidden
    /// this makes no OpenVR call beyond the anchor check - the "animation
    /// timer" is this method's own no-op path, not a separate timer racing
    /// the 10 ms input poll that calls it.
    /// </summary>
    public void Tick(OpenVrInput openVr, long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        _anchorTracker.Tick(openVr, _surface);

        var frame = _player.Tick(nowMs);
        if (_hidden)
        {
            if (_shown)
            {
                _surface.Hide();
                _shown = false;
            }

            return;
        }

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

        _surface.SetAlpha(frame.Alpha * (float)_opacity);
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
