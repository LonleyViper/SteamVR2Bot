using System.Numerics;

namespace SvrBridge.Core;

/// <summary>Which tracked devices reported a usable pose this sample.</summary>
[Flags]
public enum MotionTracking
{
    None = 0,
    Head = 1,
    Left = 2,
    Right = 4,
    Hands = Left | Right,
    All = Head | Left | Right
}

/// <summary>
/// A yaw-only frame of reference centred on the headset.
/// <para>
/// Gestures have to mean the same thing wherever the user stands and whichever
/// way they face, so hand positions are expressed relative to the head rather
/// than to the play space. Only the head's yaw is used: if pitch or roll were
/// included, glancing down mid-gesture would rotate the whole gesture space and
/// a level sweep would read as a diagonal one.
/// </para>
/// <para>
/// Local axes follow the body rather than OpenVR: +X is the user's right, +Y
/// is up, and +Z is straight ahead. OpenVR reports the headset looking down
/// its own -Z, so callers negate that column before passing it in.
/// </para>
/// </summary>
public readonly record struct BodyFrame(
    Vector3 Origin,
    Vector3 Right,
    Vector3 Up,
    Vector3 Forward)
{
    private const float HorizontalEpsilon = 1e-3f;

    public static BodyFrame Identity { get; } = new(
        Vector3.Zero,
        Vector3.UnitX,
        Vector3.UnitY,
        -Vector3.UnitZ);

    /// <summary>
    /// Builds the frame from a head pose. <paramref name="forward"/> is the
    /// world-space direction the user is looking and <paramref name="up"/> is
    /// the headset's own up axis, used only to recover yaw when the user is
    /// looking straight up or down.
    /// </summary>
    public static BodyFrame FromHead(Vector3 position, Vector3 forward, Vector3 up)
    {
        var flattened = Flatten(forward);
        if (flattened is null)
        {
            // Looking straight up or down leaves no yaw in the forward axis.
            // The head's own up axis still carries it: looking down, up points
            // where forward used to; looking up, it points behind the user.
            var fallback = Flatten(forward.Y < 0 ? up : -up);
            flattened = fallback ?? Identity.Forward;
        }

        var resolvedForward = flattened.Value;
        var worldUp = Vector3.UnitY;
        return new BodyFrame(
            position,
            Vector3.Normalize(Vector3.Cross(resolvedForward, worldUp)),
            worldUp,
            resolvedForward);
    }

    /// <summary>Converts a world-space position into body-relative metres.</summary>
    public Vector3 ToLocalPoint(Vector3 world)
    {
        var offset = world - Origin;
        return new Vector3(
            Vector3.Dot(offset, Right),
            Vector3.Dot(offset, Up),
            Vector3.Dot(offset, Forward));
    }

    /// <summary>
    /// Converts a world-space direction, such as a velocity, into body space.
    /// Unlike a position this ignores the origin, so translation does not leak
    /// into a velocity reading.
    /// </summary>
    public Vector3 ToLocalDirection(Vector3 world) =>
        new(
            Vector3.Dot(world, Right),
            Vector3.Dot(world, Up),
            Vector3.Dot(world, Forward));

    /// <summary>
    /// Projects a vector onto the horizontal plane and normalizes it, or
    /// returns null when almost nothing is left to normalize.
    /// </summary>
    private static Vector3? Flatten(Vector3 value)
    {
        var horizontal = new Vector3(value.X, 0, value.Z);
        return horizontal.Length() < HorizontalEpsilon
            ? null
            : Vector3.Normalize(horizontal);
    }
}

/// <summary>
/// One timestamped observation of where the hands are and how fast they are
/// moving, already expressed in the wearer's <see cref="BodyFrame"/>.
/// <para>
/// Velocities come from the tracking driver rather than from differencing
/// successive positions, so they stay usable even when the poll loop jitters.
/// </para>
/// </summary>
public readonly record struct MotionSample(
    long TimestampMs,
    MotionTracking Tracking,
    Vector3 LeftPosition,
    Vector3 LeftVelocity,
    Vector3 RightPosition,
    Vector3 RightVelocity,
    float HeadHeightMeters)
{
    public static MotionSample Untracked(long timestampMs) => new(
        timestampMs,
        MotionTracking.None,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        Vector3.Zero,
        0);

    public bool HasHands => (Tracking & MotionTracking.Hands) == MotionTracking.Hands;

    /// <summary>
    /// True when every device a gesture needs reported a good pose. Recognizers
    /// must abort rather than fire when this goes false mid-gesture; a
    /// tracking dropout otherwise looks exactly like a fast movement.
    /// </summary>
    public bool IsUsable => Tracking == MotionTracking.All;

    public Vector3 Position(ControllerHand hand) =>
        hand == ControllerHand.Left ? LeftPosition : RightPosition;

    public Vector3 Velocity(ControllerHand hand) =>
        hand == ControllerHand.Left ? LeftVelocity : RightVelocity;
}
