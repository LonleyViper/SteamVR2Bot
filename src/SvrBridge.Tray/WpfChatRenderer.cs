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
    public const int PanelWidth = ChatOverlayLayout.PanelWidth;
    public const int PanelHeight = ChatOverlayLayout.PanelHeight;
    private const double Padding = 20;
    private const double FontSize = 20;
    private const double WorkspaceMaximumFontSize = 24;
    private const double WorkspaceMinimumFontSize = 13;
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
        // The texture is wider than the visible card so the move tab can live
        // outside it. The symmetric gutters keep the card centred at the
        // existing overlay placement. Controls are positioned from
        // ChatOverlayLayout, whose rectangles are in the same panel pixels
        // SteamVR reports mouse events in.
        var grid = new Grid { Width = PanelWidth, Height = PanelHeight };
        grid.Children.Add(
            new Border
            {
                Width = ChatOverlayLayout.ChatCardWidth,
                Height = ChatOverlayLayout.ChatCardHeight,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(
                    ChatOverlayLayout.ChatCardBounds.Left,
                    ChatOverlayLayout.ChatCardBounds.Top,
                    0,
                    0),
                Background = new SolidColorBrush(BackgroundColor),
                ClipToBounds = true,
                Padding = new Thickness(0),
                Child = BuildWorkspaceCard(content, chatImages)
            });
        foreach (var index in new[]
                 {
                     ChatOverlayLayout.ChatTabIndex,
                     ChatOverlayLayout.EventsTabIndex
                 })
        {
            grid.Children.Add(BuildControl(index, content));
        }
        if (content.MoveHandleVisible)
        {
            grid.Children.Add(BuildControl(ChatOverlayLayout.MoveHandleIndex, content));
        }

        return new Border
        {
            Width = PanelWidth,
            Height = PanelHeight,
            Background = System.Windows.Media.Brushes.Transparent,
            Child = grid
        };
    }

    /// <summary>
    /// Draws one entry of <see cref="ChatOverlayLayout.Buttons"/> at exactly
    /// the rectangle the hit test will use for it - the structural rule this
    /// panel exists to keep. The highlight is keyed on the hovered index and
    /// nothing else, so it can never light up a control the laser would miss.
    /// </summary>
    private static UIElement BuildControl(int index, ChatContent content)
    {
        var bounds = ChatOverlayLayout.Buttons[index];
        var isMove = index == ChatOverlayLayout.MoveHandleIndex;
        var isActiveTab = (index == ChatOverlayLayout.ChatTabIndex && content.ActiveTab == ChatWorkspaceTab.Chat)
                          || (index == ChatOverlayLayout.EventsTabIndex && content.ActiveTab == ChatWorkspaceTab.Events);
        return new Border
        {
            Width = bounds.Width,
            Height = bounds.Height,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(bounds.Left, bounds.Top, 0, 0),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(
                isMove && index == content.HoveredButtonIndex
                    ? HandleHoverColor
                    : isActiveTab
                        ? WpfColor.FromArgb(190, 59, 130, 246)
                        : HandleColor),
            Padding = isMove ? new Thickness(bounds.Width * 0.2) : new Thickness(0),
            Child = isMove
                ? new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse(MoveHandleGlyph),
                    Fill = new SolidColorBrush(HandleGlyphColor),
                    Stretch = Stretch.Uniform
                }
                : new TextBlock
                {
                    Text = ChatOverlayLayout.LabelFor(index),
                    FontFamily = new WpfFontFamily("Segoe UI"),
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(245, 250, 255)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
        };
    }

    private static UIElement BuildWorkspaceCard(ChatContent content, ChatImageCache? chatImages)
    {
        var entries = content.Entries;
        var body = content.ActiveTab == ChatWorkspaceTab.Events
            ? BuildEventTextBlock(entries ?? [])
            : BuildTextBlock(
                entries is null ? content.Messages : entries.Select(entry => entry.Payload).ToArray(),
                chatImages);
        FitWorkspaceTextToBounds(body);
        body.Width = ChatOverlayLayout.ContentBounds.Width;
        body.Height = ChatOverlayLayout.ContentBounds.Height;
        body.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        body.VerticalAlignment = VerticalAlignment.Top;
        body.Margin = new Thickness(
            ChatOverlayLayout.ContentBounds.Left - ChatOverlayLayout.ChatCardBounds.Left,
            ChatOverlayLayout.ContentBounds.Top - ChatOverlayLayout.ChatCardBounds.Top,
            0,
            0);
        var card = new Grid();
        card.Children.Add(body);

        // This is deliberately visual-only: controller scroll events remain
        // available anywhere over the armed panel, rather than requiring a
        // tiny laser target on the edge of the window.
        var track = ChatOverlayLayout.ScrollbarTrackBounds;
        var thumb = ChatOverlayLayout.ScrollbarThumbBounds(content.ActiveEntryCount, content.ScrollFraction);
        card.Children.Add(BuildScrollbarPart(
            track,
            WpfColor.FromArgb(74, 155, 187, 215)));
        card.Children.Add(BuildScrollbarPart(
            thumb,
            WpfColor.FromArgb(210, 195, 224, 245)));
        return card;
    }

    /// <summary>
    /// Chooses the largest readable type size whose fully wrapped rows fit in
    /// the fixed chat card. A cached emote/badge can impose a row height that
    /// font size alone cannot reduce, so the rare remaining overflow receives
    /// a small uniform scale as a final safety net. This keeps the entire
    /// scrollback slice inside the card rather than clipping its newest rows.
    /// </summary>
    internal static void FitWorkspaceTextToBounds(TextBlock body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var availableWidth = ChatOverlayLayout.ContentBounds.Width;
        var availableHeight = ChatOverlayLayout.ContentBounds.Height;
        body.Width = availableWidth;
        body.RenderTransform = Transform.Identity;
        body.RenderTransformOrigin = new System.Windows.Point(0, 0);

        var minimum = Math.Min(WorkspaceMinimumFontSize, body.FontSize);
        var maximum = WorkspaceMaximumFontSize;
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var candidate = (minimum + maximum) / 2;
            body.FontSize = candidate;
            body.Measure(new System.Windows.Size(availableWidth, double.PositiveInfinity));
            if (body.DesiredSize.Height <= availableHeight)
            {
                minimum = candidate;
            }
            else
            {
                maximum = candidate;
            }
        }

        body.FontSize = minimum;
        body.Measure(new System.Windows.Size(availableWidth, double.PositiveInfinity));
        if (body.DesiredSize.Height > availableHeight)
        {
            var scale = availableHeight / body.DesiredSize.Height;
            body.RenderTransform = new ScaleTransform(scale, scale);
        }
    }

    private static Border BuildScrollbarPart(System.Drawing.Rectangle bounds, WpfColor color) =>
        new()
        {
            Width = bounds.Width,
            Height = bounds.Height,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(
                bounds.Left - ChatOverlayLayout.ChatCardBounds.Left,
                bounds.Top - ChatOverlayLayout.ChatCardBounds.Top,
                0,
                0),
            CornerRadius = new CornerRadius(bounds.Width / 2d),
            Background = new SolidColorBrush(color),
            IsHitTestVisible = false
        };

    private static TextBlock BuildEventTextBlock(IReadOnlyList<ChatWorkspaceEntry> entries)
    {
        var textBlock = new TextBlock
        {
            FontFamily = new WpfFontFamily("Segoe UI"),
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(BodyColor)
        };
        if (entries.Count == 0)
        {
            textBlock.Inlines.Add(new Run("No events received this session yet.") { Foreground = new SolidColorBrush(BadgeColor) });
            return textBlock;
        }

        foreach (var entry in entries)
        {
            if (textBlock.Inlines.Count > 0) textBlock.Inlines.Add(new LineBreak());
            var payload = entry.Payload;
            var title = string.IsNullOrWhiteSpace(payload.Title) ? "Event" : payload.Title;
            var source = string.IsNullOrWhiteSpace(payload.Source) ? "Streamer.bot" : payload.Source;
            textBlock.Inlines.Add(new Run($"{entry.ReceivedAt.LocalDateTime:HH:mm}  ") { Foreground = new SolidColorBrush(BadgeColor) });
            textBlock.Inlines.Add(new Run(title) { FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(BodyColor) });
            if (!string.IsNullOrWhiteSpace(payload.Text))
            {
                textBlock.Inlines.Add(new Run($" — {payload.Text}") { Foreground = new SolidColorBrush(BodyColor) });
            }
            textBlock.Inlines.Add(new Run($"  [{source}]") { Foreground = new SolidColorBrush(BadgeColor) });
        }

        return textBlock;
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
            Width = ChatOverlayLayout.ChatCardWidth - (Padding * 2)
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
