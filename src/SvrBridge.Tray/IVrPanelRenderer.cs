namespace SvrBridge.Tray;

/// <summary>Text content for one notification render, independent of what draws it.</summary>
public readonly record struct NotificationContent(string Title, string Text, string AccentHex);

/// <summary>
/// The messages to draw for one chat repaint, oldest first, newest last - a
/// snapshot rather than a live reference into
/// <see cref="SvrBridge.Core.ChatRingBuffer"/>, so the render thread never
/// has to take that buffer's own lock.
/// </summary>
/// <param name="HoveredButtonIndex">
/// Which <see cref="ChatOverlayLayout.Buttons"/> entry the laser is currently
/// over, or <see cref="ChatOverlayLayout.NoButton"/>. Defaulted so every
/// caller that has no pointer - the notification path, and every self-test
/// about text - stays unchanged.
/// </param>
public readonly record struct ChatContent(
    IReadOnlyList<SvrBridge.Core.StreamerBotEventPayload> Messages,
    int HoveredButtonIndex = ChatOverlayLayout.NoButton);

/// <summary>One rendered texture, straight-alpha RGBA, ready for SteamVR.</summary>
public sealed record RenderedPanel(byte[] Rgba, int Width, int Height);

/// <summary>
/// Turns content into a texture. <see cref="WpfNotificationRenderer"/> and
/// the Phase 3 chat renderer are two implementations behind this one seam,
/// parameterised by content type rather than forked from one another, so
/// <see cref="NotificationOverlay"/> and its chat counterpart never have to
/// know they are WPF underneath.
/// </summary>
internal interface IVrPanelRenderer<in TContent> : IDisposable
{
    RenderedPanel Render(TContent content);
}
