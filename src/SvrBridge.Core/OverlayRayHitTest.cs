using System.Numerics;

namespace SvrBridge.Core;

/// <summary>
/// Tests whether a controller's SteamVR laser ray reaches an overlay panel.
/// SteamVR emits overlay hover events only after globally enabling laser input,
/// so this is the narrow pre-hover gate that keeps a regular game in control
/// until its controller is already pointing at the panel.
/// </summary>
public static class OverlayRayHitTest
{
    private const float ParallelTolerance = 0.0001f;

    /// <summary>
    /// Whether the controller's local forward direction (-Z) intersects the
    /// front of the rectangular panel. Panel dimensions are in metres.
    /// </summary>
    public static bool IntersectsPanel(
        VrOverlayTransform controllerPose,
        VrOverlayTransform panelPose,
        float panelWidthMeters,
        float panelHeightMeters)
    {
        if (!controllerPose.IsUsable()
            || !panelPose.IsUsable()
            || !float.IsFinite(panelWidthMeters)
            || !float.IsFinite(panelHeightMeters)
            || panelWidthMeters <= 0f
            || panelHeightMeters <= 0f)
        {
            return false;
        }

        var panelFromWorld = panelPose.InverseRigid();
        var origin = panelFromWorld.TransformPoint(
            controllerPose.TransformPoint(Vector3.Zero));
        var direction = panelFromWorld.TransformDirection(
            controllerPose.TransformDirection(-Vector3.UnitZ));
        if (MathF.Abs(direction.Z) < ParallelTolerance)
        {
            return false;
        }

        // The panel lies in local Z=0. A negative distance would mean the
        // controller is pointing away from it, which must never arm input.
        var distance = -origin.Z / direction.Z;
        if (distance < 0f)
        {
            return false;
        }

        var hit = origin + (distance * direction);
        return MathF.Abs(hit.X) <= panelWidthMeters / 2f
               && MathF.Abs(hit.Y) <= panelHeightMeters / 2f;
    }
}
