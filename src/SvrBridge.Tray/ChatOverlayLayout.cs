using System.Drawing;

namespace SvrBridge.Tray;

/// <summary>
/// Control geometry for the chat window, following the same pattern as
/// <see cref="VrDashboardLayout"/>: one shared rectangle table read by the
/// renderer, the hover highlight and the hit test alike, so a laser click can
/// never land on a different control than the one the wearer is pointing at -
/// and what lights up under the pointer is guaranteed to be what activates.
/// <para>
/// Declared empty in v1 precisely so this phase could add an entry rather than
/// restructure the renderer. Adding a button means adding a rectangle here and
/// drawing it from the same array; it must never mean computing geometry
/// separately in <see cref="WpfChatRenderer"/>.
/// </para>
/// <para>
/// Coordinates are panel pixels with the origin at the top-left, matching what
/// the renderer draws into. SteamVR reports overlay mouse events with the
/// origin at the <em>bottom</em>-left, so <c>ChatOverlay</c> flips Y before
/// hit-testing against this table - see its own remarks.
/// </para>
/// </summary>
internal static class ChatOverlayLayout
{
    /// <summary>The visible chat card's fixed pixel width.</summary>
    public const int ChatCardWidth = 512;

    /// <summary>The visible chat card's fixed pixel height.</summary>
    public const int ChatCardHeight = 768;

    /// <summary>
    /// Transparent padding on each side of the card. Keeping this symmetric
    /// leaves the card centred at the existing overlay placement while the
    /// right gutter contains the external move tab.
    /// </summary>
    public const int GutterWidth = 80;

    /// <summary>The full texture size, and therefore the overlay's mouse scale.</summary>
    public const int PanelWidth = ChatCardWidth + (GutterWidth * 2);

    /// <summary>See <see cref="ChatCardHeight"/>.</summary>
    public const int PanelHeight = ChatCardHeight;

    /// <summary>The answer <see cref="IndexAt"/> gives when the pointer is over no control at all.</summary>
    public const int NoButton = -1;

    /// <summary>
    /// The grab-to-move handle's index in <see cref="Buttons"/>. Named rather
    /// than written as a bare 0 at every call site, because the whole point of
    /// this table is that the entries are added to, and a literal index would
    /// silently mean something else the first time one is inserted ahead of it.
    /// </summary>
    public const int MoveHandleIndex = 0;

    private const int HandleWidth = 64;
    private const int HandleHeight = 72;

    /// <summary>Inset from the chat frame's top edge for the external tab.</summary>
    public const int MoveHandleMargin = 8;

    /// <summary>The visible card's rectangle within the expanded texture.</summary>
    public static readonly Rectangle ChatCardBounds =
        new(GutterWidth, 0, ChatCardWidth, ChatCardHeight);

    /// <summary>
    /// The external move tab's rectangle. It is entirely in the right gutter,
    /// never over chat text, and is the only interactive rectangle outside the
    /// card.
    /// </summary>
    public static readonly Rectangle MoveHandleBounds =
        new(
            ChatCardBounds.Right + GutterWidth - HandleWidth - MoveHandleMargin,
            ChatCardBounds.Top + MoveHandleMargin,
            HandleWidth,
            HandleHeight);

    /// <summary>
    /// Every hit rectangle on the panel, in draw order.
    /// <para>
    /// The move handle sits at the top of the right gutter, outside the visible card.
    /// Transparent padding is not a control: <see cref="IndexAt"/> only
    /// returns this explicit tab rectangle.
    /// </para>
    /// </summary>
    public static readonly Rectangle[] Buttons =
    [
        MoveHandleBounds
    ];

    /// <summary>
    /// Converts the old visible-card width into the expanded texture width.
    /// The overlay quad grows only by the gutter ratio, so the card retains
    /// its former physical width and height.
    /// </summary>
    public static float OverlayWidthForCardWidth(float cardWidthMeters) =>
        cardWidthMeters * PanelWidth / (float)ChatCardWidth;

    /// <summary>Returns the visible card width represented by a texture width.</summary>
    public static float CardWidthForOverlayWidth(float overlayWidthMeters) =>
        overlayWidthMeters * ChatCardWidth / (float)PanelWidth;

    /// <summary>Returns the visible card height represented by a texture width.</summary>
    public static float CardHeightForOverlayWidth(float overlayWidthMeters) =>
        overlayWidthMeters * PanelHeight / (float)PanelWidth;

    /// <summary>
    /// Which rectangle a panel-space point falls in, or <see cref="NoButton"/>.
    /// <para>
    /// Unlike <see cref="VrDashboardLayout.IndexAt"/> this does not hand the
    /// gaps to the nearest control. That row-of-buttons rule exists so a
    /// slightly off aim still does what the user meant; here the surrounding
    /// space is chat text, not more buttons, and a grab that starts because
    /// the wearer aimed near the corner would move the window when they meant
    /// to read it.
    /// </para>
    /// </summary>
    public static int IndexAt(IReadOnlyList<Rectangle> buttons, float x, float y)
    {
        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            if (x >= button.Left && x < button.Right && y >= button.Top && y < button.Bottom)
            {
                return index;
            }
        }

        return NoButton;
    }
}
