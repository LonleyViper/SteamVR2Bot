using System.Numerics;
using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// The wrist-anchored chat window: one persistent <see cref="VrOverlaySurface"/>
/// pinned to the left controller, a <see cref="ChatRingBuffer"/> written by
/// the event stream's consumption loop, and a WPF renderer that repaints at
/// most ~10 Hz per §B2 of the chat plan.
/// <para>
/// Lives entirely inside the OpenVR worker process, ticked from the same
/// single thread that owns every other OpenVR call - see
/// <see cref="NotificationOverlay"/>, which this mirrors in every structural
/// respect except what it is anchored to and how its visibility works.
/// </para>
/// <para>
/// Visibility is gaze-scale, not show/hide, per §B1: the window is always
/// present behind the controller, small and faint, and grows large and
/// opaque when the wearer looks at it. That animation runs on every tick
/// through <see cref="VrOverlaySurface.SetAlpha"/> and
/// <see cref="VrOverlaySurface.SetWidthInMeters"/> alone - no texture
/// repaint is involved - and is entirely decoupled from the throttled text
/// repaint below: the two run on independent clocks by design.
/// </para>
/// </summary>
internal sealed class ChatOverlay : IDisposable
{
    private const string OverlayKey = "ie.lonelyviper.svrbridge.chat";

    private const float SmallWidthMeters = 0.12f;
    private const float LargeWidthMeters = 0.32f;
    private const float SmallAlpha = 0.35f;
    private const float LargeAlpha = 0.95f;

    /// <summary>
    /// Sits above and slightly in front of the controller origin, tipped
    /// back like a watch face - the exact transform Phase 1's test overlay
    /// proved against the headset. Reused rather than reinvented, per §B5.
    /// </summary>
    private static readonly VrOverlayTransform WristOffset =
        VrOverlayTransform.Translation(0f, 0.06f, -0.12f)
        * VrOverlayTransform.RotationX(-0.6f);

    /// <summary>
    /// A 150 ms time constant for the gaze-scale ease: fast enough that
    /// looking at the window feels immediate, slow enough that the grow and
    /// shrink read as motion rather than a snap.
    /// </summary>
    private const float SmoothingTimeConstantMs = 150f;

    private readonly VrOverlaySurface _surface;
    private readonly IVrPanelRenderer<ChatContent> _renderer;
    private readonly ChatRingBuffer _messages = new();
    private readonly ChatGazeHysteresis _gaze = new();
    private readonly ChatRepaintThrottle _repaintThrottle = new();
    private readonly ChatImageCache _chatImages;
    private readonly Action<string> _log;

    // The index the transform is currently bound to. Kept only to notice
    // when it changes, never trusted as the source of truth - see Tick.
    private uint? _boundDeviceIndex;
    private bool _warnedAboutMissingController;
    private float _currentWidth = SmallWidthMeters;
    private float _currentAlpha = SmallAlpha;
    private long? _lastAnimateMs;
    private bool _disposed;

    private ChatOverlay(
        VrOverlaySurface surface,
        IVrPanelRenderer<ChatContent> renderer,
        ChatImageCache chatImages,
        Action<string> log)
    {
        _surface = surface;
        _renderer = renderer;
        _chatImages = chatImages;
        _log = log;
    }

