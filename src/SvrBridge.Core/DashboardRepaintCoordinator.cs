namespace SvrBridge.Core;

/// <summary>
/// Leading-edge coalescing repaint gate for the SteamVR dashboard, per §B3 of
/// the Phase 6 plan (<c>PHASE6_VR_TABS_PROMPT.md</c>).
/// <para>
/// The dashboard has no repaint throttle at all today, and every interaction
/// - including a Settings-page slider click - is a full texture write on the
/// CPU-upload path that visibly blinks (see <c>LIVE_TEST_RESULTS.md</c>'s
/// "known limitations" section). <see cref="ChatRepaintThrottle"/> already
/// solves the equivalent problem for the chat window, but this stays a
/// separate type rather than a shared one: the two consumers apply it very
/// differently (chat polls a message/image version number every tick;
/// the dashboard wraps a specific paint action per navigation event), and
/// keeping this class dashboard-only means nothing here can regress the
/// already-proven chat overlay.
/// </para>
/// <para>
/// <b>Leading edge, not trailing.</b> The first request in a burst paints
/// immediately - <see cref="Request"/> always attempts to flush inline, and
/// a fresh instance's throttle has nothing painted yet so that first attempt
/// always succeeds. Only requests that arrive before the throttle window has
/// elapsed since the last paint are deferred, and always to the *latest*
/// requested paint action - never a stale one, and never silently dropped:
/// <see cref="Flush"/> must be called again once the window elapses (on a
/// following tick) for a deferred paint to actually happen.
/// </para>
/// </summary>
public sealed class DashboardRepaintCoordinator
{
    private readonly ChatRepaintThrottle _throttle;
    private Action? _pendingRepaint;
    private long _version;

    public DashboardRepaintCoordinator(long minimumIntervalMs)
    {
        _throttle = new ChatRepaintThrottle(minimumIntervalMs);
    }

    /// <summary>
    /// Requests that <paramref name="repaint"/> run to reflect the latest
    /// state, replacing any not-yet-run request from an earlier call. Runs
    /// immediately if the throttle allows it; otherwise remembers it for a
    /// later <see cref="Flush"/> once the window elapses.
    /// </summary>
    public void Request(Action repaint, long nowMs)
    {
        _version++;
        _pendingRepaint = repaint;
        Flush(nowMs);
    }

    /// <summary>
    /// Runs the pending repaint if one is owed and the throttle window has
    /// elapsed since the last paint. Call this every tick regardless of
    /// whether <see cref="Request"/> was just called, so a repaint coalesced
    /// during a burst still reaches the screen once nothing more arrives.
    /// A no-op when nothing is pending or the window has not elapsed yet.
    /// </summary>
    public void Flush(long nowMs)
    {
        if (_pendingRepaint is null || !_throttle.ShouldRepaint(_version, nowMs))
        {
            return;
        }

        var repaint = _pendingRepaint;
        _pendingRepaint = null;
        _throttle.MarkPainted(_version, nowMs);
        repaint();
    }
}
