using System.Numerics;

namespace SvrBridge.Core;

/// <summary>
/// One observation of a SteamVR digital action taken during a probe poll.
/// </summary>
public readonly record struct ProbeActionState(
    string Name,
    int ErrorCode,
    bool Active,
    bool State,
    bool Changed,
    ulong ActiveOrigin);

/// <summary>
/// Read-only diagnostics for focused-dashboard input capture.
/// <para>
/// The probe never changes input delivery. It records state transitions only,
/// plus one summary per second, so a 10 ms poll loop cannot flood the log.
/// Enable it while the recorder page is showing and disable it everywhere else.
/// </para>
/// </summary>
public sealed class InputProbe(Action<string> log)
{
    private const long SummaryIntervalMs = 1000;

    private readonly Action<string> _log = log;
    private readonly Dictionary<string, string> _actionSignatures =
        new(StringComparer.Ordinal);
    private readonly HashSet<int> _reportedOverlayEventTypes = [];
    private readonly List<string> _pressedActions = [];
    private string _dashboardSignature = "";
    private string _trackingSignature = "";
    private MotionSample _lastMotion;
    private int _actionCount;
    private int _activeActionCount;
    private long _overlayEventCount;
    private long _nextSummaryAt;

    public bool Enabled { get; private set; }

    /// <summary>
    /// Turns probing on or off. Cached transitions are cleared either way so
    /// the next session reports a full picture instead of silently resuming.
    /// </summary>
    public void SetEnabled(bool enabled, long nowMs)
    {
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        _actionSignatures.Clear();
        _reportedOverlayEventTypes.Clear();
        _pressedActions.Clear();
        _dashboardSignature = "";
        _trackingSignature = "";
        _lastMotion = default;
        _actionCount = 0;
        _activeActionCount = 0;
        _overlayEventCount = 0;
        _nextSummaryAt = nowMs + SummaryIntervalMs;
        _log(
            enabled
                ? "input probe: on (recorder page)."
                : "input probe: off.");
    }

    /// <summary>Starts a poll. Resets the per-poll action tally only.</summary>
    public void BeginPoll()
    {
        if (!Enabled)
        {
            return;
        }

        _actionCount = 0;
        _activeActionCount = 0;
        _pressedActions.Clear();
    }

    public void ObserveDashboard(bool visible, bool active)
    {
        if (!Enabled)
        {
            return;
        }

        var signature = $"visible={Bit(visible)} active={Bit(active)}";
        if (signature == _dashboardSignature)
        {
            return;
        }

        _dashboardSignature = signature;
        _log($"input probe dashboard: {signature}.");
    }

    public void ObserveAction(ProbeActionState state)
    {
        if (!Enabled)
        {
            return;
        }

        _actionCount++;
        if (state.Active)
        {
            _activeActionCount++;
        }

        if (state.State)
        {
            _pressedActions.Add(state.Name);
        }

        var signature =
            $"err={state.ErrorCode} active={Bit(state.Active)} " +
            $"state={Bit(state.State)} changed={Bit(state.Changed)} " +
            $"origin=0x{state.ActiveOrigin:X}";
        if (_actionSignatures.TryGetValue(state.Name, out var previous)
            && previous == signature)
        {
            return;
        }

        _actionSignatures[state.Name] = signature;
        _log($"input probe action {state.Name}: {signature}.");
    }

    /// <summary>
    /// Records a motion sample. Poses change on every poll, so only the
    /// tracking-validity transition is logged immediately; the numbers ride
    /// along on the once-per-second summary.
    /// </summary>
    public void ObserveMotion(MotionSample sample)
    {
        if (!Enabled)
        {
            return;
        }

        _lastMotion = sample;
        var signature = sample.Tracking.ToString();
        if (signature == _trackingSignature)
        {
            return;
        }

        _trackingSignature = signature;
        _log($"input probe tracking: {signature}.");
    }

    /// <summary>
    /// Records an event pulled from the dashboard overlay queue. Controller
    /// button events are always logged because they are the thing we are
    /// hunting; every other event type is logged once per session so we can
    /// see what the queue delivers without drowning in mouse movement.
    /// </summary>
    public void ObserveOverlayEvent(
        int eventType,
        uint deviceIndex,
        uint button,
        string friendlyName)
    {
        if (!Enabled)
        {
            return;
        }

        _overlayEventCount++;
        var name = OverlayEventName(eventType);
        if (name is not null)
        {
            _log(
                $"input probe overlay {name}: device={deviceIndex} " +
                $"button={button} ({friendlyName}).");
            return;
        }

        if (_reportedOverlayEventTypes.Add(eventType))
        {
            _log($"input probe overlay event {eventType}: first seen this session.");
        }
    }

    /// <summary>
    /// Ends a poll and emits the once-per-second liveness summary, so an empty
    /// log means "nothing arrived" rather than "the probe stopped running".
    /// </summary>
    public void EndPoll(long nowMs)
    {
        if (!Enabled || nowMs < _nextSummaryAt)
        {
            return;
        }

        _nextSummaryAt = nowMs + SummaryIntervalMs;
        var pressed = _pressedActions.Count == 0
            ? "none"
            : string.Join(", ", _pressedActions);
        _log(
            $"input probe: dashboard {Fallback(_dashboardSignature)} | " +
            $"actions {_activeActionCount}/{_actionCount} active, pressed {pressed} | " +
            $"overlay events {_overlayEventCount} | " +
            $"{DescribeMotion()}.");
    }

    /// <summary>
    /// Body-frame hand positions in metres and hand speeds in metres per
    /// second: +X right, +Y up, +Z in front of the wearer.
    /// </summary>
    private string DescribeMotion() =>
        _lastMotion.Tracking == MotionTracking.None
            ? "motion none"
            : $"motion {_trackingSignature} head {_lastMotion.HeadHeightMeters:0.00}m " +
              $"L {DescribeHand(_lastMotion.LeftPosition, _lastMotion.LeftVelocity)} " +
              $"R {DescribeHand(_lastMotion.RightPosition, _lastMotion.RightVelocity)}";

    private static string DescribeHand(Vector3 position, Vector3 velocity) =>
        $"({position.X:0.00},{position.Y:0.00},{position.Z:0.00})" +
        $"@{velocity.Length():0.00}m/s";

    public static string? OverlayEventName(int eventType) =>
        eventType switch
        {
            200 => "ButtonPress",
            201 => "ButtonUnpress",
            202 => "ButtonTouch",
            203 => "ButtonUntouch",
            _ => null
        };

    private static string Fallback(string signature) =>
        string.IsNullOrEmpty(signature) ? "unknown" : signature;

    private static string Bit(bool value) => value ? "1" : "0";
}
