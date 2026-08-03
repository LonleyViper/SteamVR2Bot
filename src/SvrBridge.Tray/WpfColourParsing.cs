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
        if ((hex.Length is not (7 or 9)) || hex[0] != '#')
        {
            return null;
        }

        var hasAlpha = hex.Length == 9;
        var offset = hasAlpha ? 3 : 1;
        var alpha = byte.MaxValue;
        if ((hasAlpha && !byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out alpha))
            || !byte.TryParse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            || !byte.TryParse(hex.AsSpan(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            || !byte.TryParse(hex.AsSpan(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        {
            return null;
        }

        return WpfColor.FromArgb(alpha, r, g, b);
    }
}
