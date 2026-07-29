using System.Globalization;
using WpfColor = System.Windows.Media.Color;

namespace SvrBridge.Tray;

/// <summary>
/// Parses the <c>#RRGGBB</c> colour strings
/// <see cref="SvrBridge.Core.StreamerBotEventPayload"/> already normalises,
/// shared by <see cref="WpfNotificationRenderer"/> and the chat renderer so a
/// malformed or missing colour costs a caller its default rather than a
/// dropped render, in exactly one place.
/// </summary>
internal static class WpfColourParsing
{
    public static WpfColor? TryParse(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#')
        {
            return null;
        }

        if (!byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return null;
        }

        return WpfColor.FromRgb(r, g, b);
    }
}
