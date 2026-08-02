using System.Numerics;

namespace SvrBridge.Core;

/// <summary>
/// A wearer-calibrated direction to the chat panel, expressed in the HMD's
/// local right/up/forward frame. An unusable value means the app should use
/// the original direct-to-panel gaze measurement instead.
/// </summary>
public readonly record struct GazeReference(float Right, float Up, float Forward)
{
    public static GazeReference None => default;

    public bool IsUsable
    {
        get
        {
            var length = MathF.Sqrt((Right * Right) + (Up * Up) + (Forward * Forward));
            return float.IsFinite(length) && length > 0.99f && length < 1.01f;
        }
    }

    public float Dot(GazeReference other) =>
        (Right * other.Right) + (Up * other.Up) + (Forward * other.Forward);

    /// <summary>
    /// Measures the head-to-panel direction in the HMD's own frame. Sampling
    /// this while the wearer deliberately looks at chat establishes their
    /// preferred gaze centre without making controller or panel placement a
    /// proxy for where they naturally look.
    /// </summary>
    public static bool TryMeasure(
        VrOverlayTransform headPose,
        VrOverlayTransform panelPose,
        out GazeReference direction)
    {
        var head = new Vector3(headPose.M03, headPose.M13, headPose.M23);
        var panel = new Vector3(panelPose.M03, panelPose.M13, panelPose.M23);
        var toPanel = panel - head;
        var length = toPanel.Length();
        if (!float.IsFinite(length) || length < 1e-3f)
        {
            direction = None;
            return false;
        }

        toPanel /= length;
        direction = new GazeReference(
            Vector3.Dot(toPanel, new Vector3(headPose.M00, headPose.M10, headPose.M20)),
            Vector3.Dot(toPanel, new Vector3(headPose.M01, headPose.M11, headPose.M21)),
            Vector3.Dot(toPanel, new Vector3(-headPose.M02, -headPose.M12, -headPose.M22)));
        return direction.IsUsable;
    }

    public static bool TryAverage(Vector3 total, int count, out GazeReference reference)
    {
        if (count <= 0 || !float.IsFinite(total.X) || !float.IsFinite(total.Y) || !float.IsFinite(total.Z))
        {
            reference = None;
            return false;
        }

        var length = total.Length();
        if (!float.IsFinite(length) || length < 1e-3f)
        {
            reference = None;
            return false;
        }

        total /= length;
        reference = new GazeReference(total.X, total.Y, total.Z);
        return reference.IsUsable;
    }

    public static Vector3 ToVector3(GazeReference value) => new(value.Right, value.Up, value.Forward);
}
