using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal static class VrDashboardRenderer
{
    private static int _imageSequence;
    private static int _oldImagesCleaned;

    public static string Render(IReadOnlyList<ShortcutConfig> shortcuts)
    {
        var path = NextDashboardImagePath();

        using var bitmap = new Bitmap(1400, 900, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(15, 23, 42));

        using var titleFont = new Font("Segoe UI", 38, FontStyle.Bold);
        using var subtitleFont = new Font("Segoe UI", 18);
        using var headingFont = new Font("Segoe UI", 20, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 17);
        using var smallFont = new Font("Segoe UI", 14);
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(180, 194, 214));
        using var blue = new SolidBrush(Color.FromArgb(59, 130, 246));
        using var red = new SolidBrush(Color.FromArgb(220, 38, 38));
        using var green = new SolidBrush(Color.FromArgb(34, 197, 94));
        using var card = new SolidBrush(Color.FromArgb(30, 41, 59));
        using var disabled = new SolidBrush(Color.FromArgb(100, 116, 139));

        DrawTabStrip(graphics, bodyFont, white, blue, card, activeIndex: 0);

        graphics.DrawString("SteamVR2Bot", titleFont, white, 60, 96);
        graphics.DrawString(
            "Your controller shortcuts and what they run",
            subtitleFont,
            muted,
            64,
            158);

        var visible = shortcuts.Take(VrDashboardLayout.ListVisibleRowCount).ToArray();
        if (visible.Length == 0)
        {
            DrawRoundedRectangle(graphics, card, new Rectangle(60, 210, 1280, 170), 18);
            graphics.DrawString(
                "No shortcuts yet",
                headingFont,
                white,
                92,
                245);
            graphics.DrawString(
                "Open SteamVR2Bot on the desktop and choose Add shortcut.",
                bodyFont,
                muted,
                92,
                295);
        }
        else
        {
            var y = VrDashboardLayout.ListRowsStartY;
            foreach (var shortcut in visible)
            {
                DrawRoundedRectangle(
                    graphics,
                    card,
                    new Rectangle(60, y, 1280, 92),
                    16);
                graphics.FillEllipse(
                    shortcut.Enabled ? green : disabled,
                    88,
                    y + 35,
                    18,
                    18);
                DrawEllipsizedText(
                    graphics,
                    shortcut.Name,
                    headingFont,
                    shortcut.Enabled ? white : muted,
                    new RectangleF(128, y + 13, 555, 34));
                DrawEllipsizedText(
                    graphics,
                    shortcut.FriendlyGesture,
                    smallFont,
                    muted,
                    new RectangleF(130, y + 52, 700, 28));
                DrawEllipsizedText(
                    graphics,
                    $"→  {shortcut.ActionName}",
                    bodyFont,
                    shortcut.Enabled ? blue : muted,
                    new RectangleF(700, y + 29, 380, 38));
                DrawRoundedRectangle(
                    graphics,
                    blue,
                    new Rectangle(1100, y + 14, 90, 64),
                    12);
                DrawCenteredText(
                    graphics,
                    "✎",
                    headingFont,
                    white,
                    new Rectangle(1100, y + 14, 90, 64));
                DrawRoundedRectangle(
                    graphics,
                    red,
                    new Rectangle(1210, y + 14, 100, 64),
                    12);
                DrawCenteredText(
                    graphics,
                    "×",
                    headingFont,
                    white,
                    new Rectangle(1210, y + 14, 100, 64));
                y += VrDashboardLayout.ListRowHeight;
            }

            if (shortcuts.Count > visible.Length)
            {
                graphics.DrawString(
                    $"+ {shortcuts.Count - visible.Length} more on the desktop",
                    smallFont,
                    muted,
                    74,
                    y + 4);
            }
        }

        DrawRoundedRectangle(
            graphics,
            blue,
            new Rectangle(60, 800, 1280, 64),
            16);
        graphics.DrawString(
            "Create a new shortcut",
            bodyFont,
            white,
            550,
            817);

        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    public static string RenderActionPicker(VrActionBrowser browser)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            DrawEllipsizedText(
                graphics,
                browser.Title,
                fonts.Title,
                brushes.White,
                new RectangleF(60, 42, 1280, 58));
            graphics.DrawString(
                browser.TotalItemCount == 0
                    ? "No enabled Streamer.bot actions were found."
                    : browser.Subtitle,
                fonts.Subtitle,
                brushes.Muted,
                64,
                104);

            var y = 165;
            foreach (var row in browser.VisibleRows)
            {
                DrawRoundedRectangle(
                    graphics,
                    brushes.Card,
                    new Rectangle(60, y, 1280, 78),
                    14);
                DrawEllipsizedText(
                    graphics,
                    row.Label,
                    fonts.Body,
                    brushes.White,
                    new RectangleF(92, y + (row.Detail is null ? 22 : 9), 1080, 38));
                if (row.Detail is not null)
                {
                    graphics.DrawString(
                        row.Detail,
                        fonts.Small,
                        brushes.Muted,
                        94,
                        y + 43);
                    graphics.DrawString("›", fonts.Heading, brushes.Blue, 1265, y + 19);
                }

                y += 91;
            }

            if (browser.TotalItemCount > 0)
            {
                graphics.DrawString(
                    $"{browser.FirstVisibleItemNumber}–{browser.LastVisibleItemNumber} " +
                    $"of {browser.TotalItemCount}",
                    fonts.Small,
                    brushes.Muted,
                    1180,
                    748);
            }

            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Cancel",
                VrDashboardLayout.ActionPicker[0]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                browser.BackLabel,
                VrDashboardLayout.ActionPicker[1]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                browser.CanScrollUp ? brushes.Blue : brushes.Disabled,
                "↑  Previous",
                VrDashboardLayout.ActionPicker[2]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                browser.CanScrollDown ? brushes.Blue : brushes.Disabled,
                "Next  ↓",
                VrDashboardLayout.ActionPicker[3]);
        });
    }

    public static string RenderGestureTypePicker(bool isEditing)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString(
                isEditing ? "Edit controller shortcut" : "Create a controller shortcut",
                fonts.Title,
                brushes.White,
                60,
                42);
            graphics.DrawString(
                "Choose how the controller input should trigger.",
                fonts.Subtitle,
                brushes.Muted,
                64,
                108);

            (string Label, string Detail)[] choices =
            [
                ("Single Button", "Press one controller input once"),
                ("Button Combo", "Press two different controller inputs together"),
                ("Double Press", "Press the same input twice within a chosen time"),
                ("Long Hold", "Hold one input for a chosen amount of time")
            ];
            var y = 180;
            foreach (var choice in choices)
            {
                DrawRoundedRectangle(
                    graphics,
                    brushes.Card,
                    new Rectangle(60, y, 1280, 110),
                    16);
                graphics.DrawString(
                    choice.Label,
                    fonts.Heading,
                    brushes.White,
                    96,
                    y + 17);
                graphics.DrawString(
                    choice.Detail,
                    fonts.Body,
                    brushes.Muted,
                    98,
                    y + 60);
                graphics.DrawString("›", fonts.Title, brushes.Blue, 1250, y + 24);
                y += 135;
            }

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 1280, 64),
                16);
            DrawCenteredText(
                graphics,
                "Cancel",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 1280, 64));
        });
    }

    /// <summary>
    /// The VR settings page - anchor mode/hand, opacity, size and (chat only)
    /// gaze sensitivity per surface, per §B1/§B4 of the Phase 4b plan. No
    /// bottom bar: every change here applies and saves automatically, the
    /// same as the desktop.
    /// </summary>
    public static string RenderSettings(VrSettingsSnapshot settings)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            // Deliberately no repeated "SteamVR2Bot" title/subtitle here -
            // the tab strip immediately above already establishes "you are
            // in Settings", and the full title collided with the first
            // section heading below it (a real layout bug caught live: the
            // title at the same fixed y the List page uses landed directly
            // on top of "Chat" and the anchor buttons, since this page's
            // rows start much higher than List's do).
            DrawTabStrip(graphics, fonts.Body, brushes.White, brushes.Blue, brushes.Card, activeIndex: 1);

            DrawSurfaceSection(
                graphics,
                fonts,
                brushes,
                "Chat",
                VrDashboardLayout.ChatControlsY,
                settings.ChatEnabled,
                VrDashboardLayout.ChatToggle,
                VrDashboardLayout.ChatAnchorMode,
                VrDashboardLayout.ChatAnchorHand,
                settings.ChatAnchor);
            DrawSlider(
                graphics,
                fonts,
                brushes,
                VrDashboardLayout.ChatOpacityTrack,
                "Opacity",
                $"{Math.Round((settings.ChatOpacity - 0.2) / 0.8 * 100)}%",
                (float)Math.Clamp((settings.ChatOpacity - 0.2) / 0.8, 0, 1));
            DrawSlider(
                graphics,
                fonts,
                brushes,
                VrDashboardLayout.ChatSizeTrack,
                "Size",
                $"{Math.Round(settings.ChatSizeScale * 100)}%",
                (float)Math.Clamp((settings.ChatSizeScale - 0.5) / 1.5, 0, 1));
            DrawSegmented(
                graphics,
                fonts,
                brushes,
                VrDashboardLayout.GazeSensitivity,
                ["Relaxed gaze", "Normal gaze", "Tight gaze"],
                (int)settings.GazeSensitivity,
                enabled: true);

            DrawSurfaceSection(
                graphics,
                fonts,
                brushes,
                "Notifications",
                VrDashboardLayout.NotificationControlsY,
                settings.NotificationsEnabled,
                VrDashboardLayout.NotificationToggle,
                VrDashboardLayout.NotificationAnchorMode,
                VrDashboardLayout.NotificationAnchorHand,
                settings.NotificationAnchor);
            DrawSlider(
                graphics,
                fonts,
                brushes,
                VrDashboardLayout.NotificationOpacityTrack,
                "Opacity",
                $"{Math.Round((settings.NotificationOpacity - 0.2) / 0.8 * 100)}%",
                (float)Math.Clamp((settings.NotificationOpacity - 0.2) / 0.8, 0, 1));
            DrawSlider(
                graphics,
                fonts,
                brushes,
                VrDashboardLayout.NotificationSizeTrack,
                "Size",
                $"{Math.Round(settings.NotificationSizeScale * 100)}%",
                (float)Math.Clamp((settings.NotificationSizeScale - 0.5) / 1.5, 0, 1));
        });
    }

    public static string RenderTolerancePicker(ChordMode mode, int valueMs)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            var doublePress = mode == ChordMode.DoublePress;
            graphics.DrawString(
                doublePress ? "Double-press tolerance" : "Long-hold duration",
                fonts.Title,
                brushes.White,
                60,
                42);
            graphics.DrawString(
                doublePress
                    ? "How much time can pass between the two presses?"
                    : "How long must the input stay held?",
                fonts.Subtitle,
                brushes.Muted,
                64,
                108);

            var valueText = doublePress
                ? $"{valueMs} ms"
                : FriendlyDuration(valueMs);
            DrawCenteredText(
                graphics,
                valueText,
                fonts.Title,
                brushes.White,
                new Rectangle(350, 190, 700, 80));

            const int trackX = 180;
            const int trackY = 370;
            const int trackWidth = 1040;
            var ratio = doublePress
                ? Math.Clamp((valueMs - 200) / 1000f, 0f, 1f)
                : Math.Clamp((valueMs - 500) / 4500f, 0f, 1f);
            var knobX = trackX + (int)(trackWidth * ratio);
            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(trackX, trackY, trackWidth, 22),
                11);
            DrawRoundedRectangle(
                graphics,
                brushes.Blue,
                new Rectangle(trackX, trackY, Math.Max(22, knobX - trackX), 22),
                11);
            graphics.FillEllipse(brushes.White, knobX - 20, trackY - 9, 40, 40);

            graphics.DrawString(
                doublePress ? "200 ms" : "0.5 sec",
                fonts.Small,
                brushes.Muted,
                trackX,
                trackY + 48);
            graphics.DrawString(
                doublePress ? "1.2 sec" : "5 sec",
                fonts.Small,
                brushes.Muted,
                trackX + trackWidth - 70,
                trackY + 48);
            DrawCenteredText(
                graphics,
                "Point or drag, then release to set the value.",
                fonts.Body,
                brushes.Muted,
                new Rectangle(180, 500, 1040, 70));

            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Cancel",
                VrDashboardLayout.Tolerance[0]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Back",
                VrDashboardLayout.Tolerance[1]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Blue,
                "Next: record input",
                VrDashboardLayout.Tolerance[2]);
        });
    }

    public static string RenderInputRecorder(
        ChordMode mode,
        ControllerSetup setup,
        ControllerInputBinding? firstInput)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            var combo = mode == ChordMode.Simultaneous;
            graphics.DrawString(
                combo ? "Record your button combo" : "Record your controller input",
                fonts.Title,
                brushes.White,
                60,
                42);
            graphics.DrawString(
                combo && firstInput is not null
                    ? $"Input 1: {firstInput.FriendlyName} • Choose a different input 2."
                    : combo
                        ? "Pick two inputs below, or close the SteamVR menu and press both. "
                          + "SteamVR2Bot reopens automatically."
                        : "Pick an input below, or close the SteamVR menu and press it. "
                          + "SteamVR2Bot reopens automatically.",
                fonts.Subtitle,
                brushes.Muted,
                64,
                108);

            var left = ControllerInputs.AvailableInputs(ControllerHand.Left, setup);
            var right = ControllerInputs.AvailableInputs(ControllerHand.Right, setup);
            DrawCenteredText(
                graphics,
                "Left controller",
                fonts.Heading,
                brushes.White,
                new Rectangle(60, 138, 620, 40));
            DrawCenteredText(
                graphics,
                "Right controller",
                fonts.Heading,
                brushes.White,
                new Rectangle(720, 138, 620, 40));

            for (var index = 0; index < 6; index++)
            {
                DrawInputChoice(
                    graphics,
                    fonts,
                    brushes,
                    left.ElementAtOrDefault(index),
                    firstInput,
                    new Rectangle(60, 180 + (index * 88), 620, 76));
                DrawInputChoice(
                    graphics,
                    fonts,
                    brushes,
                    right.ElementAtOrDefault(index),
                    firstInput,
                    new Rectangle(720, 180 + (index * 88), 620, 76));
            }

            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Cancel",
                VrDashboardLayout.RecordInput[0]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Back",
                VrDashboardLayout.RecordInput[1]);
        });
    }

    public static string RenderShortcutReview(
        ChordMode mode,
        ControllerInputBinding? firstInput,
        ControllerInputBinding? secondInput,
        StreamerBotAction? action,
        int doublePressWindowMs,
        int holdMs,
        bool isEditing)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString(
                isEditing ? "Review your changes" : "Review and save",
                fonts.Title,
                brushes.White,
                60,
                42);
            graphics.DrawString(
                "Check the recorded inputs before saving.",
                fonts.Subtitle,
                brushes.Muted,
                64,
                108);

            var gesture = mode switch
            {
                ChordMode.SinglePress => "Single Button",
                ChordMode.Simultaneous => "Button Combo",
                ChordMode.DoublePress => $"Double Press • {doublePressWindowMs} ms",
                ChordMode.LongPress => $"Long Hold • {FriendlyDuration(holdMs)}",
                _ => mode.ToString()
            };
            var inputs = mode == ChordMode.Simultaneous
                ? $"Input 1: {firstInput?.FriendlyName ?? "Not recorded"}\n" +
                  $"Input 2: {secondInput?.FriendlyName ?? "Not recorded"}"
                : $"Input: {firstInput?.FriendlyName ?? "Not recorded"}";

            DrawReviewRow(graphics, fonts, brushes, 180, "Gesture", gesture);
            DrawReviewRow(graphics, fonts, brushes, 310, "Recorded inputs", inputs);
            DrawReviewRow(
                graphics,
                fonts,
                brushes,
                470,
                "Streamer.bot action",
                action?.FriendlyName ?? "Not chosen yet");

            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Cancel",
                VrDashboardLayout.Review[0]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                "Record again",
                VrDashboardLayout.Review[1]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                brushes.Disabled,
                action is null ? "Choose action" : "Change action",
                VrDashboardLayout.Review[2]);
            DrawBarButton(
                graphics,
                fonts,
                brushes,
                action is null ? brushes.Disabled : brushes.Blue,
                action is null ? "Choose an action first" : "Save shortcut",
                VrDashboardLayout.Review[3]);
        });
    }

    public static string RenderGesturePicker(string actionName)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString("How should it trigger?", fonts.Title, brushes.White, 60, 42);
            DrawEllipsizedText(
                graphics,
                $"Runs: {actionName}",
                fonts.Subtitle,
                brushes.Muted,
                new RectangleF(64, 108, 1270, 36));

            (string Label, string Detail)[] choices =
            [
                ("Double press one button", "Fast to test; both presses must be within half a second"),
                ("Hold one button for 1 second", "Good for a quick deliberate hold"),
                ("Hold one button for 2 seconds", "Safer against accidental presses"),
                ("Hold one button for 3 seconds", "Most deliberate single-button option"),
                ("Hold one, then press another", "A two-button safety shortcut"),
                ("Press two buttons together", "Both inputs must be pressed close together")
            ];
            var y = 165;
            foreach (var choice in choices)
            {
                DrawRoundedRectangle(
                    graphics,
                    brushes.Card,
                    new Rectangle(60, y, 1280, 78),
                    14);
                DrawEllipsizedText(
                    graphics,
                    choice.Label,
                    fonts.Body,
                    brushes.White,
                    new RectangleF(92, y + 7, 1120, 34));
                graphics.DrawString(
                    choice.Detail,
                    fonts.Small,
                    brushes.Muted,
                    94,
                    y + 40);
                graphics.DrawString("›", fonts.Heading, brushes.Blue, 1265, y + 19);
                y += 90;
            }

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 1280, 64),
                16);
            DrawCenteredText(
                graphics,
                "Back to button choices",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 1280, 64));
        });
    }

    public static string RenderQuickInputPicker(
        string actionName,
        IReadOnlyList<ControllerInputBinding> inputs,
        bool isEditing = false)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString(
                isEditing
                    ? "Edit your controller shortcut"
                    : "Choose your controller shortcut",
                fonts.Title,
                brushes.White,
                60,
                42);
            DrawEllipsizedText(
                graphics,
                $"Runs: {actionName}",
                fonts.Subtitle,
                brushes.Muted,
                new RectangleF(64, 108, 1270, 36));

            var y = 165;
            foreach (var input in inputs.Take(6))
            {
                DrawRoundedRectangle(
                    graphics,
                    brushes.Card,
                    new Rectangle(60, y, 1280, 78),
                    14);
                DrawEllipsizedText(
                    graphics,
                    input.FriendlyName,
                    fonts.Body,
                    brushes.White,
                    new RectangleF(92, y + 22, 390, 36));
                DrawRoundedRectangle(
                    graphics,
                    brushes.Blue,
                    new Rectangle(500, y + 10, 380, 58),
                    12);
                DrawCenteredText(
                    graphics,
                    "Double press",
                    fonts.Body,
                    brushes.White,
                    new Rectangle(500, y + 10, 380, 58));
                DrawRoundedRectangle(
                    graphics,
                    brushes.Disabled,
                    new Rectangle(900, y + 10, 410, 58),
                    12);
                DrawCenteredText(
                    graphics,
                    "Hold 2 sec",
                    fonts.Body,
                    brushes.White,
                    new Rectangle(900, y + 10, 410, 58));
                y += 91;
            }

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 350, 64),
                16);
            DrawCenteredText(
                graphics,
                isEditing ? "Cancel edit" : "Back",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 350, 64));
            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(430, 800, 440, 64),
                16);
            DrawCenteredText(
                graphics,
                "Change action",
                fonts.Body,
                brushes.White,
                new Rectangle(430, 800, 440, 64));
            DrawRoundedRectangle(
                graphics,
                brushes.Blue,
                new Rectangle(890, 800, 450, 64),
                16);
            DrawCenteredText(
                graphics,
                "More shortcut options",
                fonts.Body,
                brushes.White,
                new Rectangle(890, 800, 450, 64));
        });
    }

    public static string RenderHandPicker(
        string controllerFamily,
        string? firstInput = null)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString(
                firstInput is null
                    ? "Choose a controller"
                    : "Choose the second controller",
                fonts.Title,
                brushes.White,
                60,
                42);
            DrawEllipsizedText(
                graphics,
                firstInput is null
                    ? controllerFamily
                    : $"First input: {firstInput}",
                fonts.Subtitle,
                brushes.Muted,
                new RectangleF(64, 108, 1270, 36));

            DrawRoundedRectangle(
                graphics,
                brushes.Card,
                new Rectangle(60, 230, 1280, 180),
                20);
            graphics.DrawString("L", fonts.Title, brushes.Blue, 150, 282);
            graphics.DrawString(
                "Left controller",
                fonts.Heading,
                brushes.White,
                245,
                300);
            graphics.DrawString("›", fonts.Title, brushes.Blue, 1250, 282);

            DrawRoundedRectangle(
                graphics,
                brushes.Card,
                new Rectangle(60, 430, 1280, 180),
                20);
            graphics.DrawString("R", fonts.Title, brushes.Blue, 150, 482);
            graphics.DrawString(
                "Right controller",
                fonts.Heading,
                brushes.White,
                245,
                500);
            graphics.DrawString("›", fonts.Title, brushes.Blue, 1250, 482);

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 1280, 64),
                16);
            DrawCenteredText(
                graphics,
                "Back",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 1280, 64));
        });
    }

    public static string RenderButtonPicker(
        ControllerHand hand,
        IReadOnlyList<ControllerInputBinding> inputs,
        string? firstInput = null)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            var handName = hand == ControllerHand.Left ? "left" : "right";
            graphics.DrawString(
                $"Choose a {handName} button",
                fonts.Title,
                brushes.White,
                60,
                42);
            graphics.DrawString(
                firstInput is null
                    ? "Select the physical input from the list."
                    : $"First input: {firstInput}",
                fonts.Subtitle,
                brushes.Muted,
                64,
                108);

            var y = 165;
            foreach (var input in inputs.Take(6))
            {
                DrawRoundedRectangle(
                    graphics,
                    brushes.Card,
                    new Rectangle(60, y, 1280, 78),
                    14);
                graphics.DrawString(
                    input.FriendlyName,
                    fonts.Body,
                    brushes.White,
                    92,
                    y + 22);
                graphics.DrawString("›", fonts.Heading, brushes.Blue, 1265, y + 19);
                y += 91;
            }

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 1280, 64),
                16);
            DrawCenteredText(
                graphics,
                "Back to controllers",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 1280, 64));
        });
    }

    private static void DrawReviewRow(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        int y,
        string label,
        string value)
    {
        var height = label == "Recorded inputs" ? 140 : 110;
        DrawRoundedRectangle(
            graphics,
            brushes.Card,
            new Rectangle(60, y, 1280, height),
            16);
        graphics.DrawString(label, fonts.Small, brushes.Muted, 94, y + 14);
        graphics.DrawString(value, fonts.Heading, brushes.White, 92, y + 45);
    }

    private static void DrawInputChoice(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        ControllerInputBinding? input,
        ControllerInputBinding? selected,
        Rectangle bounds)
    {
        if (input is null)
        {
            return;
        }

        var isSelected = selected?.Id.Equals(
            input.Id,
            StringComparison.OrdinalIgnoreCase) == true;
        DrawRoundedRectangle(
            graphics,
            isSelected ? brushes.Blue : brushes.Card,
            bounds,
            14);
        DrawEllipsizedText(
            graphics,
            $"{(isSelected ? "✓  " : "")}{input.FriendlyName}",
            fonts.Body,
            brushes.White,
            new RectangleF(
                bounds.X + 30,
                bounds.Y + 21,
                bounds.Width - 60,
                38));
    }

    private static string FriendlyDuration(int milliseconds) =>
        milliseconds % 1000 == 0
            ? $"{milliseconds / 1000} second{(milliseconds == 1000 ? "" : "s")}"
            : $"{milliseconds / 1000d:0.##} seconds";

    private static string RenderSimplePage(
        Action<Graphics, DashboardFonts, DashboardBrushes> draw)
    {
        var path = NextDashboardImagePath();
        using var bitmap = new Bitmap(1400, 900, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint =
            System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.Clear(Color.FromArgb(15, 23, 42));
        using var fonts = new DashboardFonts();
        using var brushes = new DashboardBrushes();
        draw(graphics, fonts, brushes);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static string NextDashboardImagePath()
    {
        var directory = AppPaths.DataDirectory;
        Directory.CreateDirectory(directory);
        CleanupOldDashboardImages(directory);
        var sequence = Interlocked.Increment(ref _imageSequence);
        return Path.Combine(
            directory,
            $"vr-dashboard-{Environment.ProcessId}-{sequence}.png");
    }

    private static void CleanupOldDashboardImages(string directory)
    {
        if (Interlocked.Exchange(ref _oldImagesCleaned, 1) != 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-6);
        foreach (var path in Directory.EnumerateFiles(
                     directory,
                     "vr-dashboard-*.png",
                     SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // SteamVR may still be finishing an asynchronous image load.
            }
            catch (UnauthorizedAccessException)
            {
                // A stale frame is harmless and can be retried next launch.
            }
        }
    }

    private static void DrawRoundedRectangle(
        Graphics graphics,
        Brush brush,
        Rectangle rectangle,
        int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(
            rectangle.Right - diameter,
            rectangle.Bottom - diameter,
            diameter,
            diameter,
            0,
            90);
        path.AddArc(
            rectangle.Left,
            rectangle.Bottom - diameter,
            diameter,
            diameter,
            90,
            90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    /// <summary>
    /// Draws one bottom-bar button using the shared layout rectangle, so the
    /// drawn button and the click handler cannot disagree about where it is.
    /// </summary>
    private static void DrawBarButton(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        Brush fill,
        string label,
        Rectangle bounds)
    {
        DrawRoundedRectangle(graphics, fill, bounds, 16);
        DrawCenteredText(graphics, label, fonts.Body, brushes.White, bounds);
    }

    private static void DrawCenteredText(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        Rectangle bounds)
    {
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    /// <summary>Peer navigation between the Shortcuts and Settings pages - see <see cref="VrDashboardLayout.Tabs"/>.</summary>
    private static void DrawTabStrip(
        Graphics graphics,
        Font font,
        Brush white,
        Brush blue,
        Brush card,
        int activeIndex)
    {
        string[] labels = ["Shortcuts", "Settings"];
        for (var index = 0; index < VrDashboardLayout.Tabs.Length; index++)
        {
            var rectangle = VrDashboardLayout.Tabs[index];
            DrawRoundedRectangle(graphics, index == activeIndex ? blue : card, rectangle, 14);
            DrawCenteredText(graphics, labels[index], font, white, rectangle);
        }
    }

    /// <summary>One surface's row on the settings page: heading, anchor mode/hand segmented controls, and an on/off toggle.</summary>
    private static void DrawSurfaceSection(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        string title,
        int rowY,
        bool enabled,
        Rectangle toggle,
        Rectangle[] anchorMode,
        Rectangle[] anchorHand,
        OverlayAnchor anchor)
    {
        graphics.DrawString(title, fonts.Heading, brushes.White, 60, rowY - 34);

        var modeIndex = anchor.Mode == OverlayAnchorMode.Head ? 1 : 0;
        DrawSegmented(graphics, fonts, brushes, anchorMode, ["Controller", "Headset"], modeIndex, enabled: true);

        // Which hand only means anything in Controller mode - drawn disabled
        // rather than hidden, so its position on the page never moves.
        var handIndex = anchor.Hand == OverlayAnchorHand.Right ? 1 : 0;
        var handEnabled = anchor.Mode == OverlayAnchorMode.Controller;
        DrawSegmented(graphics, fonts, brushes, anchorHand, ["Left hand", "Right hand"], handIndex, handEnabled);

        DrawRoundedRectangle(graphics, enabled ? brushes.Green : brushes.Disabled, toggle, 14);
        DrawCenteredText(graphics, enabled ? "On" : "Off", fonts.Heading, brushes.White, toggle);
    }

    /// <summary>A row of 2-4 adjacent buttons, one highlighted as the current choice - see §B4 of the Phase 4b plan.</summary>
    private static void DrawSegmented(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        IReadOnlyList<Rectangle> rectangles,
        IReadOnlyList<string> labels,
        int selectedIndex,
        bool enabled)
    {
        for (var index = 0; index < rectangles.Count; index++)
        {
            var rectangle = rectangles[index];
            var isSelected = index == selectedIndex;
            var fill = !enabled ? brushes.Disabled : isSelected ? brushes.Blue : brushes.Card;
            DrawRoundedRectangle(graphics, fill, rectangle, 14);
            DrawCenteredText(graphics, labels[index], fonts.Body, brushes.White, rectangle);
        }
    }

    /// <summary>
    /// A click-to-position slider drawn inside a hit region far taller than
    /// the visible track, reusing the shape proven by the tolerance picker -
    /// see <see cref="RenderTolerancePicker"/> and §B4 of the Phase 4b plan.
    /// </summary>
    private static void DrawSlider(
        Graphics graphics,
        DashboardFonts fonts,
        DashboardBrushes brushes,
        Rectangle hitRegion,
        string label,
        string valueText,
        float ratio)
    {
        graphics.DrawString($"{label}: {valueText}", fonts.Body, brushes.White, hitRegion.Left, hitRegion.Top);

        const int trackHeight = 18;
        var trackX = hitRegion.Left + 10;
        var trackY = hitRegion.Top + 52;
        var trackWidth = hitRegion.Width - 20;
        var knobX = trackX + (int)(trackWidth * ratio);

        DrawRoundedRectangle(
            graphics,
            brushes.Disabled,
            new Rectangle(trackX, trackY, trackWidth, trackHeight),
            trackHeight / 2);
        DrawRoundedRectangle(
            graphics,
            brushes.Blue,
            new Rectangle(trackX, trackY, Math.Max(trackHeight, knobX - trackX), trackHeight),
            trackHeight / 2);
        graphics.FillEllipse(brushes.White, knobX - 14, trackY - 5, 28, 28);
    }

    private static void DrawEllipsizedText(
        Graphics graphics,
        string text,
        Font font,
        Brush brush,
        RectangleF bounds)
    {
        using var format = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        graphics.DrawString(text, font, brush, bounds, format);
    }

    private sealed class DashboardFonts : IDisposable
    {
        public Font Title { get; } = new("Segoe UI", 38, FontStyle.Bold);
        public Font Subtitle { get; } = new("Segoe UI", 18);
        public Font Heading { get; } = new("Segoe UI", 20, FontStyle.Bold);
        public Font Body { get; } = new("Segoe UI", 17);
        public Font Small { get; } = new("Segoe UI", 14);

        public void Dispose()
        {
            Title.Dispose();
            Subtitle.Dispose();
            Heading.Dispose();
            Body.Dispose();
            Small.Dispose();
        }
    }

    private sealed class DashboardBrushes : IDisposable
    {
        public Brush White { get; } = new SolidBrush(Color.White);
        public Brush Muted { get; } = new SolidBrush(Color.FromArgb(180, 194, 214));
        public Brush Blue { get; } = new SolidBrush(Color.FromArgb(59, 130, 246));
        public Brush Green { get; } = new SolidBrush(Color.FromArgb(34, 197, 94));
        public Brush Card { get; } = new SolidBrush(Color.FromArgb(30, 41, 59));
        public Brush Disabled { get; } = new SolidBrush(Color.FromArgb(100, 116, 139));

        public void Dispose()
        {
            White.Dispose();
            Muted.Dispose();
            Blue.Dispose();
            Green.Dispose();
            Card.Dispose();
            Disabled.Dispose();
        }
    }
}
