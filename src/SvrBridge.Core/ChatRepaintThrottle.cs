namespace SvrBridge.Core;

/// <summary>
/// Decides whether a chat repaint is owed, per §B2 of the chat plan: repaint
/// only when the message list actually changed, throttled to roughly 10 Hz,
/// coalescing a burst of messages into one repaint rather than one per
/// message.
/// <para>
/// Pure and deliberately separated from the decision to actually repaint -
/// see <c>ChatOverlay.RepaintIfOwed</c> in <c>SvrBridge.Tray</c> - so the
/// coalescing behaviour can be proven with plain integers and no render
/// thread or headset.
/// </para>
/// </summary>
public sealed class ChatRepaintThrottle
{
    private readonly long _minimumIntervalMs;
    private long _lastPaintedVersion = -1;
    // Null rather than a sentinel like long.MinValue: nowMs - long.MinValue
    // overflows a signed long for any nowMs >= 0, which would make the very
    // first ShouldRepaint call after construction spuriously return false.
    private long? _lastPaintedAtMs;

    public ChatRepaintThrottle(long minimumIntervalMs = 100)
    {
        if (minimumIntervalMs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumIntervalMs),
                minimumIntervalMs,
                "The repaint interval cannot be negative.");
        }

        _minimumIntervalMs = minimumIntervalMs;
    }

    /// <summary>
    /// True when <paramref name="currentVersion"/> differs from the version
    /// last recorded by <see cref="MarkPainted"/> and the throttle interval
    /// has elapsed since then. Side-effect-free: calling this repeatedly
    /// without ever calling <see cref="MarkPainted"/> keeps returning the
    /// same answer, which is what lets a caller poll it every tick.
    /// </summary>
    public bool ShouldRepaint(long currentVersion, long nowMs) =>
        currentVersion != _lastPaintedVersion
        && (_lastPaintedAtMs is not { } last || nowMs - last >= _minimumIntervalMs);

    /// <summary>
    /// Records that a repaint just happened at <paramref name="version"/>.
    /// Call this only when a repaint was actually performed - it is the
    /// throttle's entire memory of the past.
    /// </summary>
    public void MarkPainted(long version, long nowMs)
    {
        _lastPaintedVersion = version;
        _lastPaintedAtMs = nowMs;
    }
}
