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
