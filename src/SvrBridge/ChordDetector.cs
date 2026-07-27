namespace SvrBridge;

internal sealed class ChordDetector
{
    private readonly ChordMode _mode;
    private readonly long _windowMs;
    private readonly long _cooldownMs;

    private bool _previousOne;
    private bool _previousTwo;
    private bool _latched;
    private long _onePressedAt = long.MinValue;
    private long _twoPressedAt = long.MinValue;
    private long _lastFiredAt = long.MinValue;

    public ChordDetector(ChordConfig config)
    {
        _mode = config.Mode;
        _windowMs = config.WindowMs;
        _cooldownMs = config.CooldownMs;
    }

    public bool Update(bool buttonOne, bool buttonTwo, long nowMs)
    {
        var onePressed = buttonOne && !_previousOne;
        var twoPressed = buttonTwo && !_previousTwo;

        if (onePressed)
        {
            _onePressedAt = nowMs;
        }

        if (twoPressed)
        {
            _twoPressedAt = nowMs;
        }

        if (!buttonOne || !buttonTwo)
        {
            _latched = false;
        }

        var validGesture = _mode switch
        {
            ChordMode.Modifier => buttonOne && twoPressed,
            ChordMode.Simultaneous => buttonOne
                                      && buttonTwo
                                      && Math.Abs(_onePressedAt - _twoPressedAt) <= _windowMs,
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

