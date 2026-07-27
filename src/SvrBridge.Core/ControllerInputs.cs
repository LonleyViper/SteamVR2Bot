using System.Numerics;

namespace SvrBridge.Core;

public static class ControllerInputs
{
    public static IReadOnlyList<ControllerInputBinding> PressedInputs(
        InputSnapshot snapshot,
        ControllerSetup setup)
    {
        var result = new List<ControllerInputBinding>();
        AddPressed(result, ControllerHand.Left, snapshot.LeftButtons, setup);
        AddPressed(result, ControllerHand.Right, snapshot.RightButtons, setup);
        return result;
    }

    public static string FriendlyName(
        ControllerHand hand,
        uint button,
        ControllerSetup setup)
    {
        var handName = hand == ControllerHand.Left ? "Left" : "Right";
        var controllerType = setup.Controllers
            .FirstOrDefault(controller =>
                controller.Hand.Equals(handName, StringComparison.OrdinalIgnoreCase))
            ?.ControllerType
            .ToLowerInvariant() ?? "";

        var inputName = button switch
        {
            0 => "System Button",
            1 => "Menu Button",
            2 => "Grip",
            7 => hand == ControllerHand.Left ? "X Button" : "A Button",
            32 when controllerType.Contains("vive") => "Trackpad",
            32 => "Thumbstick / Trackpad",
            33 => "Trigger",
            34 => hand == ControllerHand.Left ? "Y Button" : "B Button",
            _ => $"Button {button}"
        };
        return $"{handName} {inputName}";
    }

    public static string ControllerFamily(ControllerSetup setup) =>
        setup.Controllers
            .Select(controller => controller.FriendlyName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault() ?? "VR controller";

    private static void AddPressed(
        ICollection<ControllerInputBinding> result,
        ControllerHand hand,
        ulong buttons,
        ControllerSetup setup)
    {
        while (buttons != 0)
        {
            var button = (uint)BitOperations.TrailingZeroCount(buttons);
            result.Add(
                ControllerInputBinding.Physical(
                    hand,
                    button,
                    FriendlyName(hand, button, setup)));
            buttons &= buttons - 1;
        }
    }
}

public enum DashboardInteractionKind
{
    None,
    Click,
    Scroll
}

public readonly record struct DashboardInteraction(
    DashboardInteractionKind Kind,
    float X,
    float Y,
    float ScrollY)
{
    public static DashboardInteraction Click(float x, float y) =>
        new(DashboardInteractionKind.Click, x, y, 0);

    public static DashboardInteraction Scroll(float deltaY) =>
        new(DashboardInteractionKind.Scroll, 0, 0, deltaY);
}

public sealed class DashboardPointerTracker
{
    private float _x;
    private float _y;

    public bool Update(
        int eventType,
        float eventX,
        float eventY,
        out DashboardInteraction interaction)
    {
        interaction = default;
        if (eventType == 300) // VREvent_MouseMove
        {
            _x = eventX;
            _y = eventY;
            return false;
        }

        if (eventType == 301) // VREvent_MouseButtonDown
        {
            interaction = DashboardInteraction.Click(_x, _y);
            return true;
        }

        if (eventType == 305 && eventY != 0) // VREvent_ScrollDiscrete
        {
            interaction = DashboardInteraction.Scroll(eventY);
            return true;
        }

        return false;
    }
}
