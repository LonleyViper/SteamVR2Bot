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
            Padding = new Thickness(Padding),
            Child = BuildTextBlock(content.Messages, chatImages)
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
        AppendBodyText(inlines, message.Text, message.EmoteNames, chatImages);
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
        string text,
        IReadOnlyList<string> emoteNames,
        ChatImageCache? chatImages)
    {
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
