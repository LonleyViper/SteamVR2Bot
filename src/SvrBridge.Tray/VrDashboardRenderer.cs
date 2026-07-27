using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal static class VrDashboardRenderer
{
    private static int _imageSequence;

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

        graphics.DrawString("SVR Bridge", titleFont, white, 60, 42);
        graphics.DrawString(
            "Your controller shortcuts and what they run",
            subtitleFont,
            muted,
            64,
            104);

        var visible = shortcuts.Take(6).ToArray();
        if (visible.Length == 0)
        {
            DrawRoundedRectangle(graphics, card, new Rectangle(60, 170, 1280, 170), 18);
            graphics.DrawString(
                "No shortcuts yet",
                headingFont,
                white,
                92,
                205);
            graphics.DrawString(
                "Open SVR Bridge on the desktop and choose Add shortcut.",
                bodyFont,
                muted,
                92,
                255);
        }
        else
        {
            var y = 160;
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
                y += 105;
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

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 360, 64),
                16);
            DrawCenteredText(
                graphics,
                browser.BackLabel,
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 360, 64));
            DrawRoundedRectangle(
                graphics,
                browser.CanScrollUp ? brushes.Blue : brushes.Disabled,
                new Rectangle(440, 800, 430, 64),
                16);
            DrawCenteredText(
                graphics,
                "↑  Previous",
                fonts.Body,
                brushes.White,
                new Rectangle(440, 800, 430, 64));
            DrawRoundedRectangle(
                graphics,
                browser.CanScrollDown ? brushes.Blue : brushes.Disabled,
                new Rectangle(890, 800, 450, 64),
                16);
            DrawCenteredText(
                graphics,
                "Next  ↓",
                fonts.Body,
                brushes.White,
                new Rectangle(890, 800, 450, 64));
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
                "Point anywhere on the slider and click to set the value.",
                fonts.Body,
                brushes.Muted,
                new Rectangle(180, 500, 1040, 70));

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 520, 64),
                16);
            DrawCenteredText(
                graphics,
                "Back",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 520, 64));
            DrawRoundedRectangle(
                graphics,
                brushes.Blue,
                new Rectangle(600, 800, 740, 64),
                16);
            DrawCenteredText(
                graphics,
                "Next: record input",
                fonts.Body,
                brushes.White,
                new Rectangle(600, 800, 740, 64));
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
                    : "Choose from the list without closing the SteamVR menu.",
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

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 320, 64),
                16);
            DrawCenteredText(
                graphics,
                "Record again",
                fonts.Body,
                brushes.White,
                new Rectangle(60, 800, 320, 64));
            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(400, 800, 510, 64),
                16);
            DrawCenteredText(
                graphics,
                action is null ? "Choose action" : "Change action",
                fonts.Body,
                brushes.White,
                new Rectangle(400, 800, 510, 64));
            DrawRoundedRectangle(
                graphics,
                action is null ? brushes.Disabled : brushes.Blue,
                new Rectangle(930, 800, 410, 64),
                16);
            DrawCenteredText(
                graphics,
                action is null ? "Choose an action first" : "Save shortcut",
                fonts.Body,
                brushes.White,
                new Rectangle(930, 800, 410, 64));
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
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVR Bridge");
        Directory.CreateDirectory(directory);
        var slot = (uint)Interlocked.Increment(ref _imageSequence) % 8;
        return Path.Combine(directory, $"vr-dashboard-{slot}.png");
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
