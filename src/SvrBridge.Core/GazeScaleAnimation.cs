namespace SvrBridge.Core;

/// <summary>
/// Eases a (width, alpha) pair towards a target and reports whether either
/// value actually moved this tick, so a caller driving an OpenVR overlay call
/// from the result can skip that call entirely once the animation has
/// converged.
/// <para>
/// Exponential easing only asymptotes towards its target - it never reaches
/// it exactly - so a caller that pushes <c>Width</c>/<c>Alpha</c> to the
/// overlay on every tick regardless would keep issuing
/// <c>SetOverlayWidthInMeters</c>/<c>SetOverlayAlpha</c> calls forever, even
/// with the window sitting at rest, changing by amounts far below anything
/// visible. This type exists to make "converged" an explicit, checkable
/// state rather than an implicit property of a value nobody compares against
/// anything.
/// </para>
/// </summary>
public sealed class GazeScaleAnimation
{
    // Half a millimetre of width and a fraction of a percent of alpha are
    // both well below what a wearer could perceive, so snapping here reads as
    // "already arrived", not as a visible jump.
    private const float WidthEpsilonMeters = 0.0005f;
    private const float AlphaEpsilon = 0.002f;

    private float _currentWidth;
    private float _currentAlpha;
    private float _targetWidth;
    private float _targetAlpha;
    private bool _converged = true;

    public GazeScaleAnimation(float initialWidth, float initialAlpha)
    {
        _currentWidth = _targetWidth = initialWidth;
        _currentAlpha = _targetAlpha = initialAlpha;
    }

    /// <summary>The current eased width in metres.</summary>
    public float Width => _currentWidth;

    /// <summary>The current eased alpha.</summary>
    public float Alpha => _currentAlpha;

    /// <summary>True once both values have snapped to their target and no further ticks will change them.</summary>
    public bool IsConverged => _converged;

    /// <summary>
    /// Sets where the animation is heading. A no-op if this is exactly the
    /// target already in effect - in particular, re-asserting the same
    /// target every tick (as <c>ChatOverlay.AnimateGaze</c> does, since it
    /// recomputes the target from the gaze verdict on every call) must not
    /// itself un-converge an animation that has already arrived.
    /// </summary>
    public void SetTarget(float targetWidth, float targetAlpha)
    {
        if (targetWidth == _targetWidth && targetAlpha == _targetAlpha)
        {
            return;
        }

        _targetWidth = targetWidth;
        _targetAlpha = targetAlpha;
        _converged = IsWithinEpsilon();
        if (_converged)
        {
            _currentWidth = _targetWidth;
            _currentAlpha = _targetAlpha;
        }
    }

    /// <summary>
    /// Advances the ease by <paramref name="deltaMs"/> using a time constant
    /// of <paramref name="timeConstantMs"/>. Returns false, doing nothing,
    /// once converged - that is the caller's cue to skip pushing
    /// <see cref="Width"/>/<see cref="Alpha"/> to the overlay this tick.
    /// </summary>
    public bool Advance(float deltaMs, float timeConstantMs)
    {
        if (_converged)
        {
            return false;
        }

        var t = deltaMs <= 0 ? 1f : 1f - MathF.Exp(-deltaMs / timeConstantMs);
        _currentWidth += (_targetWidth - _currentWidth) * t;
        _currentAlpha += (_targetAlpha - _currentAlpha) * t;

        if (IsWithinEpsilon())
        {
            _currentWidth = _targetWidth;
            _currentAlpha = _targetAlpha;
            _converged = true;
        }

        return true;
    }

    private bool IsWithinEpsilon() =>
        MathF.Abs(_targetWidth - _currentWidth) < WidthEpsilonMeters
        && MathF.Abs(_targetAlpha - _currentAlpha) < AlphaEpsilon;
}
