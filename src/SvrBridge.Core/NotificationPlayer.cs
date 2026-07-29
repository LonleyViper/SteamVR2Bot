namespace SvrBridge.Core;

/// <summary>Where a notification's on-screen life currently sits.</summary>
public enum NotificationPhase
{
    /// <summary>Nothing queued and nothing on screen.</summary>
    Idle,
    FadingIn,
    Holding,
    FadingOut
}

/// <summary>
/// One tick of playback: what should be on screen, at what opacity, and
/// whether it just started - which is the caller's only cue to repaint,
/// because the texture is rendered once per notification and never again
/// during its fade. See <see cref="NotificationPlayer.Tick"/>.
/// </summary>
public readonly record struct NotificationFrame(
    NotificationPhase Phase,
    StreamerBotEventPayload? Current,
    float Alpha,
    bool IsNewItem);

/// <summary>
/// Pure playback logic for the notification overlay: a bounded queue and a
/// fade-in/hold/fade-out timeline, with no OpenVR or rendering dependency so
/// it can be proven correct without a headset.
/// <para>
/// Deliberately single-threaded by contract rather than by lock. The only
/// caller is the OpenVR worker's own poll loop, which already serialises
/// every overlay call onto one thread - the same reason
/// <see cref="VrOverlaySurface"/> takes no lock of its own.
/// </para>
/// </summary>
public sealed class NotificationPlayer
{
    /// <summary>
    /// Bounded rather than unbounded: a backlog of stale notifications from a
    /// misbehaving Streamer.bot action is worse than dropping the oldest of a
    /// burst, per §5 of the notifications plan.
    /// </summary>
    public const int QueueCapacity = 8;

    /// <summary>
    /// The longest either half of the fade is allowed to take. Halved instead
    /// of applied in full when a notification's clamped duration is short, so
    /// a 500 ms notification still gets a fade rather than fading in for
    /// longer than it is on screen.
    /// </summary>
    private const int MaximumFadeMs = 250;

    private readonly Queue<StreamerBotEventPayload> _queue = new();
    private StreamerBotEventPayload? _current;
    private long _startedAtMs;
    private bool _paintPending;

    /// <summary>Items waiting behind whatever is currently showing, if anything.</summary>
    public int QueuedCount => _queue.Count;

    /// <summary>True while a notification is on screen (fading in, holding, or fading out).</summary>
    public bool IsShowing => _current is not null;

    /// <summary>
    /// Adds a notification to the queue, dropping the oldest queued item first
    /// if it is already full. Never drops the item currently on screen - only
    /// ever the backlog behind it.
    /// </summary>
    public void Enqueue(StreamerBotEventPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_queue.Count >= QueueCapacity)
        {
            _queue.Dequeue();
        }

        _queue.Enqueue(payload);
    }

    /// <summary>
    /// Advances playback to <paramref name="nowMs"/> and reports what should
    /// be on screen. Cheap and side-effect-free to call repeatedly while idle:
    /// with nothing queued and nothing showing this does no work beyond
    /// returning <see cref="NotificationPhase.Idle"/>.
    /// </summary>
    public NotificationFrame Tick(long nowMs)
    {
        if (_current is null)
        {
            if (_queue.Count == 0)
            {
                return new NotificationFrame(NotificationPhase.Idle, null, 0f, false);
            }

            _current = _queue.Dequeue();
            _startedAtMs = nowMs;
            _paintPending = true;
        }

        var duration = _current.DurationMs;
        var fade = Math.Min(MaximumFadeMs, duration / 2);
        var elapsed = nowMs - _startedAtMs;

        if (elapsed >= duration)
        {
            // Finished: report Idle this tick rather than immediately starting
            // the next item, so every notification gets its own IsNewItem tick
            // and the caller never has to paint two panels within one call.
            _current = null;
            return new NotificationFrame(NotificationPhase.Idle, null, 0f, false);
        }

        var isNewItem = _paintPending;
        _paintPending = false;

        if (elapsed < fade)
        {
            var alpha = fade == 0 ? 1f : elapsed / (float)fade;
            return new NotificationFrame(
                NotificationPhase.FadingIn,
                _current,
                Math.Clamp(alpha, 0f, 1f),
                isNewItem);
        }

        if (elapsed < duration - fade)
        {
            return new NotificationFrame(NotificationPhase.Holding, _current, 1f, isNewItem);
        }

        var remaining = duration - elapsed;
        var fadeOutAlpha = fade == 0 ? 0f : remaining / (float)fade;
        return new NotificationFrame(
            NotificationPhase.FadingOut,
            _current,
            Math.Clamp(fadeOutAlpha, 0f, 1f),
            isNewItem);
    }
}
