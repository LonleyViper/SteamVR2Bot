namespace SvrBridge.Tray;

/// <summary>
/// Text content for one notification render, independent of what draws it.
/// <para>
/// <see cref="AccentHex"/>, <see cref="BackgroundHex"/> and
/// <see cref="TextHex"/> are already the <b>resolved</b> colours - whichever
/// of the payload's own value or the configured setting won, per §B4 of the
/// Phase 7 plan's precedence rule. The renderer has no opinion about where a
/// colour came from, only what to draw with it; empty falls back to the
/// renderer's own hardcoded default, which covers a settings file predating
/// this feature.
/// </para>
/// <para>
/// <see cref="TemplatePath"/> is the resolved template - the payload's
/// <c>image</c> field if it set one, else the configured default - or empty
/// for no template at all. See §B5.
/// </para>
/// </summary>
public readonly record struct NotificationContent(
    string Title,
    string Text,
    string AccentHex,
    string BackgroundHex = "",
    string TextHex = "",
    string TemplatePath = "",
    double BackgroundOpacity = SvrBridge.Core.NotificationAppearanceSettings.DefaultBackgroundOpacity,
    double CornerRadiusPixels = 0,
    string Source = "",
    int PanelWidth = SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelWidth,
    int PanelHeight = SvrBridge.Core.NotificationAppearanceSettings.DefaultPanelHeight);

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
/// <param name="MoveHandleVisible">
/// Whether the chat interaction gate is armed. The external move tab is
/// omitted entirely while false; the hit-test remains separately gated by
/// <see cref="ChatOverlayInput"/>.
/// </param>
public readonly record struct ChatContent(
    IReadOnlyList<SvrBridge.Core.StreamerBotEventPayload> Messages,
    int HoveredButtonIndex = ChatOverlayLayout.NoButton,
    bool MoveHandleVisible = false,
    SvrBridge.Core.ChatWorkspaceTab ActiveTab = SvrBridge.Core.ChatWorkspaceTab.Chat,
    IReadOnlyList<SvrBridge.Core.ChatWorkspaceEntry>? Entries = null,
    int ActiveEntryCount = 0,
    double ScrollFraction = 0);

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
