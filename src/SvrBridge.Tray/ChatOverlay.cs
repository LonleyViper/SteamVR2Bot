using SvrBridge.Core;
using System.Numerics;

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
/// <para>
/// From Phase 5 the offset within that anchor is the wearer's to choose: grab
/// the move handle with the laser and the panel follows the pointing
/// controller until the trigger is released. Two things make that nearly free.
/// The offset is already a transform relative to the anchor device, so a drag
/// is a continuous recompute of a value that already exists and release is
/// simply "stop recomputing" - there is no world-to-local conversion step and
/// no <c>SetOverlayTransformAbsolute</c>, which true world-lock would need and
/// which stays deferred. Interaction is pre-armed only while a controller's
/// local laser ray intersects the panel, so SteamVR's global laser mode is not
/// enabled while the wearer is merely looking at chat. The grab is rigid: the
/// panel keeps whatever relationship it had to the grabbing controller, so
/// turning the wrist turns the panel - see
/// <see cref="OverlayDrag"/>.
/// </para>
/// </summary>
internal sealed class ChatOverlay : IDisposable
{
    private const string OverlayKey = "ie.lonelyviper.svrbridge.chat";

    private const uint HmdDeviceIndex = 0;

    /// <summary>
    /// Stands in for the head-to-panel distance while a pose is missing. Near
    /// enough that no distance rule fires on it, which is the point: a
    /// tracking dropout is not evidence about where the window is.
    /// </summary>
    private const float DefaultReadingDistanceMeters = 0.5f;

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

    /// <summary>
    /// Reported when a hand drag ends, so the worker can persist the new
    /// placement and clear any Streamer.bot anchor override the wearer has
    /// just overruled by hand. Never called during a drag: the panel moving
    /// live is what the wearer sees, and writing a settings file per poll
    /// would be absurd.
    /// </summary>
    private readonly Action<OverlayPlacement> _placementChanged;
    private readonly Action<GazeReference> _gazeReferenceChanged;

    private readonly ChatOverlayInput _input = new(ChatOverlayLayout.Buttons);
    private readonly List<OverlayMouseEvent> _laserEvents = [];

    private OverlayPlacement _placement;

    /// <summary>
    /// The grab in progress, or null. Held here rather than in
    /// <see cref="ChatOverlayInput"/> because it is made of tracked-device
    /// poses, which that class deliberately knows nothing about.
    /// </summary>
    private OverlayDrag? _drag;

    private uint? _dragPointerDeviceIndex;
    private bool _inputEnabled;

    /// <summary>
    /// Hides the window when it is turned away from the wearer or has been
    /// left too far off to read - the behaviour established VR overlay apps
    /// have, and which this window needed once its placement stopped being a
    /// fixed constant. Independent of the <c>hide</c> control command, which
    /// is the wearer's own explicit choice and is tracked separately.
    /// </summary>
    private readonly PanelVisibilityGate _visibility = new();

    private bool _autoVisible = true;

    /// <summary>
    /// Whether the window grows and brightens on gaze at all. Off leaves it at
    /// its configured size and opacity permanently.
    /// <para>
    /// Only the animation is switched off, never the gaze measurement itself:
    /// visibility and calibration still need the measurement, while laser
    /// input has its own controller-ray safety gate.
    /// </para>
    /// </summary>
    private bool _gazeScaleEnabled = true;

    /// <summary>
    /// Whether the panel becomes fully transparent outside the gaze cone. It
    /// is deliberately independent from <see cref="_gazeScaleEnabled"/> so
    /// a wearer may fade a fixed-size panel in without any size motion.
    /// </summary>
    private bool _gazeFadeEnabled;

    /// <summary>
    /// Whether the chat panel should disappear when its physical placement is
    /// turned away from the wearer or too far from their head. This is
    /// separate from gaze-scale: a wearer can keep a fixed-size chat panel
    /// while still asking it to get out of the way when their controller is
    /// lowered or rotated away.
    /// </summary>
    private bool _autoHideEnabled = true;

    /// <summary>
    /// False when this SteamVR version refused to set a mouse scale on a
    /// regular overlay - see <see cref="TryCreate"/>. The window then behaves
    /// exactly as it did before this phase: readable, gaze-scaled, and not
    /// movable by hand. The developer probe still works, since its question is
    /// about the input method rather than about hit-testing.
    /// </summary>
    private bool _laserInputAvailable = true;

    /// <summary>
    /// Reset every time input is switched on, so the log records the first
    /// laser event of each pointer entry rather than one line per move.
    /// <para>
    /// This exists because "the handle does not respond" is not a diagnosis.
    /// It has two very different causes - SteamVR sending this overlay nothing
    /// at all, or sending pointer moves but no button events - and they need
    /// opposite fixes. One log line at the top of each interaction tells them
    /// apart without another headset session spent guessing.
    /// </para>
    /// </summary>
    private bool _loggedFirstLaserEvent;

