using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SvrBridge.Core;
using WpfColor = System.Windows.Media.Color;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;

namespace SvrBridge.Tray;

/// <summary>
/// Renders the current chat ring buffer into a fixed-size panel texture using
/// WPF, per §B3 of the chat plan: real <c>TextWrapping</c> with inline
/// coloured <c>Run</c> elements for usernames, not hand-measured layout.
/// <para>
/// A second <see cref="IVrPanelRenderer{TContent}"/> implementation behind
/// the same seam as <see cref="WpfNotificationRenderer"/> - it shares
/// <see cref="WpfRenderThread"/> and <see cref="WpfOverlayPixelPipeline"/>
/// rather than forking either.
/// </para>
/// </summary>
internal sealed class WpfChatRenderer : IVrPanelRenderer<ChatContent>
{
    public const int PanelWidth = 512;
    public const int PanelHeight = 768;
    private const double Padding = 20;
    private const double FontSize = 20;
    private const double EmoteImageHeight = FontSize * 1.2;
    private const double BadgeImageHeight = FontSize * 0.9;

    private static readonly WpfColor DefaultUsernameColor = WpfColor.FromRgb(180, 195, 220);
    private static readonly WpfColor BackgroundColor = WpfColor.FromArgb(230, 18, 24, 36);
    private static readonly WpfColor BadgeColor = WpfColor.FromRgb(140, 150, 165);
    private static readonly WpfColor BodyColor = WpfColor.FromRgb(225, 230, 240);
    private static readonly WpfColor EmoteColor = WpfColor.FromRgb(190, 140, 255);

    // Faint enough at rest that the handle does not compete with the text on a
    // window that is mostly read rather than moved, and unmistakable once the
    // laser is on it - the hover fill is the blue the VR dashboard already
    // uses for "this is the control you are about to activate".
    private static readonly WpfColor HandleColor = WpfColor.FromArgb(90, 148, 163, 184);
    private static readonly WpfColor HandleHoverColor = WpfColor.FromArgb(235, 59, 130, 246);
    private static readonly WpfColor HandleGlyphColor = WpfColor.FromArgb(220, 235, 240, 250);

    /// <summary>
    /// A four-way arrow in a 24x24 box, scaled to whatever
    /// <see cref="ChatOverlayLayout"/> says the handle is. The universal
    /// "grab this to move it" icon, and the one OVRdrop uses - the interaction
    /// this phase was modelled on.
    /// </summary>
    private const string MoveHandleGlyph =
        "M12,2 L16,6 L13,6 L13,11 L18,11 L18,8 L22,12 L18,16 L18,13 L13,13 L13,18 L16,18 "
        + "L12,22 L8,18 L11,18 L11,13 L6,13 L6,16 L2,12 L6,8 L6,11 L11,11 L11,6 L8,6 Z";

    private readonly WpfRenderThread _renderThread;
    private readonly ChatImageCache? _chatImages;
    private bool _disposed;

    /// <param name="chatImages">
    /// Null renders every emote name as styled text only - used by
    /// self-tests, which have no business making network calls. Production
    /// always supplies one; a real image is used whenever it is already
    /// decoded and cached, falling back to styled text otherwise.
    /// </param>
    public WpfChatRenderer(ChatImageCache? chatImages = null)
    {
        _renderThread = new WpfRenderThread("SteamVR2Bot chat renderer");
        _chatImages = chatImages;
    }

    public RenderedPanel Render(ChatContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _renderThread.Invoke(() => RenderOnDispatcherThread(content, _chatImages));
    }

    private static RenderedPanel RenderOnDispatcherThread(ChatContent content, ChatImageCache? chatImages)
    {
        var panel = BuildPanel(content, chatImages);
        var pixels = WpfOverlayPixelPipeline.RenderToRgba(panel, PanelWidth, PanelHeight);
        return new RenderedPanel(pixels, PanelWidth, PanelHeight);
    }

