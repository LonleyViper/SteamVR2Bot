namespace SvrBridge.Core;

/// <summary>Where a VR overlay panel is pinned.</summary>
public enum OverlayAnchorMode
{
    Controller,
    Head
}

/// <summary>
/// Which hand a <see cref="OverlayAnchorMode.Controller"/> anchor follows.
/// Meaningless in <see cref="OverlayAnchorMode.Head"/> mode, but always
/// present on <see cref="OverlayAnchor"/> so the two modes share one shape.
/// </summary>
public enum OverlayAnchorHand
{
    Left,
    Right
}

/// <summary>
/// One anchor any overlay surface can use - controller-relative, with a
/// choice of hand, or head-relative - so "chat on my wrist" and
/// "notifications on my head" are the same feature rather than two special
/// cases. See §B2 of the Phase 4 plan.
/// <para>
/// <see cref="Offset"/> is deliberately keyed only by <see cref="Mode"/>, not
/// by which surface is asking: it is exactly the tipped wrist transform
/// Phase 1/3 proved in the headset for controller mode, and exactly the
/// translation-only transform Phase 2 proved for head mode. Reusing the same
/// two constants regardless of content means switching a surface's anchor
/// mode always lands it somewhere already validated on hardware, rather than
/// an unproven offset invented for the occasion.
/// </para>
/// <para>
/// Since Phase 5 those two transforms are the <b>defaults</b> rather than the
/// only values: the chat window's offset is user-owned, dragged by hand in the
/// headset and persisted per anchor mode - see <see cref="OverlayPlacement"/>.
/// <see cref="Offset"/> remains what a surface with no saved placement of its
/// own uses, which is still every surface but chat.
/// </para>
/// <para>
/// World-lock - an anchor with no tracked device at all, placed once in the
/// room - is a deliberate deferral, not a missing case here. It needs
/// <c>SetOverlayTransformAbsolute</c> (a different OpenVR call the
/// tracked-device-relative path cannot express) and a placement mechanism,
/// and grab-to-place needs the overlay input Phase 5 gates. See
/// <c>CHAT_AND_NOTIFICATIONS_PLAN.md</c>.
/// </para>
/// </summary>
public readonly record struct OverlayAnchor(OverlayAnchorMode Mode, OverlayAnchorHand Hand)
{
    /// <summary>
    /// Sits above and slightly in front of the controller origin, tipped back
    /// like a watch face - the exact transform Phase 1's test overlay and
    /// Phase 3's chat window both proved against the headset.
    /// </summary>
    public static readonly VrOverlayTransform ControllerOffset =
        VrOverlayTransform.Translation(0f, 0.06f, -0.12f) * VrOverlayTransform.RotationX(-0.6f);

    /// <summary>
    /// Metres, in the HMD's own frame: +X right, +Y up, +Z back toward the
    /// wearer. 0.6 m ahead is far enough to read comfortably without eye
    /// strain; 0.12 m down keeps it clear of the centre of view - the exact
    /// transform Phase 2's notifications proved against the headset. No
    /// rotation is composed in: a tracked-device-relative overlay already
    /// faces back toward the device that owns it, so an HMD-relative panel
    /// needs only a translation to face the wearer.
    /// </summary>
    public static readonly VrOverlayTransform HeadOffset =
        VrOverlayTransform.Translation(0f, -0.12f, -0.6f);

    public static OverlayAnchor Controller(OverlayAnchorHand hand) => new(OverlayAnchorMode.Controller, hand);

    public static readonly OverlayAnchor Head = new(OverlayAnchorMode.Head, OverlayAnchorHand.Left);

    /// <summary>
    /// The canonical, hardware-proven offset for this anchor's mode - the
    /// default a surface uses when it has no user-placed
    /// <see cref="OverlayPlacement"/> of its own.
    /// </summary>
    public VrOverlayTransform Offset => Mode == OverlayAnchorMode.Head ? HeadOffset : ControllerOffset;
}

/// <summary>
/// Resolves an <see cref="OverlayAnchor"/> to a live tracked-device index
/// every tick and re-applies the transform whenever it changes or the anchor
/// itself changes. Folds together the re-resolution logic that used to be
/// duplicated between <c>ChatOverlay</c> and <c>NotificationOverlay</c>.
/// <para>
/// The device index is never cached as a source of truth - only the index
/// this tracker last bound to is kept, purely to notice a change. Tracked
/// device indices are not stable across controller sleep, reconnect or a
/// battery change, and a role can come back unassigned; an overlay bound to a
/// stale index detaches silently, with no error reported anywhere, which is
/// why this asks again on every tick rather than trusting its own memory.
/// </para>
/// </summary>
public sealed class OverlayAnchorTracker
{
    private const uint HmdDeviceIndex = 0;