    private ChatGazeHysteresis _gaze;
    private GazeSensitivity _gazeSensitivity = GazeSensitivity.Normal;
    private GazeReference _gazeReference;
    private long? _gazeCalibrationStartedAtMs;
    private Vector3 _gazeCalibrationTotal;
    private int _gazeCalibrationSamples;

    private const long GazeCalibrationDurationMs = 3_000;

    // Opacity is the gazed-at (large) alpha ceiling; size is a multiplier on
    // both widths. The faint, not-gazed-at state scales proportionally from
    // the same SmallAlpha/LargeAlpha ratio the hardcoded constants always
    // had, rather than being its own setting - see SetOpacity/SetSizeScale.
    private double _opacity = LargeAlpha;
    private double _sizeScale = 1.0;

    private readonly OverlayTextureUploader _uploader;

    /// <summary>
    /// How long the laser-input probe stays on before turning itself off.
    /// <para>
    /// Bounded rather than a plain on/off switch because the thing being
    /// probed is whether this overlay steals the trigger from a running VR
    /// game. If it does, the wearer is mid-game with broken input and the tray
    /// menu is on a monitor they cannot see - so the probe has to end on its
    /// own. Long enough to look at the window and pull the trigger a few
    /// times, short enough that a bad result is over quickly.
    /// </para>
    /// </summary>
    private const long InputProbeDurationMs = 60_000;

    private long? _inputProbeExpiresAtMs;

    private bool _hidden;
    private bool _shown = true;
    private readonly GazeScaleAnimation _gazeAnimation;
    private long? _lastAnimateMs;
    private bool _disposed;

    private ChatOverlay(
        VrOverlaySurface surface,
        IOverlayTextureSource textureSource,
        IVrPanelRenderer<ChatContent> renderer,
        ChatImageCache chatImages,
        OverlayAnchorTracker anchorTracker,
        ChatGazeHysteresis gaze,
        OverlayPlacement placement,
        float initialWidth,
        float initialAlpha,
        Action<OverlayPlacement> placementChanged,
        Action<GazeReference> gazeReferenceChanged,
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
        _placement = placement;
        _gazeAnimation = new GazeScaleAnimation(initialWidth, initialAlpha);
        _placementChanged = placementChanged;
        _gazeReferenceChanged = gazeReferenceChanged;
        _log = log;
    }

