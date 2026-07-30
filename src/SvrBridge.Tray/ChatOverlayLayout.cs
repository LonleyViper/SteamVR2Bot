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
    /// <summary>The panel texture's own size, and therefore the overlay's mouse scale.</summary>
    public const int PanelWidth = WpfChatRenderer.PanelWidth;

    /// <summary>See <see cref="PanelWidth"/>.</summary>
    public const int PanelHeight = WpfChatRenderer.PanelHeight;

    /// <summary>The answer <see cref="IndexAt"/> gives when the pointer is over no control at all.</summary>
    public const int NoButton = -1;

    /// <summary>
    /// The grab-to-move handle's index in <see cref="Buttons"/>. Named rather
    /// than written as a bare 0 at every call site, because the whole point of
    /// this table is that the entries are added to, and a literal index would
    /// silently mean something else the first time one is inserted ahead of it.
    /// </summary>
    public const int MoveHandleIndex = 0;

    private const int HandleSize = 72;
    private const int HandleMargin = 14;

    /// <summary>
    /// Every hit rectangle on the panel, in draw order.
    /// <para>
    /// The move handle sits in the top-right corner: chat is bottom-aligned
    /// and grows upward, so the top of the panel is the one region reliably
    /// clear of text. 72 px on a 512 px panel is about 4.5 cm across at the
    /// gazed-at width, which is a comfortable laser target at arm's length.
    /// </para>
    /// </summary>
    public static readonly Rectangle[] Buttons =
    [
        new(PanelWidth - HandleSize - HandleMargin, HandleMargin, HandleSize, HandleSize)
    ];

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
