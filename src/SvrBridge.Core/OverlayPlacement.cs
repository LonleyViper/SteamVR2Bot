using System.Globalization;

namespace SvrBridge.Core;

/// <summary>
/// Where one overlay sits within its anchor, as a full rigid transform per
/// anchor mode - position <b>and</b> orientation.
/// <para>
/// Translation-only was the first shape this took, and the headset rejected
/// it: dragging moved the panel around but could not tilt or turn it, which
/// makes a wrist panel awkward to read at any angle the fixed watch-face tilt
/// did not anticipate. A grab that carries orientation is what the interaction
/// this was modelled on actually does, so the saved value has to carry it too.
/// </para>
/// <para>
/// <see cref="Default"/> is exactly the pair of transforms Phases 1/2/3 proved
/// against hardware - the same constants, not a reconstruction of them - so an
/// install that has never dragged anything looks precisely as it did before
/// this phase, and reset restores that value rather than an approximation.
/// </para>
/// </summary>
public readonly record struct OverlayPlacement(
    VrOverlayTransform ControllerOffset,
    VrOverlayTransform HeadOffset)
{
    /// <summary>
    /// How far from the anchor device a panel may be placed, in metres. Two
    /// metres is far past anything usable in either direction; this exists so
    /// one bad pose sample - or a NaN out of a tracking dropout - cannot save
    /// a placement that puts the panel somewhere the wearer can never point at
    /// it again to drag it back. The reset control is the other half of that
    /// safety net.
    /// </summary>
    public const float LimitMeters = 2f;

    public static readonly OverlayPlacement Default = new(
        OverlayAnchor.ControllerOffset,
        OverlayAnchor.HeadOffset);

    /// <summary>The transform in force for one anchor mode.</summary>
    public VrOverlayTransform For(OverlayAnchorMode mode) =>
        mode == OverlayAnchorMode.Head ? HeadOffset : ControllerOffset;

    /// <summary>This placement with one mode's transform replaced, the other left alone.</summary>
    public OverlayPlacement With(OverlayAnchorMode mode, VrOverlayTransform offset) =>
        mode == OverlayAnchorMode.Head
            ? this with { HeadOffset = offset }
            : this with { ControllerOffset = offset };

    /// <summary>
    /// The transform to hand
    /// <c>SetOverlayTransformTrackedDeviceRelative</c>, guarded against a
    /// value that would lose the panel - see <see cref="LimitMeters"/> and
    /// <see cref="VrOverlayTransform.IsUsable"/>.
    /// </summary>
    public VrOverlayTransform ToTransform(OverlayAnchorMode mode)
    {
        var offset = For(mode);
        return offset.IsUsable() ? offset.WithTranslationClamped(LimitMeters) : Default.For(mode);
    }

    /// <summary>
    /// This placement with any half that is not a usable transform replaced by
    /// the proven default for that mode.
    /// <para>
    /// <see cref="ToTransform"/> already refuses to apply a bad value, so this
    /// is not what keeps the panel visible - it is what stops a bad value
    /// being carried around and written back to disk on the next save, where
    /// it would look for all the world like a placement the wearer chose.
    /// Applied where saved or externally-supplied values enter: the settings
    /// store and <see cref="Parse"/>.
    /// </para>
    /// <para>
    /// The concrete case this exists for: this type's persisted shape changed
    /// from three numbers per anchor mode to a full twelve-element transform.
    /// A settings file written before that change has no property the new
    /// shape recognises, so it deserialises to all zeros rather than to
    /// "absent" - and a zero matrix is a perfectly valid-looking value that
    /// collapses the overlay to nothing. A field whose shape changes without a
    /// version marker cannot rely on the deserialiser to notice.
    /// </para>
    /// </summary>
    public OverlayPlacement Sanitised() => new(
        ControllerOffset.IsUsable() ? ControllerOffset : Default.ControllerOffset,
        HeadOffset.IsUsable() ? HeadOffset : Default.HeadOffset);

    /// <summary>
    /// Twenty-four invariant-culture numbers, for the OpenVR worker's command
    /// line - one argument rather than twenty-four, so a placement can never
    /// be half-applied by a partially-updated spawn.
    /// </summary>
    public string ToArgument() =>
        string.Join(",", ControllerOffset.ToFloats().Concat(HeadOffset.ToFloats())
            .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>
    /// The inverse of <see cref="ToArgument"/>. Anything unparseable falls
    /// back to <see cref="Default"/> whole, never per-number: a half-read
    /// placement is worse than the proven one, and a worker spawned by an
    /// older build simply has no such argument.
    /// </summary>
    public static OverlayPlacement Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Default;
        }

        var parts = raw.Split(',');
        if (parts.Length != 24)
        {
            return Default;
        }

        var values = new float[24];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!float.TryParse(
                    parts[index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out values[index]))
            {
                return Default;
            }
        }

        return new OverlayPlacement(
            VrOverlayTransform.FromFloats(values, 0),
            VrOverlayTransform.FromFloats(values, 12)).Sanitised();
    }
}

/// <summary>
/// One in-progress grab of an overlay panel, captured at the moment the laser
/// button went down.
/// <para>
/// A rigid grab, not a translation: the panel keeps whatever spatial
/// relationship it had to the grabbing controller when it was taken hold of,
/// so turning the wrist turns the panel and pushing it away moves it away.
/// That relationship is this type's entire contents - one constant transform,
/// <see cref="PanelInPointer"/>, computed once and then applied against
/// whatever the two devices are doing now.
/// </para>
/// <para>
/// The overlay's own mouse events are only the grab and release signal. They
/// carry 2D coordinates on the panel surface, not a world-space ray, so they
/// can say the wearer is holding on but not where they have moved to; the
/// movement comes from the tracked poses.
/// </para>
/// <para>
/// The first version of this drove the drag from <see cref="MotionSample"/>'s
/// body frame, which is yaw-only and carries no device rotation. That could
/// only ever produce translation, and left the axis mapping exact only while
/// the anchor device was level and facing forward. Working in device poses
/// directly removes both limitations at once - there is no frame conversion
/// left to be approximately right.
/// </para>
/// </summary>
public readonly record struct OverlayDrag(
    OverlayAnchorMode Mode,
    VrOverlayTransform PanelInPointer)
{
    /// <summary>
    /// Takes hold of a panel currently sitting at <paramref name="offset"/>
    /// relative to <paramref name="anchorPose"/>, with the grabbing controller
    /// at <paramref name="pointerPose"/>. All poses are world-space.
    /// </summary>
    public static OverlayDrag Begin(
        OverlayAnchorMode mode,
        VrOverlayTransform offset,
        VrOverlayTransform anchorPose,
        VrOverlayTransform pointerPose) =>
        new(mode, pointerPose.InverseRigid() * (anchorPose * offset));

    /// <summary>
    /// Where the panel should sit now, expressed the way SteamVR wants it -
    /// relative to the anchor device - given where both devices currently are.
    /// <para>
    /// Reads right to left: put the panel back where it was held relative to
    /// the pointing controller, then express that in the anchor's frame. Both
    /// devices are free to move; only their relationship to the panel is
    /// pinned.
    /// </para>
    /// </summary>
    public VrOverlayTransform OffsetAt(VrOverlayTransform anchorPose, VrOverlayTransform pointerPose) =>
        anchorPose.InverseRigid() * (pointerPose * PanelInPointer);
}