    /// <summary>
    /// Creates the surface and shows it immediately at
    /// <paramref name="defaultAnchor"/>. Its initial size and alpha honour
    /// the gaze settings so fade-on-gaze does not flash a visible panel before
    /// the first worker tick. Returns null when this SteamVR version has no
    /// overlay interface, which is not worth failing the worker over.
    /// </summary>
    public static ChatOverlay? TryCreate(
        OpenVrInput openVr,
        IOverlayTextureSource textureSource,
        OverlayAnchor defaultAnchor,
        OverlayPlacement placement,
        double opacity,
        double sizeScale,
        GazeSensitivity gazeSensitivity,
        bool gazeScaleEnabled,
        bool gazeFadeEnabled,
        bool autoHideEnabled,
        GazeReference gazeReference,
        Action<OverlayPlacement> placementChanged,
        Action<GazeReference> gazeReferenceChanged,
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
            var resolvedOpacity = (float)Math.Clamp(opacity, 0.2, 1.0);
            var resolvedSizeScale = (float)Math.Clamp(sizeScale, 0.5, 2.0);
            var initialGrown = !gazeScaleEnabled;
            var initialWidth = (initialGrown ? LargeWidthMeters : SmallWidthMeters) * resolvedSizeScale;
            var initialAlpha = gazeFadeEnabled
                ? 0f
                : initialGrown
                    ? resolvedOpacity
                    : resolvedOpacity * (SmallAlpha / LargeAlpha);
            surface.SetWidthInMeters(initialWidth);
            surface.SetCurvature(0.05f);
            surface.SetAlpha(initialAlpha);
            surface.SetSortOrder(0);
            // Set once, at creation, and in the panel's own pixels: it is what
            // makes a laser event's x/y directly comparable to the rectangles
            // ChatOverlayLayout hands both the renderer and the hit test.
            // Input itself stays off until the wearer looks at the window -
            // see UpdateLaserInput.
            //
            // Non-fatal on purpose. SetOverlayMouseScale has only ever been
            // called on the dashboard overlay in this app, and every previous
            // first use of an overlay call against a regular overlay has been
            // worth being careful about - the SetOverlayTexture finding is the
            // standing example. A window that reads chat but cannot be
            // dragged is a far better failure than no window at all.
            var laserInput = true;
            try
            {
                surface.SetMouseScale(ChatOverlayLayout.PanelWidth, ChatOverlayLayout.PanelHeight);
            }
            catch (Exception exception)
            {
                laserInput = false;
                log(
                    "The chat window cannot be moved by hand on this SteamVR version: "
                    + $"{exception.Message} Everything else about it works.");
            }

            renderer = new WpfChatRenderer(chatImages);
            var anchorTracker = new OverlayAnchorTracker("The chat window", defaultAnchor, log, placement);
            anchorTracker.Tick(openVr, surface);
            var overlay = new ChatOverlay(
                surface,
                textureSource,
                renderer,
                chatImages,
                anchorTracker,
                ChatGazeHysteresis.Create(gazeSensitivity),
                placement,
                initialWidth,
                initialAlpha,
                placementChanged,
                gazeReferenceChanged,
                log)
            {
                _opacity = opacity,
                _sizeScale = sizeScale,
                _gazeSensitivity = gazeSensitivity,
                _gazeScaleEnabled = gazeScaleEnabled,
                _gazeFadeEnabled = gazeFadeEnabled,
                _autoHideEnabled = autoHideEnabled,
                _gazeReference = gazeReference.IsUsable ? gazeReference : GazeReference.None,
                _laserInputAvailable = laserInput
            };
            // SteamVR shows an overlay without a texture as a blank surface
            // (and some runtimes do not visibly present it until their first
            // texture upload). Seed and paint an app-local line before Show,
            // so an enabled chat window is immediately discoverable and can
            // be used as the target for gaze calibration.
            overlay.Enqueue(CreateStartupMessage());
            overlay.RepaintIfOwed(Environment.TickCount64);
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
    public void SetAnchorOverride(OverlayAnchor anchor)
    {
        if (_anchorTracker.Anchor.Mode != anchor.Mode)
        {
            // A drag is only meaningful against the anchor mode it started on
            // - its start pointer is measured relative to that device, and its
            // offset is saved under that mode's key. Something moving the
            // window out from under a drag in progress abandons it rather than
            // writing a delta into the wrong mode's offset.
            _drag = null;
            _input.CancelDrag();
        }

        _anchorTracker.SetAnchor(anchor);
    }

    /// <summary>
    /// Replaces the saved offsets - a reset from the VR settings page, or a
    /// settings change arriving from the desktop. Takes effect on the next
    /// tick, and abandons any drag in progress: the wearer asked for a
    /// specific placement, so a half-finished drag must not overwrite it.
    /// </summary>
    public void SetPlacement(OverlayPlacement placement)
    {
        if (_disposed)
        {
            return;
        }

        _drag = null;
        _input.CancelDrag();
        _placement = placement;
        _anchorTracker.SetPlacement(placement);
        // The whole point of a reset is to get a lost window back, so it must
        // not stay auto-hidden on the strength of where it used to be. The
        // gate re-evaluates from the new placement on the next tick.
        _visibility.ForceVisible();
    }

    /// <summary>Replaces the optional wearer-calibrated centre of the gaze cone.</summary>
    public void SetGazeReference(GazeReference reference) =>
        _gazeReference = reference.IsUsable ? reference : GazeReference.None;

    /// <summary>
    /// Starts a short calibration while the wearer deliberately looks at chat.
    /// It is deliberately a timed average rather than one pose, so ordinary
    /// headset tracking noise cannot choose the fade centre.
    /// </summary>
    public bool StartGazeCalibration(long nowMs)
    {
        if (_disposed)
        {
            return false;
        }

        _gazeCalibrationStartedAtMs = nowMs;
        _gazeCalibrationTotal = Vector3.Zero;
        _gazeCalibrationSamples = 0;
        _log("Gaze calibration started. Look directly at the chat window for 3 seconds.");
        return true;
    }

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
    public void SetGazeSensitivity(GazeSensitivity sensitivity)
    {
        _gazeSensitivity = sensitivity;
        _gaze = ChatGazeHysteresis.Create(sensitivity);
    }

    /// <summary>
    /// Turns the grow-and-brighten-on-gaze animation on or off - a VR or
    /// desktop settings change. See <see cref="_gazeScaleEnabled"/> for why
    /// this does not also turn off the gaze measurement.
    /// </summary>
    public void SetGazeScaleEnabled(bool enabled) => _gazeScaleEnabled = enabled;

    /// <summary>Turns the gaze-controlled alpha fade on or off without changing panel size.</summary>
    public void SetGazeFadeEnabled(bool enabled) => _gazeFadeEnabled = enabled;

    /// <summary>
    /// Changes the automatic angle/distance visibility gate live. Disabling
    /// it immediately restores a panel previously hidden by that gate.
    /// </summary>
    public void SetAutoHideEnabled(bool enabled)
    {
        _autoHideEnabled = enabled;
        if (!enabled)
        {
            _visibility.ForceVisible();
        }
    }

    /// <summary>
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

        // Before the hidden/shown branches below, which both return early: a
        // probe that is running must end on time whatever else the window is
        // doing, including being hidden by a control command.
        ExpireInputProbe(nowMs);

        // One pose pair, three answers - see PanelView. Measured after the
        // anchor tick, so a drag applied last tick is reflected in where the
        // panel actually is now rather than where it was.
        var view = MeasurePanel(openVr, out var currentGazeDirection);

        // A drag overrules both gates. The wearer has hold of the window, so
        // it must not shrink, must not hide, and must keep accepting the input
        // that will eventually release it - see AnimateGaze and the reconcile
        // in UpdateLaserInput for what happens when it does not.
        var dragging = _drag is not null;
        var calibratingGaze = _gazeCalibrationStartedAtMs is not null;
        if (dragging || calibratingGaze)
        {
            _visibility.ForceVisible();
        }

        // Calibration is an explicit request to look at this surface. Let it
        // temporarily overrule both automatic visibility and a transient
        // Streamer.bot hide command; otherwise Tick returns below, the timer
        // never advances, and the dashboard is stranded on “Calibrating…”.
        var autoVisible = dragging
                          || calibratingGaze
                          || !_autoHideEnabled
                          || _visibility.Update(view.FacingDot, view.DistanceMeters);
        LogVisibilityChange(autoVisible, view);

        if (!ShouldShowSurface(_hidden, autoVisible, calibratingGaze))
        {
            if (_shown)
            {
                _surface.Hide();
                _shown = false;
            }

            // A window nobody can see accepts nothing. Said here rather than
            // left to the gaze update below, which this branch returns before
            // reaching - a "hide" control command arriving mid-drag would
            // otherwise leave an invisible panel still following the wearer's
            // hand, and this branch also returns before the reconcile in
            // UpdateLaserInput that would otherwise catch it.
            _input.SetLaserInputArmed(false);
            if (_drag is not null)
            {
                CancelDrag("the window was hidden");
            }

            if (_inputProbeExpiresAtMs is null)
            {
                SetInputEnabled(false);
            }

            return;
        }

        if (!_shown)
        {
            _surface.Show();
            _shown = true;
        }

        AnimateGaze(view, currentGazeDirection, nowMs);
        // After the gaze update and before the repaint: the controller ray,
        // not gaze enlargement, decides whether SteamVR may send chat input.
        UpdateLaserInput(openVr);
        RepaintIfOwed(nowMs);
    }

