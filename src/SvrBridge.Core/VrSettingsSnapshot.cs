namespace SvrBridge.Core;

/// <summary>
/// How readily the chat window's gaze-scale visibility triggers. Presented
/// as a small number of named choices rather than a raw angle, per §B1 of
/// the Phase 4b plan - a user judges this by trying it on in the headset,
/// not by typing degrees on a desktop form.
/// </summary>
public enum GazeSensitivity
{
    /// <summary>Wider entry/exit angles - the window grows even when not looked at directly.</summary>
    Relaxed,

    /// <summary>The angles <see cref="ChatGazeHysteresis"/> has always defaulted to (20°/35°).</summary>
    Normal,

    /// <summary>Narrower angles - the wearer must look more directly at the window.</summary>
    Tight
}

/// <summary>
/// Everything the VR settings page needs to display and can change, in one
/// value. Passed into the dashboard controller when it opens and reported
/// back, via a callback, whenever the wearer changes something - see §"Live
/// -apply architecture" of the Phase 4b plan. Mirrors exactly the fields
/// <c>UserSettings</c> persists for the chat and notification surfaces, so
/// the VR page and the desktop page can never disagree about what a setting
/// means or what its default is.
/// </summary>
/// <param name="ChatPlacement">
/// The chat window's hand-placed offsets, one per anchor mode. Unlike every
/// other field here it is usually changed by dragging the window in the
/// headset rather than by a control on the settings page - the page's only
/// control for it is the reset back to
/// <see cref="OverlayPlacement.Default"/>, which exists because a window
/// dragged somewhere unreachable cannot be dragged back.
/// </param>
public sealed record VrSettingsSnapshot(
    bool ChatEnabled,
    OverlayAnchor ChatAnchor,
    OverlayPlacement ChatPlacement,
    double ChatOpacity,
    double ChatSizeScale,
    GazeSensitivity GazeSensitivity,
    bool ChatGazeScaleEnabled,
    bool NotificationsEnabled,
    OverlayAnchor NotificationAnchor,
    double NotificationOpacity,
    double NotificationSizeScale);
