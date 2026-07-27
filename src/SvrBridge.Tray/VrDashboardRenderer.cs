using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal static class VrDashboardRenderer
{
    public static string Render(IReadOnlyList<ShortcutConfig> shortcuts)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVR Bridge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vr-dashboard.png");

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
                graphics.DrawString(
                    shortcut.Name,
                    headingFont,
                    shortcut.Enabled ? white : muted,
                    128,
                    y + 13);
                graphics.DrawString(
                    shortcut.FriendlyGesture,
                    smallFont,
                    muted,
                    130,
                    y + 52);
                graphics.DrawString(
                    $"→  {shortcut.ActionName}",
                    bodyFont,
                    shortcut.Enabled ? blue : muted,
                    900,
                    y + 29);
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
            "Record a new shortcut",
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

    public static string RenderRecording(string? firstInput = null)
    {
        return RenderSimplePage((graphics, fonts, brushes) =>
        {
            graphics.DrawString("Record controller inputs", fonts.Title, brushes.White, 60, 42);
            graphics.DrawString(
                firstInput is null
                    ? "Release all buttons, then hold your safety input."
                    : $"{firstInput} recorded — keep holding it and press the action input.",
                fonts.Subtitle,
                brushes.Muted,
                64,
                115);

            DrawRoundedRectangle(
                graphics,
                brushes.Card,
                new Rectangle(150, 250, 1100, 330),
                24);
            graphics.DrawString(
                firstInput is null ? "1" : "✓",
                fonts.Title,
                firstInput is null ? brushes.Blue : brushes.Green,
                255,
                350);
            graphics.DrawString(
                firstInput is null ? "Hold safety input" : firstInput,
                fonts.Heading,
                brushes.White,
                330,
                355);
            graphics.DrawString("2", fonts.Title, brushes.Blue, 760, 350);
            graphics.DrawString(
                "Press action input",
                fonts.Heading,
                brushes.White,
                835,
                355);

            DrawRoundedRectangle(
                graphics,
                brushes.Disabled,
                new Rectangle(60, 800, 1280, 64),
                16);
            graphics.DrawString("Cancel", fonts.Body, brushes.White, 655, 817);
        });
    }

    private static string RenderSimplePage(
        Action<Graphics, DashboardFonts, DashboardBrushes> draw)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SVR Bridge");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vr-dashboard.png");
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
