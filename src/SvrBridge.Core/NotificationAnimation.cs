namespace SvrBridge.Core;

/// <summary>Named entry/exit transitions for the notification panel. See §B3 of the Phase 7 plan.</summary>
public enum NotificationTransition
{
    /// <summary>The behaviour every notification already had before this phase.</summary>
    Fade,

    /// <summary>Travels in from - and back out to - one edge of the panel's own footprint.</summary>
    Slide,

    /// <summary>Grows from a smaller size while fading in, shrinks back while fading out.</summary>
    ScalePop
}

/// <summary>Which edge <see cref="NotificationTransition.Slide"/> travels from.</summary>
public enum NotificationSlideEdge
{
    Top,
    Bottom,
    Left,
    Right
}

/// <summary>
/// One instant of a transition: the alpha and width to hand
/// <see cref="VrOverlaySurface.SetAlpha"/>/<see cref="VrOverlaySurface.SetWidthInMeters"/>,
/// plus a translation offset - in the anchor device's own metres, the same
/// frame <see cref="OverlayAnchor.Offset"/> is expressed in - to lay on top
/// of the panel's placement transform while sliding.
/// </summary>
public readonly record struct NotificationTransform(
    float Alpha,
    float WidthScale,
    float OffsetXMeters,
    float OffsetYMeters);

/// <summary>
/// Pure functions of <see cref="NotificationPlayer"/>'s own time-driven
/// <c>progress</c> (0 = fully faded out, 1 = fully shown) - not a new
/// per-tick ease towards a moving target the way
/// <see cref="GazeScaleAnimation"/> is. <see cref="NotificationFrame.Alpha"/>
/// is already exactly this progress value during fade-in/hold/fade-out, so
/// feeding it straight in here means every transition inherits the player's
/// existing, already-terminating timeline instead of inventing a second one -
/// there is no new asymptote to worry about converging.
/// </summary>
public static class NotificationTransitionCurve
{
    /// <summary>How far ScalePop starts below full size - 60% reads as a distinct "pop", not a jump cut.</summary>
    private const float ScalePopMinimumScale = 0.6f;

    public static NotificationTransform Evaluate(
        NotificationTransition transition,
        NotificationSlideEdge slideEdge,
        float progress,
        float baseWidthMeters)
    {
        var clamped = Math.Clamp(progress, 0f, 1f);
        return transition switch
        {
            NotificationTransition.ScalePop => new NotificationTransform(
                clamped,
                ScalePopMinimumScale + ((1f - ScalePopMinimumScale) * clamped),
                0f,
                0f),
            NotificationTransition.Slide => Slide(slideEdge, clamped, baseWidthMeters),
            _ => new NotificationTransform(clamped, 1f, 0f, 0f)
        };
    }

    private static NotificationTransform Slide(
        NotificationSlideEdge edge,
        float progress,
        float baseWidthMeters)
    {
        // Fully off its own footprint at progress 0, exactly at rest at
        // progress 1 - so a slide never travels further than the panel's own
        // width, whatever size it happens to be configured at.
        var travel = baseWidthMeters * (1f - progress);
        return edge switch
        {
            NotificationSlideEdge.Left => new NotificationTransform(progress, 1f, -travel, 0f),
            NotificationSlideEdge.Right => new NotificationTransform(progress, 1f, travel, 0f),
            NotificationSlideEdge.Top => new NotificationTransform(progress, 1f, 0f, travel),
            _ => new NotificationTransform(progress, 1f, 0f, -travel)
        };
    }
}

/// <summary>
/// Turns a stream of <see cref="NotificationTransitionCurve.Evaluate"/>
/// results into a decision the caller can act on directly: issue the overlay
/// calls, or skip them because nothing actually changed since the last tick.
/// <para>
/// This is the type the Phase 7 plan's one hard rule is about. A
/// non-converging animation is invisible to a visual check - the residual
/// movement is far below a pixel - so the only thing that catches it is
/// counting calls, which is exactly what <see cref="Advance"/> makes
/// countable: it returns <c>false</c>, issuing nothing, whenever the computed
/// transform is unchanged from the one last reported. Since
/// <see cref="NotificationTransitionCurve"/> is a closed-form function of the
/// player's own bounded fade timeline rather than an asymptotic ease, holding
/// steady during the "showing" phase and doing nothing at all while idle both
/// fall out for free - there is no separate convergence state to track.
/// </para>
/// </summary>
public sealed class NotificationTransitionAnimator
{
    private NotificationTransform? _last;

