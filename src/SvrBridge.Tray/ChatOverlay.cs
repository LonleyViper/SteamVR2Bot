using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// The chat window: one persistent <see cref="VrOverlaySurface"/>, a
/// <see cref="ChatRingBuffer"/> written by the event stream's consumption
/// loop, and a WPF renderer that repaints at most ~10 Hz per §B2 of the chat
/// plan.
/// <para>
/// Lives entirely inside the OpenVR worker process, ticked from the same
/// single thread that owns every other OpenVR call - see
/// <see cref="NotificationOverlay"/>, which this mirrors in every structural
/// respect except what it is anchored to and how its visibility works.
/// </para>
/// <para>
/// Visibility is gaze-scale, not show/hide, per §B1: the window is always
/// present at its anchor, small and faint, and grows large and opaque when
/// the wearer looks at it. That animation runs on every tick through
/// <see cref="VrOverlaySurface.SetAlpha"/> and
/// <see cref="VrOverlaySurface.SetWidthInMeters"/> alone - no texture
/// repaint is involved - and is entirely decoupled from the throttled text
/// repaint below: the two run on independent clocks by design.
/// </para>
/// <para>
/// Anchoring is wrist-relative by default (Phase 1/3's proven placement),
/// but is no longer hardcoded to the left controller: it is driven by an
/// <see cref="OverlayAnchorTracker"/> so a Streamer.bot <c>anchor</c> control
/// command can move it to the right controller or the headset instead - see
/// §B2 and §B3 of the Phase 4 plan.
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
    /// A 150 ms time constant for the gaze-scale ease: fast enough that
    /// looking at the window feels immediate, slow enough that the grow and
    /// shrink read as motion rather than a snap.
    /// </summary>
    private const float SmoothingTimeConstantMs = 150f;

    private readonly VrOverlaySurface _surface;
    private readonly IVrPanelRenderer<ChatContent> _renderer;
    private readonly ChatRingBuffer _messages = new();
    private readonly ChatRepaintThrottle _repaintThrottle = new();
    private readonly ChatImageCache _chatImages;
    private readonly OverlayAnchorTracker _anchorTracker;
    private readonly Action<string> _log;

    private ChatGazeHysteresis _gaze;

    // Opacity is the gazed-at (large) alpha ceiling; size is a multiplier on
    // both widths. The faint, not-gazed-at state scales proportionally from
    // the same SmallAlpha/LargeAlpha ratio the hardcoded constants always
    // had, rather than being its own setting - see SetOpacity/SetSizeScale.
    private double _opacity = LargeAlpha;
    private double _sizeScale = 1.0;

    private readonly OverlayTextureUploader _uploader;

    private bool _hidden;
    private bool _shown = true;
    private readonly GazeScaleAnimation _gazeAnimation = new(SmallWidthMeters, SmallAlpha);
    private long? _lastAnimateMs;
    private bool _disposed;

    private ChatOverlay(
        VrOverlaySurface surface,
        IOverlayTextureSource textureSource,
        IVrPanelRenderer<ChatContent> renderer,
        ChatImageCache chatImages,
        OverlayAnchorTracker anchorTracker,
        ChatGazeHysteresis gaze,
        Action<string> log)
    {
        _surface = surface;
        _uploader = new OverlayTextureUploader(
            new VrOverlaySurfaceUploadTarget(surface),
            textureSource,
            "The chat window",
            log)
        {
            // Default off: this is the first surface converted from
            // SetOverlayRaw to a persistent D3D11 texture, and the first
            // native GPU dependency in this app. Live-confirmed working, but
            // untested across the range of GPUs/drivers real users run -
            // opt in from the tray's developer menu. See
            // TrayApplicationContext's texture-path toggle.
            TexturePathEnabled = false
        };
        _renderer = renderer;
        _chatImages = chatImages;
        _anchorTracker = anchorTracker;
        _gaze = gaze;
        _log = log;
    }

    /// <summary>
    /// Creates the surface and shows it immediately, small and faint, at
    /// <paramref name="defaultAnchor"/>. Returns null when this SteamVR
    /// version has no overlay interface, which is not worth failing the
    /// worker over.
    /// </summary>
    public static ChatOverlay? TryCreate(
        OpenVrInput openVr,
        IOverlayTextureSource textureSource,
        OverlayAnchor defaultAnchor,
        double opacity,
        double sizeScale,
        GazeSensitivity gazeSensitivity,
        Action<string> log)
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
            surface.SetWidthInMeters(SmallWidthMeters * (float)sizeScale);
            surface.SetCurvature(0.05f);
            surface.SetAlpha(SmallAlpha * (float)(opacity / LargeAlpha));
            surface.SetSortOrder(0);
            renderer = new WpfChatRenderer(chatImages);
            var anchorTracker = new OverlayAnchorTracker("The chat window", defaultAnchor, log);
            anchorTracker.Tick(openVr, surface);
            var overlay = new ChatOverlay(
                surface,
                textureSource,
                renderer,
                chatImages,
                anchorTracker,
                ChatGazeHysteresis.Create(gazeSensitivity),
                log)
            {
                _opacity = opacity,
                _sizeScale = sizeScale
            };
            surface.Show();
            log($"The chat window is on. {DescribePlacement(defaultAnchor)}");
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
    /// Moves this surface to a different anchor - the effect of a
    /// Streamer.bot <c>anchor</c> control command, or a <c>reset</c> handing
    /// back the saved default. A no-op if it is already there.
    /// </summary>
    public void SetAnchorOverride(OverlayAnchor anchor) => _anchorTracker.SetAnchor(anchor);

    /// <summary>
    /// Forces this surface hidden regardless of gaze, or clears that
    /// override - the effect of a Streamer.bot <c>hide</c>/<c>show</c>
    /// control command.
    /// </summary>
    public void SetHidden(bool hidden) => _hidden = hidden;

    /// <summary>Empties the ring buffer - the effect of a <c>clear</c> control command.</summary>
    public void ClearMessages() => _messages.Clear();

    /// <summary>
    /// Sets the gazed-at alpha ceiling, 0.2-1.0 - a VR or desktop settings
    /// change. The faint, not-gazed-at alpha is derived from this, not set
    /// independently - see the field remarks above.
    /// </summary>
    public void SetOpacity(double opacity) => _opacity = Math.Clamp(opacity, 0.2, 1.0);

    /// <summary>Sets the width multiplier, 0.5-2.0 - a VR or desktop settings change.</summary>
    public void SetSizeScale(double sizeScale) => _sizeScale = Math.Clamp(sizeScale, 0.5, 2.0);

    /// <summary>
    /// Rebuilds the gaze detector with a new sensitivity - a VR or desktop
    /// settings change. Momentarily resets whether the wearer is currently
    /// judged to be gazing; a cosmetic reset only, corrected on the very next
    /// tick.
    /// </summary>
    public void SetGazeSensitivity(GazeSensitivity sensitivity) => _gaze = ChatGazeHysteresis.Create(sensitivity);

    /// <summary>
    /// Developer-only: switches this surface between the default
    /// <c>SetOverlayRaw</c> path and the persistent-texture path - see
    /// <see cref="OverlayTextureUploader.TexturePathEnabled"/>. Off by
    /// default because this is a first-of-its-kind GPU dependency in this
    /// app; toggled from the tray's developer menu, never persisted.
    /// </summary>
    public void SetTexturePathEnabled(bool enabled) => _uploader.TexturePathEnabled = enabled;

    /// <summary>
    /// Re-resolves the anchor, advances the gaze-scale animation, and
    /// repaints the texture when owed. Called once per OpenVR poll
    /// (~10 ms), like <see cref="VrTestOverlay.Tick"/> and
    /// <see cref="NotificationOverlay.Tick"/>.
    /// </summary>
    public void Tick(OpenVrInput openVr, long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        _anchorTracker.Tick(openVr, _surface);

        if (_hidden)
        {
            if (_shown)
            {
                _surface.Hide();
                _shown = false;
            }

            return;
        }

        if (!_shown)
        {
            _surface.Show();
            _shown = true;
        }

        AnimateGaze(openVr, nowMs);
        RepaintIfOwed(nowMs);
    }

    private void AnimateGaze(OpenVrInput openVr, long nowMs)
    {
        var deltaMs = _lastAnimateMs is { } last ? Math.Max(0L, nowMs - last) : 0L;
        _lastAnimateMs = nowMs;

        var isGazing = _gaze.Update(GazeDot(openVr.LatestMotion));
        var largeAlpha = (float)_opacity;
        var smallAlpha = largeAlpha * (SmallAlpha / LargeAlpha);
        var sizeScale = (float)_sizeScale;
        var targetWidth = (isGazing ? LargeWidthMeters : SmallWidthMeters) * sizeScale;
        var targetAlpha = isGazing ? largeAlpha : smallAlpha;

        // Exponential ease towards the target rather than an instant jump,
        // so the transition reads as smooth motion - required by the manual
        // "no flicker" check in the headset matrix. The ease only asymptotes
        // towards its target, so GazeScaleAnimation tracks convergence
        // explicitly - once converged, Advance is a no-op and these two
        // overlay calls are skipped rather than reissued forever at rest.
        _gazeAnimation.SetTarget(targetWidth, targetAlpha);
        if (!_gazeAnimation.Advance(deltaMs, SmoothingTimeConstantMs))
        {
            return;
        }

        _surface.SetWidthInMeters(_gazeAnimation.Width);
        _surface.SetAlpha(_gazeAnimation.Alpha);
    }

    /// <summary>
    /// Cosine of the angle between the head's forward vector and the
    /// head-to-window direction, both already expressed in
    /// <see cref="MotionSample"/>'s head-relative <see cref="BodyFrame"/>.
    /// <para>
    /// A head-anchored window (see <see cref="OverlayAnchor.HeadOffset"/>)
    /// sits directly ahead of the wearer by construction, so there is no
    /// separate "not looking at it" state worth detecting there - this
    /// always reads as fully gazed at, which is the degenerate case of the
    /// same formula rather than a special one. A controller-anchored window
    /// is treated as co-located with whichever hand it currently follows,
    /// close enough at wrist distance to matter.
    /// </para>
    /// <para>
    /// Because that frame's own forward axis is, by construction, its local
    /// +Z, the dot product collapses to the Z component of the normalised
    /// direction to the controller - no explicit forward vector is needed.
    /// <see cref="BodyFrame"/> is yaw-only (see its own remarks), so this
    /// reads as "roughly facing the anchor's direction" rather than a true
    /// eye-line check that accounts for head pitch; that is the same
    /// simplification the rest of this codebase's gesture recognition
    /// already makes with the same data.
    /// </para>
    /// </summary>
    private float GazeDot(MotionSample motion)
    {
        var anchor = _anchorTracker.Anchor;
        if (anchor.Mode == OverlayAnchorMode.Head)
        {
            return 1f;
        }

        var trackingFlag = anchor.Hand == OverlayAnchorHand.Left
            ? MotionTracking.Left
            : MotionTracking.Right;
        if ((motion.Tracking & trackingFlag) == 0)
        {
            return -1f;
        }

        var direction = anchor.Hand == OverlayAnchorHand.Left
            ? motion.LeftPosition
            : motion.RightPosition;
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
        _uploader.Upload(rendered.Rgba, rendered.Width, rendered.Height);
        _repaintThrottle.MarkPainted(combinedVersion, nowMs);
    }

    private static string DescribePlacement(OverlayAnchor anchor) =>
        anchor.Mode == OverlayAnchorMode.Head
            ? "It sits in front of you and grows on gaze."
            : $"It stays behind your {(anchor.Hand == OverlayAnchorHand.Left ? "left" : "right")} controller and grows on gaze.";

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
        // After the surface, not before: SteamVR holds a reference to the
        // texture until DestroyOverlay releases it.
        _uploader.Dispose();
    }
}
