using System.Drawing;

namespace SvrBridge.Tray;

/// <summary>
/// Button geometry for the chat window, following the same pattern as
/// <see cref="VrDashboardLayout"/>: one shared rectangle table meant to be
/// read by both the renderer and (from Phase 5) the click handler, so a
/// laser click can never land on a different button than the one the wearer
/// is pointing at.
/// <para>
/// Empty today. v1 has no buttons and does not enable
/// <c>SetOverlayInputMethod</c> - see §B4 of the chat plan - but the table is
/// declared now, on day one, so a future phase adds entries here instead of
/// restructuring the renderer around a table that never existed.
/// </para>
/// </summary>
internal static class ChatOverlayLayout
{
    public static readonly Rectangle[] Buttons = [];
}