    private readonly string _surfaceName;
    private readonly Action<string> _log;
    private OverlayAnchor _anchor;
    private OverlayPlacement _placement;
    private uint? _boundDeviceIndex;
    private bool _warnedAboutMissingController;

    /// <summary>
    /// Set when the transform changed without the device index changing - a
    /// hand drag, a reset, or a settings apply - so the next
    /// <see cref="Tick"/> re-attaches without logging a re-attach the wearer
    /// did not cause. Kept separate from <see cref="_boundDeviceIndex"/> for
    /// exactly that reason: a drag re-attaches on every poll, and routing it
    /// through the device-change path would fill the log at a hundred lines a
    /// second.
    /// </summary>
    private bool _placementDirty;

    public OverlayAnchorTracker(
        string surfaceName,
        OverlayAnchor initialAnchor,
        Action<string> log,
        OverlayPlacement? placement = null)
    {
        _surfaceName = surfaceName;
        _anchor = initialAnchor;
        _placement = placement ?? OverlayPlacement.Default;
        _log = log;
    }

    /// <summary>The anchor currently in effect, including any transient override.</summary>
    public OverlayAnchor Anchor => _anchor;

    /// <summary>The offsets currently in effect, one per anchor mode.</summary>
    public OverlayPlacement Placement => _placement;

    /// <summary>
    /// The tracked device this surface is currently attached to, or null
    /// before the first successful attach. Exposed so a grab can ask for that
    /// device's pose - the panel's placement is expressed relative to it, so
    /// moving the panel means knowing where it is.
    /// </summary>
    public uint? BoundDeviceIndex => _boundDeviceIndex;

    /// <summary>
    /// Changes which anchor to follow. A no-op when it is already the current
    /// anchor, so a caller can call this every settings-apply without forcing
    /// a needless re-attach; otherwise forces one on the next tick even if the
    /// resolved device index happens to come back unchanged; the transform
    /// itself may have changed mode.
    /// </summary>
    public void SetAnchor(OverlayAnchor anchor)
    {
        if (_anchor.Equals(anchor))
        {
            return;
        }

        _anchor = anchor;
        _boundDeviceIndex = null;
    }

    /// <summary>
    /// Changes where the surface sits relative to whichever device it follows
    /// - a hand drag in progress, a reset, or a settings apply. A no-op when
    /// the placement is unchanged, so this is safe to call every tick;
    /// otherwise the next <see cref="Tick"/> re-attaches silently.
    /// </summary>
    public void SetPlacement(OverlayPlacement placement)
    {
        if (_placement.Equals(placement))
        {
            return;
        }

        _placement = placement;
        _placementDirty = true;
    }

    /// <summary>Re-resolves the device index and re-attaches the surface if it moved.</summary>
    public void Tick(OpenVrInput openVr, VrOverlaySurface surface)
    {
        var deviceIndex = ResolveDeviceIndex(openVr);
        if (deviceIndex is null)
        {
            if (!_warnedAboutMissingController)
            {
                _warnedAboutMissingController = true;
                _log($"{_surfaceName} is waiting for {DescribeAnchor()}.");
            }

            _boundDeviceIndex = null;
            return;
        }

        _warnedAboutMissingController = false;
        var deviceChanged = _boundDeviceIndex != deviceIndex.Value;
        if (!deviceChanged && !_placementDirty)
        {
            return;
        }

        surface.AttachToDevice(deviceIndex.Value, _placement.ToTransform(_anchor.Mode));
        _boundDeviceIndex = deviceIndex.Value;
        _placementDirty = false;
        if (deviceChanged)
        {
            _log($"{_surfaceName} is following {DescribeAnchor()} (device {deviceIndex.Value}).");
        }
    }

    private uint? ResolveDeviceIndex(OpenVrInput openVr) =>
        _anchor.Mode == OverlayAnchorMode.Head
            ? HmdDeviceIndex
            : openVr.TryGetControllerDeviceIndex(
                _anchor.Hand == OverlayAnchorHand.Left ? ControllerHand.Left : ControllerHand.Right);

    private string DescribeAnchor() =>
        _anchor.Mode == OverlayAnchorMode.Head
            ? "the headset"
            : $"the {(_anchor.Hand == OverlayAnchorHand.Left ? "left" : "right")} controller";
}
