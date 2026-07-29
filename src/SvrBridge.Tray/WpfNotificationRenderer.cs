using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// System.Drawing has its own Color and FontFamily and is in scope
// project-wide via the WinForms implicit usings, so WPF's versions of both
// need a name of their own here.
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;

namespace SvrBridge.Tray;

/// <summary>
/// Renders a title and a line of text into a fixed-size panel texture using
/// WPF, per §4 of the notifications plan: real <c>TextWrapping</c> and colour
/// emoji, at the cost of an STA render thread this type owns outright.
/// </summary>
internal sealed class WpfNotificationRenderer : IVrPanelRenderer
{
    public const int PanelWidth = 900;
    public const int PanelHeight = 260;

    private static readonly WpfColor DefaultAccent = WpfColor.FromRgb(96, 200, 255);
    private static readonly WpfColor BackgroundColor = WpfColor.FromArgb(235, 24, 32, 48);
    private static readonly WpfColor TitleColor = Colors.White;
    private static readonly WpfColor BodyColor = WpfColor.FromRgb(210, 220, 235);

    private readonly WpfRenderThread _renderThread;
    private bool _disposed;

    public WpfNotificationRenderer()
    {
        _renderThread = new WpfRenderThread("SteamVR2Bot notification renderer");
    }

    public RenderedPanel Render(NotificationContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderThread.Invoke(() => RenderOnDispatcherThread(content));
    }

    private static RenderedPanel RenderOnDispatcherThread(NotificationContent content)
    {
        var panel = BuildPanel(content);
        var pixels = WpfOverlayPixelPipeline.RenderToRgba(panel, PanelWidth, PanelHeight);
        return new RenderedPanel(pixels, PanelWidth, PanelHeight);
    }

    private static Border BuildPanel(NotificationContent content)
    {
        var accent = TryParseAccent(content.AccentHex) ?? DefaultAccent;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        if (!string.IsNullOrWhiteSpace(content.Title))
        {
            stack.Children.Add(
                new TextBlock
                {
                    Text = content.Title,
                    FontFamily = new WpfFontFamily("Segoe UI"),
                    FontSize = 32,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(TitleColor),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
        }

        stack.Children.Add(
            new TextBlock
            {
                Text = content.Text,
                FontFamily = new WpfFontFamily("Segoe UI"),
                FontSize = 22,
                Foreground = new SolidColorBrush(BodyColor),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

        return new Border
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Background = new SolidColorBrush(BackgroundColor),
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(30),
            Child = stack
        };
    }

    /// <summary>
    /// Parses the <c>#RRGGBB</c> string <see cref="StreamerBotEventPayload"/>
    /// already normalises, treating anything else as "no accent given" rather
    /// than failing the render - a malformed colour must cost the user a
    /// default border, not a dropped notification.
    /// </summary>
    private static WpfColor? TryParseAccent(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return null;
        }

        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return null;
        }

        return WpfColor.FromRgb(r, g, b);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _renderThread.Dispose();
    }
}