    /// <summary>
    /// Calibration must reach the sampling/timer path even if normal chat
    /// visibility would return early. Kept pure so the hidden-surface
    /// regression is deterministic without an OpenVR session.
    /// </summary>
    internal static bool ShouldShowSurface(bool hidden, bool autoVisible, bool calibratingGaze) =>
        calibratingGaze || (!hidden && autoVisible);

    /// <summary>
    /// The local first line is deliberately a normal chat payload so the
    /// usual renderer, texture uploader and repaint throttle exercise the
    /// exact path real messages use.
    /// </summary>
    internal static StreamerBotEventPayload CreateStartupMessage() => new()
    {
        Target = StreamerBotEventTarget.Chat,
        User = "SteamVR2Bot",
        Colour = "#8EC5FF",
        Text = "Chat window ready."
    };

    /// <summary>
    /// Where the panel stands relative to the wearer's head, from live poses.
    /// <para>
    /// Falls back to "directly in front, facing me, arm's length" when a pose
    /// is missing rather than to zeros. A tracking dropout is not evidence
    /// that the window should be hidden or shrunk, and the neutral answer
    /// leaves whatever state the gates were already in undisturbed.
    /// </para>
    /// </summary>
    private PanelView MeasurePanel(OpenVrInput openVr, out GazeReference gazeDirection)
    {
        if (_anchorTracker.BoundDeviceIndex is not { } anchorIndex
            || !openVr.TryGetDevicePose(HmdDeviceIndex, out var headPose)
            || !openVr.TryGetDevicePose(anchorIndex, out var anchorPose))
        {
            gazeDirection = GazeReference.None;
            return new PanelView(1f, 1f, DefaultReadingDistanceMeters);
        }

        var panelPose = anchorPose * _placement.ToTransform(_anchorTracker.Anchor.Mode);
        _ = GazeReference.TryMeasure(headPose, panelPose, out gazeDirection);
        return PanelView.From(headPose, panelPose);
    }

    /// <summary>
    /// Says why the window disappeared, once per transition. A panel that
    /// hides itself is indistinguishable from a broken one without this, and
    /// the distance case in particular is unrecoverable by pointing at it -
    /// the wearer needs to know the reset control is what they want.
    /// </summary>
    private void LogVisibilityChange(bool visible, PanelView view)
    {
        if (_autoVisible == visible)
        {
            return;
        }

        _autoVisible = visible;
        if (visible)
        {
            _log("The chat window is back in view.");
            return;
        }

        _log(
            view.DistanceMeters > 2f
                ? $"The chat window hid itself: it is {view.DistanceMeters:0.0} m away. "
                  + "Reset its position from the VR settings tab to bring it back."
                : "The chat window hid itself: it is turned away from you.");
    }

