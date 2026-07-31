using System.Numerics;

namespace SvrBridge.Core;

/// <summary>
/// How a panel currently stands in relation to the wearer's head: whether they
/// are looking at it, whether it is facing them, and how far away it is.
/// <para>
/// All three come off the same pair of world poses, so they are computed
/// together rather than three times over. They answer three different
/// questions and must not be confused: <see cref="GazeDot"/> is "am I looking
/// towards it", <see cref="FacingDot"/> is "is its front towards me", and
/// <see cref="DistanceMeters"/> is "is it near enough to read". A panel can
/// easily be looked at while turned edge-on, which is exactly the case worth
/// hiding.
/// </para>
/// <para>
/// This is a true eye-line measurement in world space, pitch included -
/// deliberately unlike the yaw-only body frame the gesture recognizers use.
/// The first version of the chat window's gaze test measured the direction to
/// the anchor <em>device</em> in that frame, which was fine while the panel
/// was welded 12 cm off the wrist and wrong the moment the wearer could drag
/// it somewhere else: it grew and shrank according to where the controller was
/// rather than where the window was.
/// </para>
/// </summary>
public readonly record struct PanelView(float GazeDot, float FacingDot, float DistanceMeters)
{
    /// <summary>
    /// Below this the head and the panel are effectively co-located and every
    /// direction is meaningless. Reported as fully engaged rather than as a
    /// direction, because a panel in the wearer's face is being looked at by
    /// any reasonable reading.
    /// </summary>
    private const float DegenerateDistanceMeters = 1e-3f;

    /// <summary>
    /// Measures a panel at <paramref name="panelPose"/> against a head at
    /// <paramref name="headPose"/>, both in world space.
    /// <para>
    /// Two conventions are load-bearing here, and both are OpenVR's rather
    /// than this app's. A device looks down its own <b>-Z</b>, so the head's
    /// forward axis is the negated third column. An overlay's texture faces
    /// along its own <b>+Z</b> - which is why a tracked-device-relative panel
    /// placed in front of its device already faces back towards it, and why
    /// the wrist panel's -0.6 rad tilt about X points its front up towards the
    /// wearer's eyes.
    /// </para>
    /// </summary>
    public static PanelView From(VrOverlayTransform headPose, VrOverlayTransform panelPose)
    {
        var headPosition = new Vector3(headPose.M03, headPose.M13, headPose.M23);
        var panelPosition = new Vector3(panelPose.M03, panelPose.M13, panelPose.M23);
        var toPanel = panelPosition - headPosition;
        var distance = toPanel.Length();
        if (!float.IsFinite(distance) || distance < DegenerateDistanceMeters)
        {
            return new PanelView(1f, 1f, 0f);
        }

        var towardsPanel = toPanel / distance;
        var headForward = new Vector3(-headPose.M02, -headPose.M12, -headPose.M22);
        var panelNormal = new Vector3(panelPose.M02, panelPose.M12, panelPose.M22);

        return new PanelView(
            Vector3.Dot(towardsPanel, headForward),
            Vector3.Dot(panelNormal, -towardsPanel),
            distance);
    }
}

/// <summary>
/// Decides whether a panel is worth drawing at all, from how it is turned and
/// how far off it is - the behaviour established VR overlay apps have, and
/// which this app's own wrist window needed once its placement became the
/// wearer's to choose.
/// <para>
/// Separate from the gaze test on purpose. Gaze answers "is the wearer
/// attending to this", and drives the scale animation and the input gate.
/// This answers "is there any point drawing this", and a panel turned away
/// fails it while being looked at directly - a flat quad seen edge-on is a
/// line, and seen from behind it is a mirror-image nuisance sitting in the
/// wearer's view.
/// </para>
/// <para>
/// Both tests carry hysteresis for the same reason
/// <see cref="ChatGazeHysteresis"/> does: a single threshold sits still while
/// pose noise crosses back and forth over it, and a panel that flickers in and
/// out at the boundary is worse than one that is simply always on.
/// </para>
/// </summary>
public sealed class PanelVisibilityGate
{
    /// <summary>
    /// Cosines of roughly 69° and 81°. A flat panel stays legible a long way
    /// off-axis, so this deliberately does not hide anything until it is
    /// nearly edge-on - the aim is to remove a panel that has been turned
    /// away, not to punish a wearer for reading one at an angle.
    /// </summary>
    private const float FacingEnter = 0.35f;

    /// <summary>See <see cref="FacingEnter"/>.</summary>
    private const float FacingExit = 0.15f;

    /// <summary>
    /// Generous on purpose. A panel anchored to a controller or the headset
    /// travels with the wearer, so it can only end up far away by being
    /// dragged there - and a placement deliberately pushed out to arm's length
    /// and beyond, scaled up, is a legitimate way to use a big panel. This is
    /// meant to catch a window that has been lost, not to overrule a choice.
    /// </summary>
    private const float NearMeters = 1.8f;

    /// <summary>See <see cref="NearMeters"/>.</summary>
    private const float FarMeters = 2.0f;

    private bool _visible = true;

    /// <summary>The verdict as of the most recent <see cref="Update"/>. Starts visible.</summary>
    public bool IsVisible => _visible;

    /// <summary>
    /// Advances the state machine with one new measurement.
    /// <para>
    /// Hiding needs only one of the two tests to fail; coming back needs both
    /// to pass. That asymmetry is deliberate - a panel that is both turned
    /// away and far off should not flicker back into view the instant either
    /// one alone recovers.
    /// </para>
    /// </summary>
    public bool Update(float facingDot, float distanceMeters)
    {
        if (_visible)
        {
            if (facingDot < FacingExit || distanceMeters > FarMeters)
            {
                _visible = false;
            }
        }
        else if (facingDot >= FacingEnter && distanceMeters <= NearMeters)
        {
            _visible = true;
        }

        return _visible;
    }

    /// <summary>
    /// Forces the panel visible and resets the state machine - used when the
    /// wearer takes hold of it, and when a placement is reset. Something being
    /// deliberately handled must never be hidden out from under the person
    /// handling it.
    /// </summary>
    public void ForceVisible() => _visible = true;
}
