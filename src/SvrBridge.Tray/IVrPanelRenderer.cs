namespace SvrBridge.Tray;

/// <summary>Text content for one panel render, independent of what draws it.</summary>
public readonly record struct NotificationContent(string Title, string Text, string AccentHex);

/// <summary>One rendered texture, straight-alpha RGBA, ready for SteamVR.</summary>
public sealed record RenderedPanel(byte[] Rgba, int Width, int Height);

/// <summary>
/// Turns text content into a texture. The one implementation today is
/// <see cref="WpfNotificationRenderer"/>; the interface exists so Phase 3's
/// chat renderer is a second implementation behind the same seam rather than a
/// fork of the notification one, and so <see cref="NotificationOverlay"/>
/// never has to know it is WPF underneath.
/// </summary>
internal interface IVrPanelRenderer : IDisposable
{
    RenderedPanel Render(NotificationContent content);
}