    /// <summary>
    /// Creates the surface and shows it immediately, small and faint, at the
    /// wrist. Returns null when this SteamVR version has no overlay
    /// interface, which is not worth failing the worker over.
    /// </summary>
    public static ChatOverlay? TryCreate(OpenVrInput openVr, Action<string> log)
    {
        if (!openVr.SupportsOverlaySurfaces)
        {
            log("Chat was skipped: this SteamVR version has no overlay interface.");
            return null;
        }

        var surface = openVr.CreateOverlaySurface(OverlayKey, "SteamVR2Bot chat");
        var chatImages = new ChatImageCache();
        IVrPanelRenderer<ChatContent>? renderer = null;
        try
        {
            surface.SetWidthInMeters(SmallWidthMeters);
            surface.SetCurvature(0.05f);
            surface.SetAlpha(SmallAlpha);
            surface.SetSortOrder(0);
            renderer = new WpfChatRenderer(chatImages);
            var overlay = new ChatOverlay(surface, renderer, chatImages, log);
            surface.Show();
            log("The chat window is on. It stays behind your left controller and grows on gaze.");
            return overlay;
        }
        catch
        {
            // Never leave a handle, a render thread or an HTTP client behind
            // when construction fails partway - all three hold resources
            // that outlive this call.
            renderer?.Dispose();
            chatImages.Dispose();
            surface.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Replaces the known emote name → image URL lookup, fetched once by the
    /// tray process from Streamer.bot's <c>TwitchGetEmotes</c> request and
    /// pushed down over the worker command channel - see
    /// <c>OpenVrWorker</c>'s <c>"emoteCatalog"</c> command. Safe to call
    /// before the overlay has ever shown anything.
    /// </summary>
    public void SetEmoteCatalog(IReadOnlyDictionary<string, string> catalog)
    {
        if (_disposed)
        {
            return;
        }

        _chatImages.SetEmoteCatalog(catalog);
    }

    /// <summary>Appends one chat message. Safe to call from any thread - see <see cref="ChatRingBuffer"/>.</summary>
    public void Enqueue(StreamerBotEventPayload payload)
    {
        if (_disposed)
        {
            return;
        }

        _messages.Append(payload);
    }

    /// <summary>
    /// Re-resolves the wrist attachment, advances the gaze-scale animation,
    /// and repaints the texture when owed. Called once per OpenVR poll
    /// (~10 ms), like <see cref="VrTestOverlay.Tick"/> and
    /// <see cref="NotificationOverlay.Tick"/>.
    /// </summary>
    public void Tick(OpenVrInput openVr, long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        ReattachIfNeeded(openVr);
        AnimateGaze(openVr, nowMs);
        RepaintIfOwed(nowMs);
    }

    /// <summary>
    /// The device index is asked for on every tick rather than cached, for
    /// the same reason <see cref="VrTestOverlay"/> does: tracked device
    /// indices are not stable across controller sleep, reconnect or a
    /// battery change, and a role can come back unassigned. An overlay bound
    /// to a stale index detaches with no error reported anywhere.
    /// </summary>
    private void ReattachIfNeeded(OpenVrInput openVr)
    {
        var deviceIndex = openVr.TryGetControllerDeviceIndex(ControllerHand.Left);
        if (deviceIndex is null)
        {
            if (!_warnedAboutMissingController)
            {
                _warnedAboutMissingController = true;
                _log("The chat window is waiting for a left controller.");
            }

            _boundDeviceIndex = null;
            return;
        }

        _warnedAboutMissingController = false;
        if (_boundDeviceIndex == deviceIndex)
        {
            return;
        }

        _surface.AttachToDevice(deviceIndex.Value, WristOffset);
        _boundDeviceIndex = deviceIndex;
        _log($"The chat window is following left controller device {deviceIndex.Value}.");
    }

    private void AnimateGaze(OpenVrInput openVr, long nowMs)
    {
        var deltaMs = _lastAnimateMs is { } last ? Math.Max(0L, nowMs - last) : 0L;
        _lastAnimateMs = nowMs;

        var isGazing = _gaze.Update(GazeDot(openVr.LatestMotion));
        var targetWidth = isGazing ? LargeWidthMeters : SmallWidthMeters;
        var targetAlpha = isGazing ? LargeAlpha : SmallAlpha;

        // Exponential ease towards the target rather than an instant jump,
        // so the transition reads as smooth motion - required by the manual
        // "no flicker" check in the headset matrix.
        var t = deltaMs <= 0 ? 1f : 1f - MathF.Exp(-deltaMs / SmoothingTimeConstantMs);
        _currentWidth += (targetWidth - _currentWidth) * t;
        _currentAlpha += (targetAlpha - _currentAlpha) * t;

        _surface.SetWidthInMeters(_currentWidth);
        _surface.SetAlpha(_currentAlpha);
    }

    /// <summary>
    /// Cosine of the angle between the head's forward vector and the
    /// head-to-window direction, both already expressed in
    /// <see cref="MotionSample"/>'s head-relative <see cref="BodyFrame"/>.
    /// The window is treated as co-located with the left controller for this
    /// purpose - close enough at wrist distance to matter.
    /// <para>
    /// Because that frame's own forward axis is, by construction, its local
    /// +Z, the dot product collapses to the Z component of the normalised
    /// direction to the controller - no explicit forward vector is needed.
    /// <see cref="BodyFrame"/> is yaw-only (see its own remarks), so this
    /// reads as "roughly facing the wrist's direction" rather than a true
    /// eye-line check that accounts for head pitch; that is the same
    /// simplification the rest of this codebase's gesture recognition
    /// already makes with the same data.
    /// </para>
    /// </summary>
    private static float GazeDot(MotionSample motion)
    {
        if ((motion.Tracking & MotionTracking.Left) == 0)
        {
            return -1f;
        }

        var direction = motion.LeftPosition;
        var lengthSquared = direction.LengthSquared();
        return lengthSquared < 1e-6f ? -1f : direction.Z / MathF.Sqrt(lengthSquared);
    }

    private void RepaintIfOwed(long nowMs)
    {
        var (snapshot, messagesVersion) = _messages.SnapshotWithVersion();

        // An emote image that finishes downloading after its message was
        // already painted as text must still earn a repaint, not just a new
        // message - combining both versions is what makes that happen
        // without a second, separate throttle. Not a real hash, just enough
        // separation that the two counters cannot cancel each other out at
        // any version either is realistically going to reach.
        var combinedVersion = (messagesVersion << 20) ^ _chatImages.Version;
        if (!_repaintThrottle.ShouldRepaint(combinedVersion, nowMs))
        {
            return;
        }

        var rendered = _renderer.Render(new ChatContent(snapshot));
        _surface.SetTexture(rendered.Rgba, rendered.Width, rendered.Height);
        _repaintThrottle.MarkPainted(combinedVersion, nowMs);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderer.Dispose();
        _chatImages.Dispose();
        _surface.Dispose();
    }
}
