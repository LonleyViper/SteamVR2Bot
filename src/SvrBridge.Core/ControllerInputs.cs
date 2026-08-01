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
            7 when controllerType == "knuckles" => "A Button",
            7 when controllerType == "oculus_touch" =>
                hand == ControllerHand.Left ? "X Button" : "A Button",
            7 => "Button 7",
            32 when controllerType == "vive_controller" => "Trackpad",
            32 when controllerType is "knuckles" or "oculus_touch" => "Thumbstick",
            32 => "Thumbstick / Trackpad",
            33 => "Trigger",
            34 when controllerType == "knuckles" => "B Button",
            34 when controllerType == "oculus_touch" =>
                hand == ControllerHand.Left ? "Y Button" : "B Button",
            34 => "Button 34",
            _ => $"Button {button}"
        };
        return $"{handName} {inputName}";
    }

    public static string ControllerFamily(ControllerSetup setup) =>
        setup.Controllers
            .Select(controller => controller.FriendlyName)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault() ?? "VR controller";

    /// <summary>
    /// The physical inputs the in-VR/desktop picker offers for one hand, in a
    /// fixed per-family order (menu, grip, trigger, trackpad/thumbstick, then
    /// any face buttons) so the list never reshuffles as controllers wake up
    /// or swap roles.
    /// <para>
    /// Two families are asymmetric between hands, which is why this takes a
    /// <paramref name="hand"/> rather than deciding once for both:
    /// </para>
    /// <list type="bullet">
    /// <item>Index has no application-menu input at all - real Knuckles
    /// hardware exposes no <c>/input/application_menu</c> path, so there is
    /// nothing to offer on either hand.</item>
    /// <item>Touch exposes its menu (three-line "hamburger") button only on
    /// the left controller. The right controller's equivalent position is
    /// the Oculus/system button, which the runtime reserves for itself and
    /// never hands to an application - offering it here would produce a
    /// selectable input that can never actually be recorded.</item>
    /// </list>
    /// <para>
    /// An unrecognised controller type gets the conservative grip/trigger/
    /// stick set only: those three exist in some form on effectively every
    /// motion controller, whereas a face-button pair or a menu button is not
    /// safe to assume.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ControllerInputBinding> AvailableInputs(
        ControllerHand hand,
        ControllerSetup setup)
    {
        var handName = hand == ControllerHand.Left ? "Left" : "Right";
        var controllerType = setup.Controllers
            .FirstOrDefault(controller =>
                controller.Hand.Equals(handName, StringComparison.OrdinalIgnoreCase))
            ?.ControllerType
            .ToLowerInvariant() ?? "";

        uint[] buttons = controllerType switch
        {
            "vive_controller" => [1, 2, 33, 32],
            "knuckles" => [2, 33, 32, 7, 34],
            "oculus_touch" when hand == ControllerHand.Left => [1, 2, 33, 32, 7, 34],
            "oculus_touch" => [2, 33, 32, 7, 34],
            _ => [2, 33, 32]
        };

        return buttons
            .Select(button => ControllerInputBinding.Physical(
                hand,
                button,
                FriendlyName(hand, button, setup)))
            .ToArray();
    }

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
    private bool _pressed;

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
            _pressed = true;
            return false;
        }

        if (eventType == 302 && _pressed) // VREvent_MouseButtonUp
        {
            _pressed = false;
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
