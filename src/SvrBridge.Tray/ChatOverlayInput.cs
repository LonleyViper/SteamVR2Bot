using System.Drawing;
using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>What one laser event did, as far as the surface that owns it is concerned.</summary>
internal enum ChatInputOutcome
{
    /// <summary>Nothing the caller has to act on.</summary>
    None,

    /// <summary>The wearer grabbed the move handle. Start driving the offset from the controller pose.</summary>
    DragBegan,

    /// <summary>The wearer let go. Whatever offset the drag reached is the saved one.</summary>
    DragEnded
}

/// <summary>
/// Everything the chat window's laser interaction decides, with no OpenVR, no
/// render thread and no headset anywhere in it - which is the only reason any
/// of it can be proven by a self-test. <c>ChatOverlay</c> needs a live
/// <c>OpenVrInput</c> to exist at all, so the rules live here and that class
/// only wires them to the calls.
/// <para>
/// Three rules, and they are the whole class:
/// </para>
/// <list type="number">
/// <item>
/// Input is accepted only while the controller laser is armed for this panel.
/// Disarming clears the hover and abandons a drag in progress, so a panel that
/// is no longer accepting laser events cannot stay held indefinitely.
/// </item>
/// <item>
/// A repaint is owed when the <em>index</em> of the rectangle under the
/// pointer changes, never when the pointer merely moves. Laser jitter alone
/// produces mouse-move events far faster than the 10 Hz chat repaint, so
/// repainting per move would be both expensive and visibly behind the real
/// pointer.
/// </item>
/// <item>
/// The hit test reads the same rectangle table the renderer draws from, so the
/// highlighted control and the activated control cannot disagree.
/// </item>
/// </list>
/// </summary>
internal sealed class ChatOverlayInput
{
    private readonly IReadOnlyList<Rectangle> _buttons;
    private bool _holding;
    private bool _laserInputArmed;
    private bool _repaintOwed;

    /// <param name="buttons">
    /// The hit table. Defaults to <see cref="ChatOverlayLayout.Buttons"/> -
    /// the same array <see cref="WpfChatRenderer"/> draws from - and is
    /// injectable only so a self-test can prove the hover rules against a
    /// table with more entries than the one button this phase ships.
    /// </param>
    public ChatOverlayInput(IReadOnlyList<Rectangle>? buttons = null) =>
        _buttons = buttons ?? ChatOverlayLayout.Buttons;

    /// <summary>Which rectangle the pointer is currently in, or <see cref="ChatOverlayLayout.NoButton"/>.</summary>
    public int HoveredIndex { get; private set; } = ChatOverlayLayout.NoButton;

    /// <summary>
    /// Whether the wearer is holding the move handle down. Says nothing about
    /// whether a usable grab was established from it - poses can be missing at
    /// the moment of the press - which is why <c>ChatOverlay</c> keeps its own
    /// <see cref="OverlayDrag"/> and can cancel independently.
    /// </summary>
    public bool IsHolding => _holding;

    /// <summary>
    /// How many repaints the hover highlight has asked for since construction.
    /// Exists for the self-test that counts them: asserting the highlight
    /// <em>looks</em> right passes whether it repaints once or three hundred
    /// times, and the failure this guards against is entirely one of call
    /// frequency - the same class of bug as the gaze animation that eased
    /// forever with correct values.
    /// </summary>
    public int HoverRepaintCount { get; private set; }

    /// <summary>
    /// True once per hover transition, then false until the next one. Consumed
    /// rather than polled so a repaint cannot be owed twice for one change.
    /// </summary>
    public bool TakeRepaintOwed()
    {
        var owed = _repaintOwed;
        _repaintOwed = false;
        return owed;
    }

    /// <summary>
    /// Arms or disarms laser input for the panel. Disarming clears hover and
    /// abandons any drag, because queued events after the laser leaves must
    /// never be replayed against controls later.
    /// </summary>
    public void SetLaserInputArmed(bool armed)
    {
        if (_laserInputArmed == armed)
        {
            return;
        }

        _laserInputArmed = armed;
        if (!armed)
        {
            _holding = false;
            SetHovered(ChatOverlayLayout.NoButton);
        }
    }

    /// <summary>
    /// Feeds one laser event, already converted to panel space with the origin
    /// at the top-left. The caller turns a <see cref="ChatInputOutcome.DragBegan"/>
    /// into an actual grab by reading the two device poses - this type has no
    /// opinion about where anything is in the room.
    /// </summary>
    public ChatInputOutcome Handle(in OverlayMouseEvent laserEvent)
    {
        // Rule 1, applied before anything is read off the event: events can
        // still be in the queue from before the pointer left the panel.
        if (!_laserInputArmed)
        {
            return ChatInputOutcome.None;
        }

        switch (laserEvent.Kind)
        {
            case OverlayMouseEventKind.FocusLeave:
                _holding = false;
                SetHovered(ChatOverlayLayout.NoButton);
                return ChatInputOutcome.None;

            case OverlayMouseEventKind.Move:
                SetHovered(ChatOverlayLayout.IndexAt(_buttons, laserEvent.X, laserEvent.Y));
                return ChatInputOutcome.None;

            case OverlayMouseEventKind.ButtonDown:
                // Deliberately the hovered index rather than a fresh hit test
                // on the button event's own coordinates: SteamVR's button
                // packets are not a reliable source of x/y - the dashboard
                // path already uses the last move position for the same
                // reason - and re-testing would also be a second chance for
                // the highlight and the action to disagree.
                if (HoveredIndex != ChatOverlayLayout.MoveHandleIndex)
                {
                    return ChatInputOutcome.None;
                }

                _holding = true;
                return ChatInputOutcome.DragBegan;

            case OverlayMouseEventKind.ButtonUp:
                if (!_holding)
                {
                    return ChatInputOutcome.None;
                }

                // Release means "stop recomputing", nothing more. The panel is
                // already sitting where the drag last put it, so that is the
                // saved value - there is nothing to compute from the release
                // event itself.
                _holding = false;
                return ChatInputOutcome.DragEnded;

            default:
                return ChatInputOutcome.None;
        }
    }

    /// <summary>
    /// Abandons a drag without saving - the anchor moved out from under it, or
    /// the pointing hand stopped tracking. Leaves the offset wherever the drag
    /// had already applied it, which is what the wearer can see.
    /// </summary>
    public void CancelDrag() => _holding = false;

    private void SetHovered(int index)
    {
        if (HoveredIndex == index)
        {
            return;
        }

        HoveredIndex = index;
        _repaintOwed = true;
        HoverRepaintCount++;
    }
}