    /// <summary>
    /// Evaluates the transition at <paramref name="progress"/> and reports
    /// whether it differs from the last value this animator handed out.
    /// Returns <c>false</c> (and still fills in <paramref name="transform"/>,
    /// for a caller that wants the value regardless) when nothing changed -
    /// the caller's cue to skip every overlay call for this tick.
    /// </summary>
    public bool Advance(
        NotificationTransition transition,
        NotificationSlideEdge slideEdge,
        float progress,
        float baseWidthMeters,
        out NotificationTransform transform)
    {
        transform = NotificationTransitionCurve.Evaluate(transition, slideEdge, progress, baseWidthMeters);
        if (_last is { } last && last == transform)
        {
            return false;
        }

        _last = transform;
        return true;
    }

    /// <summary>
    /// Forgets the last reported value, so the next <see cref="Advance"/>
    /// call always reports a change - used when a new notification starts,
    /// so its first frame is never skipped as "unchanged" against whatever
    /// the previous notification last settled at.
    /// </summary>
    public void Reset() => _last = null;
}

/// <summary>
/// Everything §B3/§B4/§B5 of the Phase 7 plan added to notification
/// appearance, bundled into one value so <c>NotificationOverlay</c> exposes
/// one setter rather than seven - the same reasoning
/// <see cref="VrSettingsSnapshot"/> already applies to the chat/notification
/// settings page as a whole.
/// </summary>
/// <param name="BackgroundHex">"" falls back to the renderer's own hardcoded default - covers a settings file predating this feature.</param>
/// <param name="TextHex">See <paramref name="BackgroundHex"/>.</param>
/// <param name="AccentHex">
/// The settings-level default; a payload's own <c>accent</c> still wins when
/// present, per §B4's precedence rule - resolved before this ever reaches the
/// renderer, so this is only ever seen when a payload was silent about it.
/// </param>
/// <param name="DefaultDurationMs">Clamped to <see cref="StreamerBotEventPayload.MinimumDurationMs"/>/<see cref="StreamerBotEventPayload.MaximumDurationMs"/> by the caller.</param>
/// <param name="TemplatePath">"" means no template - see §B5. A payload's own <c>image</c> still wins when present.</param>
/// <param name="BackgroundOpacity">
/// 0 (fully transparent - just text over whatever is behind it, or a
/// template with no tint) to 1 (fully opaque). Clamped by the caller.
/// Independent of the panel's own overall <c>SetOverlayAlpha</c> fade, which
/// still applies on top of this - this only controls the background fill's
/// own alpha channel.
/// </param>
/// <param name="CornerRadiusPixels">
/// 0 is the original square-cornered panel. WPF's <c>Border.CornerRadius</c>
/// clips its <c>Background</c> brush (solid colour or template image) to the
/// rounded shape automatically, so this needs no separate clipping logic.
/// </param>
public sealed record NotificationAppearanceSettings(
    string BackgroundHex,
    string TextHex,
    string AccentHex,
    int DefaultDurationMs,
    NotificationTransition Transition,
    NotificationSlideEdge SlideEdge,
    string TemplatePath,
    double BackgroundOpacity,
    double CornerRadiusPixels)
{
    /// <summary>
    /// Exactly the panel's alpha before this setting existed -
    /// <c>WpfNotificationRenderer</c> hardcoded 235/255 for its background
    /// colour, a decision now equivalent to this default rather than
    /// something a settings file predating the field could disagree with.
    /// </summary>
    public const double DefaultBackgroundOpacity = 235.0 / 255.0;

    /// <summary>Exactly what every notification looked like before this phase - the migration default.</summary>
    public static readonly NotificationAppearanceSettings Default = new(
        "",
        "",
        "",
        StreamerBotEventPayload.DefaultDurationMs,
        NotificationTransition.Fade,
        NotificationSlideEdge.Bottom,
        "",
        DefaultBackgroundOpacity,
        0);
}
