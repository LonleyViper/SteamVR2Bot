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
/// <para>
/// Since Phase 7, the offset within that anchor is user-owned too, exactly
/// like the chat window's - see <see cref="SetPositioningEnabled"/>. Normal
/// operation never enables the laser on this surface (§B1's hard constraint);
/// the positioning frame is the one deliberate exception, and it is never on
/// unless the wearer explicitly turned it on from the Notifications tab.
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
    private readonly OverlayTextureUploader _uploader;
    private readonly Action<OverlayPlacement> _placementChanged;
    private readonly Action<string> _log;
    private readonly NotificationTransitionAnimator _transitionAnimator = new();
    private readonly List<OverlayMouseEvent> _laserEvents = [];

    // Opacity scales the peak alpha the fade curve holds at; size multiplies
    // WidthInMeters. Applied live via SetOpacity/SetSizeScale so a VR or
    // desktop settings change is visible on the very next notification.
    private double _opacity = 1.0;
    private double _sizeScale = 1.0;

    // §B3/§B4/§B5 appearance - see NotificationAppearanceSettings. Defaults
    // match this class's own pre-Phase-7 hardcoded values, so a settings file
    // written before this feature existed looks exactly as it always did.
    private string _backgroundHex = "";
    private string _textHex = "";
    private string _accentHex = "";
    private int _defaultDurationMs = StreamerBotEventPayload.DefaultDurationMs;
    private NotificationTransition _transition = NotificationTransition.Fade;
    private NotificationSlideEdge _slideEdge = NotificationSlideEdge.Bottom;
    private string _templatePath = "";
    private double _backgroundOpacity = NotificationAppearanceSettings.DefaultBackgroundOpacity;
    private double _cornerRadiusPixels;

    private OverlayPlacement _placement;

    /// <summary>
    /// The grab in progress while <see cref="_positioning"/>, or null - the
    /// same rigid 6-DOF mechanism <c>ChatOverlay</c> uses, built from the same
    /// Core primitives (<see cref="OverlayDrag"/>,
    /// <see cref="VrOverlayTransform.InverseRigid"/>) rather than a second
    /// implementation of them. Kept as a separate copy of the orchestration
    /// glue rather than a shared type with <c>ChatOverlay</c> deliberately -
    /// see this class's own remarks and the Phase 7 plan's B1: the chat
    /// window's grab path is hardware-validated and is not worth touching to
    /// share a few dozen lines of wiring.
    /// </summary>
    private OverlayDrag? _drag;

    private uint? _dragPointerDeviceIndex;
    private bool _holding;
    private bool _positioning;
    private bool _inputEnabled;

    /// <summary>
    /// False when this SteamVR version refused to set a mouse scale or the
    /// interactive flag on this overlay - mirrors <c>ChatOverlay</c>'s own
    /// guard. The positioning frame then shows but cannot be dragged; every
    /// other use of this surface is unaffected either way.
    /// </summary>
    private bool _laserInputAvailable = true;

    private bool _hidden;
    private bool _shown;
    private bool _disposed;

    private NotificationOverlay(
        VrOverlaySurface surface,
        IOverlayTextureSource textureSource,
        IVrPanelRenderer<NotificationContent> renderer,
        NotificationPlayer player,
        OverlayAnchorTracker anchorTracker,
        OverlayPlacement placement,
        Action<OverlayPlacement> placementChanged,
        Action<string> log)
    {
        _surface = surface;
        _uploader = new OverlayTextureUploader(
            new VrOverlaySurfaceUploadTarget(surface),
            textureSource,
            "Notifications",
            log)
        {
            // Default off - see ChatOverlay's identical gate for why.
            TexturePathEnabled = false
        };
        _renderer = renderer;
        _player = player;
        _anchorTracker = anchorTracker;
        _placement = placement;
        _placementChanged = placementChanged;
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
        IOverlayTextureSource textureSource,
        OverlayAnchor defaultAnchor,
        OverlayPlacement placement,
        double opacity,
        double sizeScale,
        NotificationAppearanceSettings appearance,
        Action<OverlayPlacement> placementChanged,
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

            var laserInput = true;
            try
            {
                surface.SetMouseScale(WpfNotificationRenderer.PanelWidth, WpfNotificationRenderer.PanelHeight);
            }
            catch (Exception exception)
            {
                laserInput = false;
                log(
                    "Notifications cannot be positioned by hand on this SteamVR version: "
                    + $"{exception.Message} Everything else about them works.");
            }

            renderer = new WpfNotificationRenderer();
            var anchorTracker = new OverlayAnchorTracker("Notifications", defaultAnchor, log, placement);
            anchorTracker.Tick(openVr, surface);
            var overlay = new NotificationOverlay(
                surface,
                textureSource,
                renderer,
                new NotificationPlayer(),
                anchorTracker,
                placement,
                placementChanged,
                log)
            {
                _opacity = opacity,
                _sizeScale = sizeScale,
                _laserInputAvailable = laserInput
            };
            overlay.ApplyAppearance(appearance);
            return overlay;
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

    /// <summary>
    /// Queues a notification. Safe to call whether or not one is currently
    /// showing. Resolves duration/accent/image precedence once, here, so
    /// every downstream reader (<see cref="NotificationPlayer"/>, the
    /// renderer) sees an already-resolved value and never has to ask "did
    /// this come from the payload or from settings" - the same reasoning
    /// Phase 4's transient overrides already applied. See §B4/§B5.
    /// </summary>
    public void Enqueue(StreamerBotEventPayload payload)
    {
        if (_disposed)
        {
            return;
        }

        _player.Enqueue(payload.WithNotificationDefaults(_defaultDurationMs, _accentHex, _templatePath));
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
            // Same reasoning as ChatOverlay.SetAnchorOverride: a grab in
            // progress is only meaningful against the mode it started on.
            CancelDrag("the anchor changed");
        }

        _anchorTracker.SetAnchor(anchor);
    }

    /// <summary>
    /// Replaces the saved offset - a reset from the VR settings page, or a
    /// settings change arriving from the desktop. Abandons any drag in
    /// progress, per §B1: the wearer asked for a specific placement.
    /// </summary>
    public void SetPlacement(OverlayPlacement placement)
    {
        if (_disposed)
        {
            return;
        }

        CancelDrag(null);
        _placement = placement;
        _anchorTracker.SetPlacement(placement);
    }

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
    public void SetOpacity(double opacity)
    {
        _opacity = Math.Clamp(opacity, 0.2, 1.0);
        // Forces the next tick to re-issue the alpha/width/offset calls even
        // if the transition's own relative progress has not moved - without
        // this, an opacity change made while a notification sits in its
        // (otherwise unchanging) Holding phase would not be visible until the
        // next notification.
        _transitionAnimator.Reset();
    }

    /// <summary>Sets the width multiplier, 0.5-2.0 - applied immediately, and forces a fresh transition tick for the same reason as <see cref="SetOpacity"/>.</summary>
    public void SetSizeScale(double sizeScale)
    {
        _sizeScale = Math.Clamp(sizeScale, 0.5, 2.0);
        _surface.SetWidthInMeters(WidthInMeters * (float)_sizeScale);
        _transitionAnimator.Reset();
    }

    /// <summary>Replaces every §B3/§B4/§B5 appearance setting at once - a VR or desktop settings change.</summary>
    public void ApplyAppearance(NotificationAppearanceSettings appearance)
    {
        _backgroundHex = appearance.BackgroundHex;
        _textHex = appearance.TextHex;
        _accentHex = appearance.AccentHex;
        _defaultDurationMs = Math.Clamp(
            appearance.DefaultDurationMs,
            StreamerBotEventPayload.MinimumDurationMs,
            StreamerBotEventPayload.MaximumDurationMs);
        _transition = appearance.Transition;
        _slideEdge = appearance.SlideEdge;
        _templatePath = appearance.TemplatePath;
        _backgroundOpacity = Math.Clamp(appearance.BackgroundOpacity, 0d, 1d);
        _cornerRadiusPixels = Math.Max(0d, appearance.CornerRadiusPixels);
        _transitionAnimator.Reset();
    }

    /// <summary>
    /// Turns the positioning frame on or off - §B1 of the Phase 7 plan. On,
    /// this surface shows representative placeholder text permanently and
    /// accepts the laser so it can be grabbed and dragged, exactly like the
    /// chat window. Off, it goes back to being a transient, non-interactive
    /// notification surface - the one hard constraint (§B1's "do not enable
    /// SetOverlayInputMethod on the notification overlay outside the
    /// positioning frame") is met by construction, since this is the only
    /// method that ever turns the laser on for this surface at all.
    /// <para>
    /// Trivially reversible: the toggle that calls this lives on the SteamVR
    /// dashboard, whose own input path is entirely separate from this
    /// overlay's laser, so it stays reachable no matter where the frame has
    /// been dragged to - the same hazard the self-limiting laser probe was
    /// designed around, met here by the two input systems never competing in
    /// the first place rather than by a timer.
    /// </para>
    /// </summary>
    public void SetPositioningEnabled(bool enabled)
    {
        if (_disposed || _positioning == enabled)
        {
            return;
        }

        _positioning = enabled;
        if (enabled)
        {
            RenderPositioningContent();
            if (!_shown)
            {
                _surface.Show();
                _shown = true;
            }

            SetInputEnabled(true);
            _log(
                "Notification positioning is on. A sample frame is showing - point the laser at it, "
                + "pull the trigger anywhere on it, and drag to move it.");
        }
        else
        {
            CancelDrag(null);
            SetInputEnabled(false);
            if (!_player.IsShowing)
            {
                _surface.Hide();
                _shown = false;
            }

            _transitionAnimator.Reset();
            _log("Notification positioning is off.");
        }
    }

    /// <summary>
    /// Developer-only: switches this surface between the default
    /// <c>SetOverlayRaw</c> path and the persistent-texture path - see
    /// <see cref="ChatOverlay.SetTexturePathEnabled"/> for the rationale.
    /// </summary>
    public void SetTexturePathEnabled(bool enabled) => _uploader.TexturePathEnabled = enabled;

    /// <summary>
    /// Re-resolves the anchor, then either drives the positioning frame's
    /// input or advances the fade timeline and, only when a new item just
    /// started, paints its texture. While idle and not hidden this makes no
    /// OpenVR call beyond the anchor check - see
    /// <see cref="NotificationTransitionAnimator"/> for how the transition
    /// itself also reaches zero calls once at rest.
    /// </summary>
    public void Tick(OpenVrInput openVr, long nowMs)
    {
        if (_disposed)
        {
            return;
        }

        _anchorTracker.Tick(openVr, _surface);

        if (_positioning)
        {
            TickPositioning(openVr);
            return;
        }

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
            // A new item's first frame must never be skipped as "unchanged"
            // against whatever the previous item's transition last settled
            // at - see NotificationTransitionAnimator.Reset.
            _transitionAnimator.Reset();
            var rendered = _renderer.Render(
                new NotificationContent(
                    payload.Title,
                    payload.Text,
                    payload.Accent,
                    _backgroundHex,
                    _textHex,
                    payload.Image,
                    _backgroundOpacity,
                    _cornerRadiusPixels));
            _uploader.Upload(rendered.Rgba, rendered.Width, rendered.Height);
            if (!_shown)
            {
                _surface.Show();
                _shown = true;
                _log("A notification is showing.");
            }
        }

        ApplyTransition(frame.Alpha);
    }

    /// <summary>
    /// Applies one instant of the configured transition, per §B3 - skipping
    /// every overlay call whenever <see cref="NotificationTransitionAnimator"/>
    /// reports nothing actually changed. That is what makes the Holding phase
    /// (constant progress for the bulk of a notification's time on screen)
    /// and the fully-Idle state both genuinely free, not merely infrequent.
    /// </summary>
    private void ApplyTransition(float progress)
    {
        var baseWidth = WidthInMeters * (float)_sizeScale;
        if (!_transitionAnimator.Advance(_transition, _slideEdge, progress, baseWidth, out var transform))
        {
            return;
        }

        _surface.SetAlpha(transform.Alpha * (float)_opacity);
        _surface.SetWidthInMeters(baseWidth * transform.WidthScale);

        if (_anchorTracker.BoundDeviceIndex is { } deviceIndex)
        {
            var baseTransform = _placement.ToTransform(_anchorTracker.Anchor.Mode);
            var offset = VrOverlayTransform.Translation(transform.OffsetXMeters, transform.OffsetYMeters, 0f);
            _surface.AttachToDevice(deviceIndex, baseTransform * offset);
        }
    }

    private void RenderPositioningContent()
    {
        var rendered = _renderer.Render(
            new NotificationContent(
                "Sample notification",
                "This is what your notifications will look like. Drag me anywhere.",
                _accentHex,
                _backgroundHex,
                _textHex,
                _templatePath,
                _backgroundOpacity,
                _cornerRadiusPixels));
        _uploader.Upload(rendered.Rgba, rendered.Width, rendered.Height);
        _surface.SetAlpha((float)_opacity);
        _surface.SetWidthInMeters(WidthInMeters * (float)_sizeScale);
    }

    /// <summary>
    /// Drains the laser queue and advances a grab in progress. The whole
    /// panel is the handle - unlike the chat window there is no button table
    /// to hit-test against, so any <c>ButtonDown</c> while positioning starts
    /// a drag wherever it lands.
    /// </summary>
    private void TickPositioning(OpenVrInput openVr)
    {
        _surface.PollMouseEvents(_laserEvents);
        foreach (var laserEvent in _laserEvents)
        {
            switch (laserEvent.Kind)
            {
                case OverlayMouseEventKind.ButtonDown when _drag is null:
                    BeginDrag(openVr, laserEvent.DeviceIndex);
                    break;
                case OverlayMouseEventKind.ButtonUp when _drag is not null:
                    _drag = null;
                    _holding = false;
                    _placementChanged(_placement);
                    _log("The notification position was saved.");
                    break;
                case OverlayMouseEventKind.FocusLeave:
                    CancelDrag("the laser left the panel");
                    break;
            }
        }

        AdvanceDrag(openVr);
    }

    private void BeginDrag(OpenVrInput openVr, uint eventDeviceIndex)
    {
        var anchor = _anchorTracker.Anchor;
        _dragPointerDeviceIndex = ResolvePointerDeviceIndex(openVr, anchor, eventDeviceIndex);
        if (!TryGetGrabPoses(openVr, out var anchorPose, out var pointerPose))
        {
            _log("Notifications could not be grabbed: a controller is not tracking.");
            return;
        }

        _holding = true;
        _drag = OverlayDrag.Begin(anchor.Mode, _placement.For(anchor.Mode), anchorPose, pointerPose);
        _log("Notifications are being moved.");
    }

    private void AdvanceDrag(OpenVrInput openVr)
    {
        if (_drag is not { } drag)
        {
            return;
        }

        var anchor = _anchorTracker.Anchor;
        if (anchor.Mode != drag.Mode)
        {
            CancelDrag("the anchor changed mid-drag");
            return;
        }

        if (!TryGetGrabPoses(openVr, out var anchorPose, out var pointerPose))
        {
            CancelDrag("a controller lost tracking");
            return;
        }

        var offset = drag.OffsetAt(anchorPose, pointerPose);
        if (!offset.IsUsable())
        {
            CancelDrag("the controller pose was unusable");
            return;
        }

        _placement = _placement.With(anchor.Mode, offset.WithTranslationClamped(OverlayPlacement.LimitMeters));
        _anchorTracker.SetPlacement(_placement);
        _anchorTracker.Tick(openVr, _surface);
    }

    private void CancelDrag(string? reason)
    {
        if (_drag is null && !_holding)
        {
            return;
        }

        _drag = null;
        _holding = false;
        if (reason is not null)
        {
            _log($"Notifications stopped moving: {reason}.");
        }
    }

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

    /// <summary>Same left/right fallback heuristic as <c>ChatOverlay.ResolvePointerDeviceIndex</c>.</summary>
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
    /// The single writer of both halves of "this overlay is interactive" -
    /// same two-part switch <c>ChatOverlay.SetInputEnabled</c> uses, and the
    /// same reasoning: order matters on the way down, the flag comes off
    /// first and goes on last.
    /// </summary>
    private void SetInputEnabled(bool enabled)
    {
        if (_inputEnabled == enabled || !_laserInputAvailable)
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
            }
            else
            {
                _surface.SetMakesOverlaysInteractive(false);
                _surface.SetAcceptsLaserInput(false);
            }
        }
        catch (Exception exception)
        {
            _laserInputAvailable = false;
            _inputEnabled = false;
            _log(
                "Notifications cannot be made interactive on this SteamVR version: "
                + $"{exception.Message} Everything else about them works.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            SetInputEnabled(false);
        }
        catch (Exception)
        {
            // SteamVR may already have gone away - see ChatOverlay.Dispose's identical guard.
        }

        _disposed = true;
        _renderer.Dispose();
        _surface.Dispose();
        // After the surface: SteamVR holds a reference to the texture until
        // DestroyOverlay releases it.
        _uploader.Dispose();
    }
}
