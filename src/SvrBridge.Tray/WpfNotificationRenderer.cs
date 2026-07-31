using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// <para>
/// Since Phase 7 this also composites an optional user-supplied PNG template
/// behind the text (§B5) and honours configurable background/text/accent
/// colours (§B4) - both resolved to hex strings by the caller before they
/// ever reach here, per <see cref="NotificationContent"/>'s own remarks.
/// </para>
/// </summary>
internal sealed class WpfNotificationRenderer : IVrPanelRenderer<NotificationContent>
{
    public const int PanelWidth = 900;
    public const int PanelHeight = 260;

    /// <summary>Exactly this class's own pre-Phase-7 hardcoded colours, exposed so <c>NotificationOverlay</c>'s settings default to them.</summary>
    public const string DefaultBackgroundHex = "#182030";

    /// <inheritdoc cref="DefaultBackgroundHex"/>
    public const string DefaultTextHex = "#FFFFFF";

    /// <inheritdoc cref="DefaultBackgroundHex"/>
    public const string DefaultAccentHex = "#60C8FF";

    /// <summary>
    /// Twice the panel's own pixel width. Generous enough that a template
    /// still looks crisp after WPF's own <c>Stretch.Uniform</c> fit, but
    /// bounded so a user dropping in a raw 6000x4000 photo does not cache
    /// megabytes per session for a panel that will only ever show it at
    /// 900x260 or smaller. Only applied when the source actually exceeds it -
    /// see <see cref="LoadTemplate"/> - so a small logo is never upscaled.
    /// </summary>
    /// <summary>Internal rather than private so the self-test proving an oversized template is downscaled can reference the exact bound.</summary>
    internal const int MaxDecodePixelWidth = PanelWidth * 2;

    private static readonly WpfColor DefaultAccent = WpfColor.FromRgb(0x60, 0xC8, 0xFF);

    // Opaque - the background's own alpha now comes from content.BackgroundOpacity
    // via the brush's Opacity, not baked into the colour, so this colour and
    // the opacity setting can vary independently.
    private static readonly WpfColor DefaultBackgroundColor = WpfColor.FromRgb(0x18, 0x20, 0x30);
    private static readonly WpfColor DefaultTitle = Colors.White;
    private static readonly WpfColor DefaultBody = WpfColor.FromRgb(210, 220, 235);

    private readonly WpfRenderThread _renderThread;

    // A one-entry cache, not a dictionary: a session realistically configures
    // one template at a time, and re-checking the path on every render is
    // cheap enough that there is nothing to gain from caching more than the
    // last one used. A failed load is never cached - see LoadTemplate's own
    // remarks - so fixing a broken file takes effect on the very next
    // notification rather than needing a worker restart.
    private string? _cachedTemplatePath;
    private BitmapSource? _cachedTemplateImage;
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

    private RenderedPanel RenderOnDispatcherThread(NotificationContent content)
    {
        var panel = BuildPanel(content);
        var pixels = WpfOverlayPixelPipeline.RenderToRgba(panel, PanelWidth, PanelHeight);
        return new RenderedPanel(pixels, PanelWidth, PanelHeight);
    }