    private void AnimateGaze(PanelView view, GazeReference currentGazeDirection, long nowMs)
    {
        var deltaMs = _lastAnimateMs is { } last ? Math.Max(0L, nowMs - last) : 0L;
        _lastAnimateMs = nowMs;

        // Updated every tick regardless, so the hysteresis state stays current
        // and the window resolves to the right size the moment a drag ends.
        AdvanceGazeCalibration(currentGazeDirection, nowMs);
        var gazeDot = _gazeReference.IsUsable && currentGazeDirection.IsUsable
            ? _gazeReference.Dot(currentGazeDirection)
            : view.GazeDot;
        var isGazing = _gaze.Update(gazeDot);

        // A drag holds the window open whatever gaze says.
        //
        // Moving a panel can carry it out of either the default or calibrated
        // gaze cone while it is being dragged. The window would then shrink,
        // drop input, and lose the release that ends the drag. Holding it open
        // for the duration is simply what the interaction means: the
        // wearer has hold of the thing, so they are unambiguously interacting
        // with it, whichever way they happen to be looking.
        isGazing |= _drag is not null || _gazeCalibrationStartedAtMs is not null;
        // With the animation switched off the window sits at its full size and
        // opacity and stays there. Deliberately the large state rather than
        // some average: a window that never grows has to be readable as it is.
        var grown = isGazing || !_gazeScaleEnabled;
        var largeAlpha = (float)_opacity;
        var smallAlpha = largeAlpha * (SmallAlpha / LargeAlpha);
        var sizeScale = (float)_sizeScale;
        var targetWidth = (grown ? LargeWidthMeters : SmallWidthMeters) * sizeScale;
        var targetAlpha = ResolveGazeAlpha(isGazing, grown, _gazeFadeEnabled, smallAlpha, largeAlpha);

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
    /// Computes the alpha target independently from the size target: when
    /// fading is enabled, the panel is transparent outside gaze even if size
    /// growth is disabled and its width therefore remains large.
    /// </summary>
    internal static float ResolveGazeAlpha(
        bool isGazing,
        bool grown,
        bool gazeFadeEnabled,
        float smallAlpha,
        float largeAlpha) =>
        gazeFadeEnabled && !isGazing ? 0f : grown ? largeAlpha : smallAlpha;

    private void AdvanceGazeCalibration(GazeReference currentGazeDirection, long nowMs)
    {
        if (_gazeCalibrationStartedAtMs is not { } startedAt)
        {
            return;
        }

        if (currentGazeDirection.IsUsable)
        {
            _gazeCalibrationTotal += GazeReference.ToVector3(currentGazeDirection);
            _gazeCalibrationSamples++;
        }

        if (nowMs - startedAt < GazeCalibrationDurationMs)
        {
            return;
        }

        _gazeCalibrationStartedAtMs = null;
        if (!GazeReference.TryAverage(_gazeCalibrationTotal, _gazeCalibrationSamples, out var reference))
        {
            _log("Gaze calibration could not read a stable headset pose. Please try again.");
            // The callback also returns the settings UI from its transient
            // "Calibrating…" state. Re-reporting the unchanged value is
            // harmless and avoids leaving that UI state stranded on a
            // tracking dropout.
            _gazeReferenceChanged(_gazeReference);
            return;
        }

        _gazeReference = reference;
        _gaze = ChatGazeHysteresis.Create(_gazeSensitivity);
        _gazeReferenceChanged(reference);
        _log("Gaze calibration saved. Chat now fades relative to where you looked during calibration.");
    }

    /// <summary>
    /// Reconciles whether this overlay accepts the laser, drains whatever it
    /// received, and advances a drag in progress. Input is on only while a
    /// controller laser ray already intersects this panel, or for the
    /// time-bounded developer probe.
    /// <para>
    /// The developer probe overrides the gate while it runs, which is the
    /// whole point of it: it exists to answer what an always-on input method
    /// does to a running game.
    /// </para>
    /// <para>
    /// SteamVR produces its own overlay hover events only after its global
    /// interactive flag is enabled, so the ray test intentionally happens
    /// before those events exist. It uses the same live controller and panel
    /// transforms SteamVR uses for placement; a panel miss leaves the game
    /// completely outside overlay input mode.
    /// </para>
    /// </summary>
    private void UpdateLaserInput(OpenVrInput openVr)
    {
        var wantInput = ShouldEnableLaserInput(
            _drag is not null || IsControllerPointerOverPanel(openVr),
            _laserInputAvailable,
            _inputProbeExpiresAtMs is not null);
        SetInputEnabled(wantInput);
        _input.SetLaserInputArmed(wantInput);

        // Drained even when input is off, so a queue that filled just before
        // the pointer left cannot be replayed against the panel later. The router
        // discards them; this just stops them accumulating.
        _surface.PollMouseEvents(_laserEvents);
        if (_laserEvents.Count > 0)
        {
            HandleLaserEvents(openVr);
        }

        // Reconciled rather than enumerated, deliberately. A drag can only end
        // properly through a release event, and a release event can only
        // arrive while input is on - so anything that turns input off while
        // the wearer is still holding on strands the drag, and the panel
        // follows their hand for ever with no way to let go. That is not a
        // hypothetical: it is what the first build of this did. Rather than
        // trying to remember every path that can withdraw the hold, this
        // checks the one invariant that matters - a live drag requires a live
        // hold - on every tick.
        if (_drag is not null && !_input.IsHolding)
        {
            CancelDrag("the window stopped accepting input");
        }

        AdvanceDrag(openVr);
    }

    internal static bool ShouldEnableLaserInput(
        bool pointerOverPanel,
        bool laserInputAvailable,
        bool inputProbeRunning) =>
        laserInputAvailable && (pointerOverPanel || inputProbeRunning);

    /// <summary>
    /// Tests both controller rays against the panel's current, anchor-relative
    /// transform. The width is the current animated width, matching the size
    /// SteamVR is displaying this tick rather than either animation endpoint.
    /// </summary>
    private bool IsControllerPointerOverPanel(OpenVrInput openVr)
    {
        if (_anchorTracker.BoundDeviceIndex is not { } anchorIndex
            || !openVr.TryGetDevicePose(anchorIndex, out var anchorPose))
        {
            return false;
        }

        var panelPose = anchorPose * _placement.ToTransform(_anchorTracker.Anchor.Mode);
        var width = _gazeAnimation.Width;
        var height = width * ChatOverlayLayout.PanelHeight / ChatOverlayLayout.PanelWidth;
        return ControllerRayHitsPanel(ControllerHand.Left)
               || ControllerRayHitsPanel(ControllerHand.Right);

        bool ControllerRayHitsPanel(ControllerHand hand) =>
            openVr.TryGetControllerDeviceIndex(hand) is { } deviceIndex
            && openVr.TryGetDevicePose(deviceIndex, out var controllerPose)
            && OverlayRayHitTest.IntersectsPanel(controllerPose, panelPose, width, height);
    }

    private void HandleLaserEvents(OpenVrInput openVr)
    {
        var anchor = _anchorTracker.Anchor;

        foreach (var laserEvent in _laserEvents)
        {
            // SteamVR reports overlay mouse coordinates with the origin at the
            // bottom-left; every rectangle in ChatOverlayLayout is top-left,
            // like the texture. The dashboard applies the same flip - see
            // OpenVrInput.TryGetDashboardInteraction.
            var flipped = laserEvent with { Y = ChatOverlayLayout.PanelHeight - laserEvent.Y };
            LogLaserEvent(flipped, laserEvent.DeviceIndex);

            switch (_input.Handle(flipped))
            {
                case ChatInputOutcome.DragBegan:
                    BeginDrag(openVr, anchor, laserEvent.DeviceIndex);
                    break;
                case ChatInputOutcome.DragEnded:
                    if (_drag is not null)
                    {
                        _drag = null;
                        _placementChanged(_placement);
                        _log("The chat window's new position was saved.");
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Records enough to tell "SteamVR sent this overlay nothing" apart from
    /// "SteamVR sent moves but no button" without a second headset session -
    /// the two causes of "the handle does not respond", which need opposite
    /// fixes and which the first Phase 5 headset run could not distinguish.
    /// <para>
    /// Button events are logged every time - a trigger pull is a rare,
    /// deliberate act and each one is worth a line. Pointer moves are logged
    /// once per interaction, because they arrive faster than the panel
    /// repaints and would drown the log otherwise.
    /// </para>
    /// </summary>
    private void LogLaserEvent(in OverlayMouseEvent laserEvent, uint deviceIndex)
    {
        var isButton = laserEvent.Kind is OverlayMouseEventKind.ButtonDown
            or OverlayMouseEventKind.ButtonUp;
        if (!isButton && _loggedFirstLaserEvent)
        {
            return;
        }

        _loggedFirstLaserEvent = true;
        var hit = ChatOverlayLayout.IndexAt(ChatOverlayLayout.Buttons, laserEvent.X, laserEvent.Y)
                  == ChatOverlayLayout.MoveHandleIndex
            ? "the move handle"
            : "no control";
        _log(
            $"The chat window received a laser {laserEvent.Kind} at "
            + $"{laserEvent.X:0}, {laserEvent.Y:0} over {hit} (device {deviceIndex}).");
    }

    /// <summary>
    /// Takes hold of the panel: records where it currently sits relative to
    /// the grabbing controller, and nothing else. Every later tick restores
    /// that one relationship against wherever the two devices have moved to,
    /// which is what makes the grab carry orientation as well as position.
    /// </summary>
    private void BeginDrag(OpenVrInput openVr, OverlayAnchor anchor, uint eventDeviceIndex)
    {
        _dragPointerDeviceIndex = ResolvePointerDeviceIndex(openVr, anchor, eventDeviceIndex);
        if (!TryGetGrabPoses(openVr, out var anchorPose, out var pointerPose))
        {
            // Nothing is tracking well enough to define a grab. Refusing is
            // the only safe answer: a grab built on a bad pose would snap the
            // panel somewhere arbitrary the instant tracking recovered.
            _input.CancelDrag();
            _log("The chat window could not be grabbed: a controller is not tracking.");
            return;
        }

        _drag = OverlayDrag.Begin(anchor.Mode, _placement.For(anchor.Mode), anchorPose, pointerPose);
        _log("The chat window is being moved.");
    }

    /// <summary>
    /// Recomputes and applies the panel's offset from both devices' current
    /// poses. Runs per tick rather than per mouse-move because overlay mouse
    /// events carry 2D panel coordinates, not a world-space ray - they can say
    /// the wearer is holding on, but not where they have moved to.
    /// </summary>
    private void AdvanceDrag(OpenVrInput openVr)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var anchor = _anchorTracker.Anchor;
        if (anchor.Mode != drag.Mode)
        {
            // Something moved the window to a different anchor mid-drag. Its
            // grab was measured against the old anchor and its result belongs
            // under the old mode's key, so it cannot be carried across.
            CancelDrag("the window changed anchor");
            return;
        }

        if (!TryGetGrabPoses(openVr, out var anchorPose, out var pointerPose))
        {
            // Tracking dropped out mid-drag. Abandoning leaves the panel where
            // it had already been dragged to, which is what the wearer can
            // see; continuing from a stale pose would jump it somewhere they
            // did not choose the moment tracking returned.
            CancelDrag("a controller lost tracking");
            return;
        }

        var offset = drag.OffsetAt(anchorPose, pointerPose);
        if (!offset.IsUsable())
        {
            CancelDrag("the controller pose was unusable");
            return;
        }

        _placement = _placement.With(
            anchor.Mode,
            offset.WithTranslationClamped(OverlayPlacement.LimitMeters));
        _anchorTracker.SetPlacement(_placement);
        // Immediately rather than next tick: at a 10 ms poll this is the
        // difference between a panel that tracks the hand and one that lags
        // behind it by a visible frame.
        _anchorTracker.Tick(openVr, _surface);
    }

    private void CancelDrag(string reason)
    {
        _drag = null;
        _input.CancelDrag();
        _log($"The chat window stopped moving: {reason}.");
    }

    /// <summary>
    /// Both poses a grab needs: the device the panel hangs off, and the device
    /// doing the pointing. Either being untracked makes the grab undefined,
    /// so this is all-or-nothing.
    /// </summary>
    private bool TryGetGrabPoses(
        OpenVrInput openVr,
        out VrOverlayTransform anchorPose,
        out VrOverlayTransform pointerPose)
    {
        anchorPose = VrOverlayTransform.Identity;
        pointerPose = VrOverlayTransform.Identity;
        return _anchorTracker.BoundDeviceIndex is { } anchorIndex
               && _dragPointerDeviceIndex is { } pointerIndex
               && openVr.TryGetDevicePose(anchorIndex, out anchorPose)
               && openVr.TryGetDevicePose(pointerIndex, out pointerPose);
    }

    /// <summary>
    /// Which tracked device is doing the pointing. SteamVR names it on the
    /// event itself; the fallback for an invalid index is the hand opposite
    /// the anchor, since a wrist panel is reached with the other hand.
    /// </summary>
    private static uint? ResolvePointerDeviceIndex(
        OpenVrInput openVr,
        OverlayAnchor anchor,
        uint eventDeviceIndex)
    {
        var left = openVr.TryGetControllerDeviceIndex(ControllerHand.Left);
        var right = openVr.TryGetControllerDeviceIndex(ControllerHand.Right);
        if (eventDeviceIndex == left || eventDeviceIndex == right)
        {
            return eventDeviceIndex;
        }

        return anchor.Mode == OverlayAnchorMode.Controller && anchor.Hand == OverlayAnchorHand.Right
            ? left
            : right;
    }

    /// <summary>
    /// The single writer of both halves of "this overlay is interactive", so
    /// the pointer gate and the developer probe cannot fight over either.
    /// Idempotent, because this is called on every poll.
    /// <para>
    /// Both halves, because one alone does nothing.
    /// <c>SetOverlayInputMethod</c> declares that this overlay would accept
    /// mouse events; <c>VROverlayFlags_MakeOverlaysInteractiveIfVisible</c> is
    /// what makes SteamVR generate any outside the dashboard. Phase 5's first
    /// headset run set only the first and the move handle never received a
    /// click - see
    /// <see cref="VrOverlaySurface.SetMakesOverlaysInteractive"/>.
    /// </para>
    /// <para>
    /// Order matters on the way down. The flag is what puts SteamVR into
    /// system-wide laser mouse mode, so it comes off first and goes on last -
    /// the app is never in a state where the laser is live but this overlay
    /// has stopped accepting what it delivers.
    /// </para>
    /// </summary>
    private void SetInputEnabled(bool enabled)
    {
        if (_inputEnabled == enabled)
        {
            return;
        }

        _inputEnabled = enabled;
        try
        {
            if (enabled)
            {
                _surface.SetAcceptsLaserInput(true);
                _surface.SetMakesOverlaysInteractive(true);
                _loggedFirstLaserEvent = false;
            }
            else
            {
                _surface.SetMakesOverlaysInteractive(false);
                _surface.SetAcceptsLaserInput(false);
            }
        }
        catch (Exception exception)
        {
            // Same reasoning as the mouse-scale guard in TryCreate: the flag
            // is a first use against a regular overlay, and this runs inside a
            // Tick whose caller answers an exception by turning chat off
            // entirely. A window that cannot be dragged beats no window.
            _laserInputAvailable = false;
            _inputEnabled = false;
            _log(
                "The chat window cannot be made interactive on this SteamVR version: "
                + $"{exception.Message} Everything else about it works.");
        }
    }

    /// <summary>
    /// Turns the SteamVR laser pointer on for the chat window for
    /// <see cref="InputProbeDurationMs"/>, then off again by itself.
    /// <para>
    /// The one question the next phase's design depends on: pointing a
    /// controller at this window and pulling the trigger while a VR game is
    /// running - does the game still get the trigger, or does the overlay
    /// swallow it? Developer-only, never persisted, off at every launch, and
    /// self-limiting because a positive result means broken game input.
    /// </para>
    /// </summary>
    public void StartInputProbe(long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        _inputProbeExpiresAtMs = nowMs + InputProbeDurationMs;
        SetInputEnabled(true);
        _input.SetLaserInputArmed(true);
        _log(
            $"Laser input probe ON for the chat window for {InputProbeDurationMs / 1000} seconds. "
            + "Point a controller at it in a running game and pull the trigger: does the game "
            + "still get it? It turns itself off.");
    }

    /// <summary>
    /// Ends the probe, whether because the timer ran out or because it was
    /// switched off. Safe to call when no probe is running.
    /// </summary>
    public void StopInputProbe(bool expired = false)
    {
        if (_disposed || _inputProbeExpiresAtMs is null)
        {
            return;
        }

        _inputProbeExpiresAtMs = null;
        SetInputEnabled(false);
        _input.SetLaserInputArmed(false);
        _log(expired
            ? "Laser input probe OFF for the chat window - the timer ran out, as designed."
            : "Laser input probe OFF for the chat window.");
    }

    private void ExpireInputProbe(long nowMs)
    {
        if (_inputProbeExpiresAtMs is { } expiresAt && nowMs >= expiresAt)
        {
            StopInputProbe(expired: true);
        }
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

        // A hover change is its own reason to repaint, deliberately not folded
        // into the message-version throttle above. Sweeping the laser across
        // the panel must light the handle up promptly or the control feels
        // dead; waiting out the 10 Hz message throttle would put up to 100 ms
        // between pointing at it and seeing it respond. It is affordable
        // precisely because it fires on transitions between rectangles, not on
        // pointer movement - see ChatOverlayInput.
        var hoverChanged = _input.TakeRepaintOwed();
        if (!hoverChanged && !_repaintThrottle.ShouldRepaint(combinedVersion, nowMs))
        {
            return;
        }

        var rendered = _renderer.Render(new ChatContent(snapshot, _input.HoveredIndex));
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

        // Before _disposed is set, so it actually runs: leaving the laser
        // pointer enabled on a surface that is about to be destroyed is
        // harmless, but leaving it on across a worker restart that reuses the
        // key would not be, and this costs nothing. Unconditional here, unlike
        // StopInputProbe, because nothing is going to tick this again.
        try
        {
            _inputProbeExpiresAtMs = null;
            SetInputEnabled(false);
        }
        catch (Exception)
        {
            // SteamVR may already have gone away, which is the common case
            // during shutdown - the same reason VrOverlaySurface.Dispose
            // swallows DestroyOverlay. Faulting here would mask whatever is
            // actually tearing the session down.
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
