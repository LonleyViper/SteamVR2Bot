namespace SvrBridge.Core;

/// <summary>
/// Tracks the transient anchor and hidden overrides a Streamer.bot
/// <c>control</c> command can place on one overlay surface, kept separate
/// from OpenVR and rendering so the override semantics themselves - what
/// each command does, and that <c>reset</c> restores the saved default - can
/// be proven without a headset. See §B3 of the Phase 4 plan.
/// <para>
/// Deliberately holds no persisted state and never touches
/// <see cref="UserSettings" />: a control command sets an in-memory override
/// over the saved default, never the saved default itself. The saved default
/// starts out as whatever the OpenVR worker was spawned with (see
/// <c>AppConfig.ChatAnchor</c>/<c>NotificationAnchor</c>), the same way a
/// settings change from the desktop only takes effect on the worker's next
/// restart. It can also change mid-session via <see cref="SetSavedDefaultAnchor"/>
/// - see that method's remarks for why that is a distinct case from a
/// Streamer.bot override.
/// </para>
/// <para>
/// <c>clear</c> is deliberately not represented here: it is a one-shot
/// action against a surface's own backlog (the chat ring buffer or the
/// notification queue), not a lasting state this class would need to
/// remember or restore on <c>reset</c>.
/// </para>
/// </summary>
public sealed class SurfaceOverrideState(OverlayAnchor savedDefault)
{
    private OverlayAnchor _savedDefault = savedDefault;

    public OverlayAnchor? AnchorOverride { get; private set; }

    public bool Hidden { get; private set; }

    /// <summary>The anchor currently in effect: the override if one is active, otherwise the saved default.</summary>
    public OverlayAnchor EffectiveAnchor => AnchorOverride ?? _savedDefault;

    /// <summary>
    /// The saved default on its own, ignoring any active override - what the
    /// VR settings page displays and edits. Deliberately not
    /// <see cref="EffectiveAnchor"/>: the settings page represents persisted
    /// configuration, the same thing the desktop settings page shows, and
    /// must not be confused with a Streamer.bot override's transient effect -
    /// see §B5 of the Phase 4b plan.
    /// </summary>
    public OverlayAnchor SavedDefault => _savedDefault;

    /// <summary>
    /// Applies one control command. Unrecognised commands and a malformed
    /// <c>anchor</c> command (no valid <see cref="StreamerBotEventPayload.RequestedAnchorMode"/>)
    /// leave the state unchanged rather than throwing - the caller decides
    /// whether either is worth logging.
    /// </summary>
    public void Apply(StreamerBotEventPayload payload)
    {
        switch (payload.Command.Trim().ToLowerInvariant())
        {
            case "show":
                Hidden = false;
                break;
            case "hide":
                Hidden = true;
                break;
            case "anchor":
                if (payload.RequestedAnchorMode is { } mode)
                {
                    AnchorOverride = new OverlayAnchor(mode, payload.RequestedAnchorHand ?? OverlayAnchorHand.Left);
                }

                break;
            case "reset":
                AnchorOverride = null;
                Hidden = false;
                break;
        }
    }

    /// <summary>
    /// Records a new saved default anchor - the effect of the wearer editing
    /// this surface's anchor from the VR settings page - and clears any
    /// active Streamer.bot override at the same time.
    /// <para>
    /// This is deliberately different from <see cref="Apply"/>'s <c>reset</c>
    /// command. A Streamer.bot <c>reset</c> hands control back to whatever
    /// the saved default already is; a VR settings edit *is* a new saved
    /// default, chosen by the person wearing the headset right now. Per §B5
    /// of the Phase 4b plan, an explicit user edit must win over an active
    /// override rather than appear to do nothing - showing the override as
    /// still in force while the person visibly changes the control and
    /// nothing happens would read as a broken setting.
    /// </para>
    /// </summary>
    public void SetSavedDefaultAnchor(OverlayAnchor anchor)
    {
        _savedDefault = anchor;
        AnchorOverride = null;
    }
}