    private Border BuildPanel(NotificationContent content)
    {
        var accent = WpfColourParsing.TryParse(content.AccentHex) ?? DefaultAccent;
        var background = WpfColourParsing.TryParse(content.BackgroundHex) ?? DefaultBackgroundColor;
        var backgroundOpacity = Math.Clamp(content.BackgroundOpacity, 0d, 1d);
        var textRgb = WpfColourParsing.TryParse(content.TextHex);
        var titleColor = textRgb ?? DefaultTitle;
        var bodyColor = textRgb is { } customText
            ? WpfColor.FromArgb(220, customText.R, customText.G, customText.B)
            : DefaultBody;

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
                    Foreground = new SolidColorBrush(titleColor),
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
                Foreground = new SolidColorBrush(bodyColor),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            });

        var template = TryGetTemplateImage(content.TemplatePath);

        // No scrim at all, over a template or otherwise. An earlier version
        // of this drew a fixed, then an opacity-scaled, dark box behind the
        // text specifically to protect legibility over an unpredictable
        // image - live testing rejected both: a wearer supplying their own
        // template wants exactly what they configured (background opacity,
        // text colour) and nothing this renderer adds on top uninvited.
        // Legibility over a bad template is now entirely the wearer's own
        // choice of text colour and background opacity, the same as it
        // always was for the flat-colour case.
        FrameworkElement contentPanel = stack;

        // Opacity lives on the brush, not baked into the colour's alpha
        // channel, so the same value applies uniformly whether the
        // background is a flat colour or a template image - and because the
        // root element this renders has no opaque backdrop of its own (see
        // WpfOverlayPixelPipeline), anywhere this brush is less than fully
        // opaque genuinely shows through to whatever is behind the overlay
        // in the real world, not just a darker shade of the panel colour.
        System.Windows.Media.Brush backgroundBrush = template is null
            ? new SolidColorBrush(background) { Opacity = backgroundOpacity }
            : new ImageBrush(template) { Stretch = Stretch.Uniform, Opacity = backgroundOpacity };

        return new Border
        {
            Width = PanelWidth,
            Height = PanelHeight,
            // Stretch=Uniform is WPF's own fit-with-letterbox: it scales the
            // template down (never up past its own resolution) to fit inside
            // the panel while preserving its aspect ratio, centring the
            // result - chosen deliberately over Fill/UniformToFill, which
            // would either distort an arbitrary user image or crop it, and
            // neither is acceptable for something the user hand-picked. See
            // §B5 of the Phase 7 plan.
            Background = backgroundBrush,
            // Border clips its own Background to this radius automatically -
            // no separate clipping geometry needed - and the root element
            // being otherwise transparent (see WpfOverlayPixelPipeline) means
            // the clipped-away corners render as genuinely transparent
            // pixels on the overlay quad, not a visible square edge.
            CornerRadius = new CornerRadius(Math.Max(0, content.CornerRadiusPixels)),
            // The accent stripe is a flat-colour-panel decoration; a
            // template image is the wearer's own art, so it is hidden
            // entirely rather than drawn over it uninvited - the same
            // "nothing added on top of a template" rule the scrim follows.
            BorderBrush = template is null ? new SolidColorBrush(accent) : System.Windows.Media.Brushes.Transparent,
            BorderThickness = template is null ? new Thickness(0, 0, 0, 6) : new Thickness(0),
            Padding = new Thickness(30),
            Child = contentPanel
        };
    }

    /// <summary>
    /// Resolves the cached decoded template for <paramref name="path"/>, or
    /// (re)loads it when the path changed. Never throws - see
    /// <see cref="LoadTemplate"/> - so a malformed, truncated or missing file
    /// costs a notification only its background image, never the render
    /// thread itself. Runs on the WPF dispatcher thread the same as
    /// everything else in this class, so no cross-thread handoff is needed
    /// beyond the <see cref="BitmapImage.Freeze"/> that already makes the
    /// result safe if it is ever read from elsewhere.
    /// </summary>
    private BitmapSource? TryGetTemplateImage(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            _cachedTemplatePath = null;
            _cachedTemplateImage = null;
            return null;
        }

        if (_cachedTemplatePath == path && _cachedTemplateImage is not null)
        {
            return _cachedTemplateImage;
        }

        var loaded = LoadTemplate(path);
        _cachedTemplatePath = path;
        _cachedTemplateImage = loaded;
        return loaded;
    }

    /// <summary>
    /// Loads and decodes one template PNG, downscaling it when it is larger
    /// than this panel could ever usefully show - see
    /// <see cref="MaxDecodePixelWidth"/>. Applies the same defensive
    /// discipline <see cref="StreamerBotEventPayload.TryParse"/> applies to
    /// hand-authored payloads: these are user files, and a bad one must cost
    /// this notification its background and nothing more.
    /// </summary>
    /// <summary>Internal rather than private so the self-test can prove degrade-on-bad-input and downscale-on-oversized directly, without a headset.</summary>
    internal static BitmapSource? LoadTemplate(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);

            int nativeWidth;
            using (var headerStream = new MemoryStream(bytes))
            {
                var frame = BitmapFrame.Create(
                    headerStream,
                    BitmapCreateOptions.DelayCreation,
                    BitmapCacheOption.OnDemand);
                nativeWidth = frame.PixelWidth;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (nativeWidth > MaxDecodePixelWidth)
            {
                bitmap.DecodePixelWidth = MaxDecodePixelWidth;
            }

            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            // Freeze makes it immutable and safe to keep across renders even
            // though it was created off whatever thread called Render.
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // A malformed, truncated or otherwise undecodable PNG. Deliberately
            // swallowed - see the type-level remarks: this must cost one
            // notification its background, not take down the render thread.
            return null;
        }
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
