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
    /// Chat/Notifications settings pages - see §"Key design decision" of the
    /// Phase 4b plan and §B1 of the Phase 6 plan (originally two tabs, now
    /// three - the Settings page split by owning surface). Drawn only on
    /// these three pages; the five wizard sub-pages are unchanged and never
    /// see this row.
    /// </summary>
    public const int TabStripHeight = 76;

    /// <summary>
    /// A live headset check found the tab strip sitting flush against the
    /// panel's own top edge with no margin at all - this is that margin.
    /// Not the same gap as the one between the tab strip and the first
    /// section heading below it (see the comment on <see cref="ChatControlsY"/>).
    /// </summary>
    public const int TabStripY = 16;

    /// <summary>
    /// Shortcuts, Chat, Notifications. A third 300-wide tab at x=700 fits the
    /// 1400px panel with the same 20px gap the first two already use
    /// (380 - (60 + 300) = 20; 700 - (380 + 300) = 20), so no other layout
    /// value needed to move to make room for it.
    /// </summary>
    public static readonly Rectangle[] Tabs =
    [
        new(60, TabStripY, 300, TabStripHeight),
        new(380, TabStripY, 300, TabStripHeight),
        new(700, TabStripY, 300, TabStripHeight)
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

    // --- Chat and Notifications settings pages ---
    // Per §B1 of the Phase 6 plan, the single two-surface Settings page split
    // into one page per surface, each reached by its own tab. Each page now
    // starts at the same first-row Y below the tab strip that the combined
    // page used for its first (Chat) section - Notifications no longer needs
    // to start lower down the panel now that it is not sharing the page with
    // Chat above it. Chat alone also gets a gaze-sensitivity row and, lower
    // still, the reset-placement/grow-on-gaze row - both chat-only concepts,
    // per the plan's guidance to put a surface-specific control on that
    // surface's own tab. No bottom bar on either page - every change applies
    // and saves automatically, so there is nothing to Cancel or Save.

    // 170, not immediately below the tab strip: a live headset check found
    // the first row's heading text uncomfortably close under it, so this
    // leaves real breathing room rather than the bare minimum gap the layout
    // technically allowed. See TabStripY for the separate margin above the
    // tab strip itself.
    public const int ChatControlsY = 170;
    public const int ChatSlidersY = 290;
    public const int GazeSensitivityY = 410;

    /// <summary>
    /// Same first-row Y as <see cref="ChatControlsY"/> - the Notifications
    /// page has no rows above this one now that it is not sharing a page
    /// with Chat's controls, toggle, anchor and sliders.
    /// </summary>
    public const int NotificationControlsY = 170;

    /// <summary>Same relationship to <see cref="NotificationControlsY"/> as <see cref="ChatSlidersY"/> has to <see cref="ChatControlsY"/>.</summary>
    public const int NotificationSlidersY = 290;

    /// <summary>
    /// Same relationship to <see cref="NotificationSlidersY"/> as
    /// <see cref="GazeSensitivityY"/> has to <see cref="ChatSlidersY"/> - the
    /// Notifications page has no gaze-sensitivity row of its own, so this is
    /// the next row down rather than the one after that. Holds the §B1
    /// positioning toggle and its reset, added per the Phase 7 plan.
    /// </summary>
    public const int NotificationPositioningY = 410;

    public const int SettingsRowHeight = 80;
    public const int SettingsSliderRowHeight = 90;

    /// <summary>
    /// Below the gaze-sensitivity row (which ends at 490) with the same
    /// ~40px gap the page's other rows use, and above where a bottom bar
    /// would sit if this page had one - it does not, because every change
    /// here applies and saves immediately. Chat-only: the Notifications page
    /// has no placement to reset and no gaze-scale animation to toggle.
    /// </summary>
    public const int ResetPlacementY = 530;

    /// <summary>
    /// Puts the chat window's hand-dragged offset back to the placement Phase
    /// 1/3 proved on hardware.
    /// <para>
    /// The one control on this page for a setting the wearer changes by
    /// grabbing the window itself rather than by pressing anything here - and
    /// the reason it has to exist. A window dragged somewhere it cannot be
    /// pointed at can no longer be dragged back, and reaching for the desktop
    /// app to fix a placement chosen in VR is exactly the loop this phase
    /// removes.
    /// </para>
    /// </summary>
    public static readonly Rectangle ResetPlacement =
        new(60, ResetPlacementY, 560, SettingsRowHeight);

    /// <summary>
    /// Turns the grow-and-brighten-on-gaze animation off, for a wearer who
    /// finds a window that changes size while they read it more distracting
    /// than useful. Sits at the same x as the two surface toggles above it, so
    /// the three read as one column of on/off controls.
    /// </summary>
    public static readonly Rectangle GazeScaleToggle =
        new(1090, ResetPlacementY, 250, SettingsRowHeight);

    /// <summary>On/off toggle for the chat window.</summary>
    public static readonly Rectangle ChatToggle = new(1090, ChatControlsY, 250, SettingsRowHeight);

    /// <summary>On/off toggle for notifications.</summary>
    public static readonly Rectangle NotificationToggle =
        new(1090, NotificationControlsY, 250, SettingsRowHeight);

    /// <summary>
    /// Pins a persistent, grabbable dummy notification frame so the wearer
    /// can drag it into place - §B1 of the Phase 7 plan. Same column as
    /// <see cref="GazeScaleToggle"/> so the on/off controls on both pages
    /// read as one visual family.
    /// </summary>
    public static readonly Rectangle PositionNotificationsToggle =
        new(1090, NotificationPositioningY, 250, SettingsRowHeight);

    /// <summary>
    /// Puts the notification panel's hand-dragged offset back to its
    /// hardware-proven default - the Notifications-page counterpart of
    /// <see cref="ResetPlacement"/>, and for the identical reason: a frame
    /// dragged somewhere unreachable needs a way back that does not depend on
    /// being able to see or point at it.
    /// </summary>
    public static readonly Rectangle ResetNotificationPlacement =
        new(60, NotificationPositioningY, 560, SettingsRowHeight);

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
