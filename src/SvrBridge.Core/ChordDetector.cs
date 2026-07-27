namespace SvrBridge.Core;

public sealed class ChordDetector
{
    private readonly ChordMode _mode;
    private readonly long _windowMs;
    private readonly long _cooldownMs;
    private readonly long _holdMs;

    private bool _previousOne;
    private bool _previousTwo;
    private bool _latched;
    private bool _waitingForRelease;
    private long _onePressedAt = long.MinValue;
    private long _twoPressedAt = long.MinValue;
    private long _lastFiredAt = long.MinValue;

    public ChordDetector(
        ChordConfig config,
        bool requireReleaseBeforeArmed = false)
    {
        _mode = config.Mode;
        _windowMs = config.WindowMs;
        _cooldownMs = config.CooldownMs;
        _holdMs = config.HoldMs;
        _waitingForRelease = requireReleaseBeforeArmed;
    }

    public bool Update(bool buttonOne, bool buttonTwo, long nowMs)
    {
        if (_waitingForRelease)
        {
            _previousOne = buttonOne;
            _previousTwo = buttonTwo;
            _waitingForRelease = buttonOne || buttonTwo;
            return false;
        }

        var onePressed = buttonOne && !_previousOne;
        var twoPressed = buttonTwo && !_previousTwo;
        var previousOnePressedAt = _onePressedAt;

        if (onePressed)
        {
            _onePressedAt = nowMs;
        }

        if (twoPressed)
        {
            _twoPressedAt = nowMs;
        }

        if (_mode
                is ChordMode.SinglePress
                or ChordMode.LongPress
                or ChordMode.DoublePress
                ? !buttonOne
                : !buttonOne || !buttonTwo)
        {
            _latched = false;
        }

        var validGesture = _mode switch
        {
            ChordMode.SinglePress => onePressed,
            ChordMode.Modifier => buttonOne && twoPressed,
            ChordMode.Simultaneous => buttonOne
                                      && buttonTwo
                                      && Math.Abs(_onePressedAt - _twoPressedAt) <= _windowMs,
            ChordMode.LongPress => buttonOne
                                   && nowMs - _onePressedAt >= _holdMs,
            ChordMode.DoublePress => onePressed
                                     && previousOnePressedAt != long.MinValue
                                     && nowMs - previousOnePressedAt <= _windowMs,
            _ => false
        };

        var outsideCooldown = _lastFiredAt == long.MinValue
                              || nowMs - _lastFiredAt >= _cooldownMs;
        var fired = validGesture && !_latched && outsideCooldown;

        if (fired)
        {
            _latched = true;
            _lastFiredAt = nowMs;
        }

        _previousOne = buttonOne;
        _previousTwo = buttonTwo;
        return fired;
    }
}
