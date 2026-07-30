using System.Drawing;

namespace SvrBridge.Tray;

/// <summary>
/// Bottom-bar button geometry shared by the renderer and the click handler.
/// <para>
/// Both sides read the same rectangles, so a laser click can never land on a
/// different button than the one the user is pointing at. Cancel is always the
/// first button on a setup page and Save is always the last, keeping the two
/// furthest apart.
/// </para>
/// </summary>
internal static class VrDashboardLayout
{
    public const int BarY = 800;
    public const int BarHeight = 64;

    /// <summary>
    /// Peer navigation between the Shortcuts wizard's entry point and the
    /// Settings page - see §"Key design decision" of the Phase 4b plan. Drawn
    /// only on those two pages; the five wizard sub-pages are unchanged and
    /// never see this row.
    /// </summary>
    public const int TabStripHeight = 76;

    /// <summary>
    /// A live headset check found the tab strip sitting flush against the
    /// panel's own top edge with no margin at all - this is that margin.
    /// Not the same gap as the one between the tab strip and the first
    /// section heading below it (see the comment on <see cref="ChatControlsY"/>).
    /// </summary>
    public const int TabStripY = 16;

    /// <summary>Shortcuts, Settings.</summary>
    public static readonly Rectangle[] Tabs =
    [
        new(60, TabStripY, 300, TabStripHeight),
        new(380, TabStripY, 300, TabStripHeight)
    ];

    /// <summary>
    /// Where the shortcut list's rows start now that the tab strip sits
    /// above the title - shared by the renderer and
    /// <c>VrDashboardController.HandleListClick</c> so the two cannot drift
    /// apart. Row count dropped from 6 to 5 to keep the last row clear of
    /// the "Create a new shortcut" bar at <see cref="BarY"/> - overflow still
    /// reads "+N more on the desktop", unchanged.
    /// </summary>
    public const int ListRowsStartY = 190;

    public const int ListRowHeight = 105;

    public const int ListVisibleRowCount = 5;

    // --- Settings page ---
    // Two sections (Chat, Notifications), each a toggle+anchor-mode+
    // anchor-hand row and an opacity/size slider row; Chat alone also gets a
    // gaze-sensitivity row. No bottom bar on this page - every change applies
    // and saves automatically, so there is nothing to Cancel or Save.

    // 170, not immediately below the tab strip: a live headset check found
    // the first row's heading text uncomfortably close under it, so this
    // leaves real breathing room rather than the bare minimum gap the layout
    // technically allowed. See TabStripY for the separate margin above the
    // tab strip itself.
    public const int ChatControlsY = 170;
    public const int ChatSlidersY = 290;
    public const int GazeSensitivityY = 410;
    public const int NotificationControlsY = 530;
    public const int NotificationSlidersY = 650;
    public const int SettingsRowHeight = 80;
    public const int SettingsSliderRowHeight = 90;

    /// <summary>On/off toggle for the chat window.</summary>
    public static readonly Rectangle ChatToggle = new(1090, ChatControlsY, 250, SettingsRowHeight);

    /// <summary>On/off toggle for notifications.</summary>
    public static readonly Rectangle NotificationToggle =
        new(1090, NotificationControlsY, 250, SettingsRowHeight);

    /// <summary>Controller, Headset.</summary>
    public static readonly Rectangle[] ChatAnchorMode =
    [
        new(60, ChatControlsY, 220, SettingsRowHeight),
        new(290, ChatControlsY, 220, SettingsRowHeight)
    ];

    /// <summary>
    /// Left hand, Right hand - only meaningful, and only drawn interactive,
    /// while <see cref="ChatAnchorMode"/>'s Controller option is selected.
    /// </summary>
    public static readonly Rectangle[] ChatAnchorHand =
    [
        new(530, ChatControlsY, 220, SettingsRowHeight),
        new(760, ChatControlsY, 220, SettingsRowHeight)
    ];

    /// <summary>Controller, Headset - see <see cref="ChatAnchorMode"/>.</summary>
    public static readonly Rectangle[] NotificationAnchorMode =
    [
        new(60, NotificationControlsY, 220, SettingsRowHeight),
        new(290, NotificationControlsY, 220, SettingsRowHeight)
    ];

    /// <summary>Left hand, Right hand - see <see cref="ChatAnchorHand"/>.</summary>
    public static readonly Rectangle[] NotificationAnchorHand =
    [
        new(530, NotificationControlsY, 220, SettingsRowHeight),
        new(760, NotificationControlsY, 220, SettingsRowHeight)
    ];

    /// <summary>
    /// Click-to-position hit region for the chat opacity slider - reuses the
    /// tolerance-slider shape from <see cref="Tolerance"/>'s own click
    /// handling: far taller than the visible track, snapped rather than
    /// free-form, no drag state.
    /// </summary>
    public static readonly Rectangle ChatOpacityTrack = new(60, ChatSlidersY, 580, SettingsSliderRowHeight);

    /// <summary>Chat size-scale slider hit region - see <see cref="ChatOpacityTrack"/>.</summary>
    public static readonly Rectangle ChatSizeTrack = new(760, ChatSlidersY, 580, SettingsSliderRowHeight);

    /// <summary>Notification opacity slider hit region - see <see cref="ChatOpacityTrack"/>.</summary>
    public static readonly Rectangle NotificationOpacityTrack =
        new(60, NotificationSlidersY, 580, SettingsSliderRowHeight);

    /// <summary>Notification size-scale slider hit region - see <see cref="ChatOpacityTrack"/>.</summary>
    public static readonly Rectangle NotificationSizeTrack =
        new(760, NotificationSlidersY, 580, SettingsSliderRowHeight);

    /// <summary>Relaxed, Normal, Tight - chat only; notifications have no gaze-scale behaviour.</summary>
    public static readonly Rectangle[] GazeSensitivity =
    [
        new(60, GazeSensitivityY, 400, SettingsRowHeight),
        new(480, GazeSensitivityY, 400, SettingsRowHeight),
        new(900, GazeSensitivityY, 400, SettingsRowHeight)
    ];

    /// <summary>Cancel, Back, Next.</summary>
    public static readonly Rectangle[] Tolerance =
    [
        new(60, BarY, 280, BarHeight),
        new(360, BarY, 280, BarHeight),
        new(660, BarY, 680, BarHeight)
    ];

    /// <summary>Cancel, Back.</summary>
    public static readonly Rectangle[] RecordInput =
    [
        new(60, BarY, 400, BarHeight),
        new(480, BarY, 860, BarHeight)
    ];

    /// <summary>Cancel, Back, Previous page, Next page.</summary>
    public static readonly Rectangle[] ActionPicker =
    [
        new(60, BarY, 260, BarHeight),
        new(340, BarY, 300, BarHeight),
        new(660, BarY, 300, BarHeight),
        new(980, BarY, 360, BarHeight)
    ];

    /// <summary>Cancel, Record again, Choose action, Save.</summary>
    public static readonly Rectangle[] Review =
    [
        new(60, BarY, 230, BarHeight),
        new(310, BarY, 360, BarHeight),
        new(690, BarY, 280, BarHeight),
        new(990, BarY, 350, BarHeight)
    ];

    /// <summary>
    /// Maps a click to a button. The gaps between buttons belong to the button
    /// on their left, so a slightly off aim still does what the user meant
    /// rather than nothing at all.
    /// </summary>
    public static int IndexAt(IReadOnlyList<Rectangle> row, float x)
    {
        for (var index = 0; index < row.Count - 1; index++)
        {
            if (x < row[index + 1].Left)
            {
                return index;
            }
        }

        return row.Count - 1;
    }
}