    /// <summary>
    /// Internal rather than private so a self-test can measure the text
    /// block's own layout directly - the only way to prove a long unbroken
    /// string wraps onto multiple lines instead of silently overflowing the
    /// panel width.
    /// </summary>
    internal static Border BuildPanel(ChatContent content, ChatImageCache? chatImages = null)
    {
        // The padding moved off the outer Border and onto the text's own so
        // the grid below shares the panel's coordinate space exactly. Controls
        // are positioned from ChatOverlayLayout, whose rectangles are in the
        // same panel pixels SteamVR reports mouse events in; an inset origin
        // would put every hit rectangle 20 px away from what was drawn.
        var grid = new Grid { Width = PanelWidth, Height = PanelHeight };
        grid.Children.Add(
            new Border
            {
                Padding = new Thickness(Padding),
                Child = BuildTextBlock(content.Messages, chatImages)
            });
        grid.Children.Add(BuildControl(ChatOverlayLayout.MoveHandleIndex, content.HoveredButtonIndex));

        return new Border
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Background = new SolidColorBrush(BackgroundColor),
            // The ring buffer already bounds message count, but the rendered
            // height of that many messages does not always fit the panel.
            // Clipping plus bottom alignment below means overflow trims from
            // the top - the oldest messages - leaving the newest visible,
            // which is what "newest at the bottom" in §B3 requires.
            ClipToBounds = true,
            Child = grid
        };
    }

    /// <summary>
    /// Draws one entry of <see cref="ChatOverlayLayout.Buttons"/> at exactly
    /// the rectangle the hit test will use for it - the structural rule this
    /// panel exists to keep. The highlight is keyed on the hovered index and
    /// nothing else, so it can never light up a control the laser would miss.
    /// </summary>
    private static UIElement BuildControl(int index, int hoveredIndex)
    {
        var bounds = ChatOverlayLayout.Buttons[index];
        return new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.Left, bounds.Top, 0, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(index == hoveredIndex ? HandleHoverColor : HandleColor),
            Padding = new Thickness(bounds.Width * 0.2),
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(MoveHandleGlyph),
                Fill = new SolidColorBrush(HandleGlyphColor),
                Stretch = Stretch.Uniform
            }
        };
    }

    internal static TextBlock BuildTextBlock(
        IReadOnlyList<StreamerBotEventPayload> messages,
        ChatImageCache? chatImages = null)
    {
        var textBlock = new TextBlock
        {
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = FontSize,
            // Wrap, not WrapWithOverflow: a long unbroken run - a URL with no
            // spaces - must be force-broken to fit the panel width rather
            // than allowed to overflow it, per §B3.
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Bottom,
            Width = PanelWidth - (Padding * 2)
        };

        foreach (var message in messages)
        {
            AppendMessage(textBlock.Inlines, message, chatImages);
        }

        return textBlock;
    }

    private static void AppendMessage(
        InlineCollection inlines,
        StreamerBotEventPayload message,
        ChatImageCache? chatImages)
    {
        if (inlines.Count > 0)
        {
            inlines.Add(new LineBreak());
        }

        var username = string.IsNullOrWhiteSpace(message.User) ? "(no name)" : message.User;
        inlines.Add(
            new Run(username)
            {
                Foreground = new SolidColorBrush(
                    WpfColourParsing.TryParse(message.Colour) ?? DefaultUsernameColor),
                FontWeight = FontWeights.Bold
            });

        AppendBadge(inlines, message, chatImages);

        inlines.Add(new Run(": ") { Foreground = new SolidColorBrush(BodyColor) });
        AppendBodyText(inlines, message, chatImages);
    }

    /// <summary>
    /// Renders every badge on the message in sequence - see
    /// <see cref="StreamerBotEventPayload.Badges"/> for why a chatter can
    /// carry more than one. A hand-authored payload that only ever set the
    /// singular <see cref="StreamerBotEventPayload.Badge"/>/
    /// <see cref="StreamerBotEventPayload.BadgeImageUrl"/> is treated as a
    /// one-item list, so that simpler contract still works unchanged.
    /// </summary>
    private static void AppendBadge(
        InlineCollection inlines,
        StreamerBotEventPayload message,
        ChatImageCache? chatImages)
    {
        var badges = message.Badges.Count > 0
            ? message.Badges
            : string.IsNullOrWhiteSpace(message.Badge)
                ? []
                : (IReadOnlyList<ChatBadge>)[new ChatBadge(message.Badge, message.BadgeImageUrl)];

        foreach (var badge in badges)
        {
            AppendOneBadge(inlines, badge, chatImages);
        }
    }

    /// <summary>
    /// Renders the real badge icon when it is already cached, per Twitch's
    /// own convention of showing the icon alone with no accompanying text.
    /// Falls back to the bracketed text label - always present, since
    /// <c>TwitchChatMessageMapper</c> derives one for every badge - for the
    /// same three
    /// reasons the emote text fallback exists: no cache supplied, image
    /// still downloading, or no image URL at all for this badge.
    /// </summary>
    private static void AppendOneBadge(InlineCollection inlines, ChatBadge badge, ChatImageCache? chatImages)
    {
        if (badge.ImageUrl.Length > 0
            && chatImages is not null
            && chatImages.TryGetByUrl(badge.ImageUrl, out var badgeImage)
            && badgeImage is not null)
        {
            inlines.Add(new Run(" ") { Foreground = new SolidColorBrush(BodyColor) });
            inlines.Add(
                new InlineUIContainer(BuildInlineImage(badgeImage, BadgeImageHeight))
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
            return;
        }

        inlines.Add(new Run($" [{badge.Label}]") { Foreground = new SolidColorBrush(BadgeColor) });
    }

    /// <summary>
    /// Splits the message body on spaces. A token that exactly matches one of
    /// the message's own <see cref="StreamerBotEventPayload.EmoteNames"/> is
    /// rendered as its real image when one is already decoded and cached, or
    /// as styled text - distinct from ordinary words, never fetching an
    /// image itself - otherwise. That fallback covers three cases the same
    /// way: no cache was supplied (self-tests), the image has not finished
    /// downloading yet (it will appear on a later repaint once it has - see
    /// <c>ChatImageCache.Version</c>), or the fetch failed. Twitch's own
    /// emote names are exact, case-sensitive tokens, so an ordinal exact
    /// match on whitespace-delimited words is enough; no punctuation
    /// stripping is needed because Twitch never embeds an emote name inside
    /// a larger word.
    /// </summary>
    private static void AppendBodyText(
        InlineCollection inlines,
        StreamerBotEventPayload message,
        ChatImageCache? chatImages)
    {
        if (message.Emotes.Count > 0)
        {
            AppendSpannedEmotes(inlines, message.Text, message.Emotes, chatImages);
            return;
        }

        var text = message.Text;
        var emoteNames = message.EmoteNames;
        if (emoteNames.Count == 0)
        {
            inlines.Add(new Run(text) { Foreground = new SolidColorBrush(BodyColor) });
            return;
        }

        var emoteSet = new HashSet<string>(emoteNames, StringComparer.Ordinal);
        var tokens = text.Split(' ');
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            var trailingSpace = index == tokens.Length - 1 ? "" : " ";
            if (emoteSet.Contains(token)
                && chatImages is not null
                && chatImages.TryGet(token, out var image)
                && image is not null)
            {
                inlines.Add(
                    new InlineUIContainer(BuildInlineImage(image, EmoteImageHeight))
                    {
                        BaselineAlignment = BaselineAlignment.Center
                    });
                if (trailingSpace.Length > 0)
                {
                    inlines.Add(new Run(trailingSpace) { Foreground = new SolidColorBrush(BodyColor) });
                }

                continue;
            }

            var display = token + trailingSpace;
            var isEmote = emoteSet.Contains(token);
            inlines.Add(
                new Run(display)
                {
                    Foreground = new SolidColorBrush(isEmote ? EmoteColor : BodyColor),
                    FontStyle = isEmote ? FontStyles.Italic : FontStyles.Normal,
                    FontWeight = isEmote ? FontWeights.SemiBold : FontWeights.Normal
                });
        }
    }

    private static void AppendSpannedEmotes(
        InlineCollection inlines,
        string text,
        IReadOnlyList<ChatEmote> emotes,
        ChatImageCache? chatImages)
    {
        var cursor = 0;
        foreach (var emote in emotes.OrderBy(emote => emote.StartIndex).ThenBy(emote => emote.EndIndex))
        {
            if (emote.StartIndex < cursor || emote.EndIndex >= text.Length)
            {
                continue;
            }

            if (emote.StartIndex > cursor)
            {
                inlines.Add(new Run(text[cursor..emote.StartIndex]) { Foreground = new SolidColorBrush(BodyColor) });
            }

            BitmapImage? image = null;
            var imageReady = chatImages is not null
                && (emote.ImageUrl.Length > 0
                    ? chatImages.TryGetByUrl(emote.ImageUrl, out image)
                    : chatImages.TryGet(emote.Name, out image))
                && image is not null;
            if (imageReady)
            {
                inlines.Add(new InlineUIContainer(BuildInlineImage(image!, EmoteImageHeight))
                {
                    BaselineAlignment = BaselineAlignment.Center
                });
            }
            else
            {
                inlines.Add(new Run(text[emote.StartIndex..(emote.EndIndex + 1)])
                {
                    Foreground = new SolidColorBrush(EmoteColor),
                    FontStyle = FontStyles.Italic,
                    FontWeight = FontWeights.SemiBold
                });
            }

            cursor = emote.EndIndex + 1;
        }

        if (cursor < text.Length)
        {
            inlines.Add(new Run(text[cursor..]) { Foreground = new SolidColorBrush(BodyColor) });
        }
    }

    private static WpfImage BuildInlineImage(BitmapImage source, double height) =>
        new()
        {
            Source = source,
            Height = height,
            Stretch = Stretch.Uniform
        };

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
