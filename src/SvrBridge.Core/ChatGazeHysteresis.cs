namespace SvrBridge.Core;

/// <summary>
/// Two-threshold state machine deciding whether the wearer is currently
/// looking at the chat window, from the cosine of the angle between the
/// head's forward vector and the head-to-window direction.
/// <para>
/// A single threshold flickers when the gaze angle sits near the boundary:
/// ordinary pose noise crosses back and forth across it, and the window
/// scales up and down on every sample, which is both ugly and, in a headset,
/// nauseating. Two thresholds with a dead zone between them fix that -
/// entering requires looking more directly at the window than leaving does,
/// so noise confined to either edge cannot cross both boundaries at once.
/// </para>
/// </summary>
public sealed class ChatGazeHysteresis
{
    private readonly float _enterCosine;
    private readonly float _exitCosine;
    private bool _isGazing;

    /// <param name="enterAngleDegrees">
    /// Half-angle within which gaze is judged to have started. Must be
    /// smaller than <paramref name="exitAngleDegrees"/>.
    /// </param>
    /// <param name="exitAngleDegrees">
    /// Half-angle beyond which gaze is judged to have ended.
    /// </param>
    public ChatGazeHysteresis(float enterAngleDegrees = 20f, float exitAngleDegrees = 35f)
    {
        if (enterAngleDegrees <= 0f || enterAngleDegrees >= exitAngleDegrees)
        {
            throw new ArgumentOutOfRangeException(
                nameof(enterAngleDegrees),
                enterAngleDegrees,
                "The enter angle must be positive and smaller than the exit angle.");
        }

        _enterCosine = MathF.Cos(enterAngleDegrees * MathF.PI / 180f);
        _exitCosine = MathF.Cos(exitAngleDegrees * MathF.PI / 180f);
    }

    /// <summary>The verdict as of the most recent <see cref="Update"/>, or false before the first call.</summary>
    public bool IsGazing => _isGazing;

    /// <summary>
    /// Advances the state machine with one new sample and returns the
    /// (possibly unchanged) verdict.
    /// <para>
    /// The comparisons are deliberately asymmetric: entering uses
    /// <c>&gt;=</c> and leaving uses <c>&lt;</c>, so a sample landing exactly
    /// on either threshold - the case a naive single-threshold design gets
    /// wrong most visibly - resolves to one definite state and stays there
    /// under repeated identical samples, rather than toggling.
    /// </para>
    /// </summary>
    public bool Update(float forwardDotToWindow)
    {
        if (!_isGazing && forwardDotToWindow >= _enterCosine)
        {
            _isGazing = true;
        }
        else if (_isGazing && forwardDotToWindow < _exitCosine)
        {
            _isGazing = false;
        }

        return _isGazing;
    }
}
