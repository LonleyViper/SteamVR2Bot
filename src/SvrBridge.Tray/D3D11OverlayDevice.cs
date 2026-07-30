using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace SvrBridge.Tray;

/// <summary>
/// The one Direct3D 11 device every overlay's texture is allocated from.
/// <para>
/// <b>Shared, not one per overlay.</b> A device is a heavyweight object and
/// there is no isolation benefit between four overlays in the same process
/// that all upload at human rates. Textures stay per-overlay because the
/// surfaces genuinely differ in size - 512x768 chat, 1400x900 dashboard - but
/// nothing below the texture does.
/// </para>
/// <para>
/// <b>Device loss is recoverable, and that is the point of most of this
/// class.</b> A TDR or a driver update mid-session kills the device, and this
/// app can stay running for hours afterwards. Treating that as permanent
/// would mean the blink comes back and never leaves until a restart. So a
/// failure drops every overlay to <c>SetOverlayRaw</c> immediately, then the
/// device is retried on the same backoff schedule
/// <see cref="SvrBridge.Core.StreamerBotEventStream"/> uses for its WebSocket:
/// 1s, 2s, 5s, 10s, then 30s forever. Both transitions are logged.
/// </para>
/// <para>
/// Not thread-safe, deliberately: owned by the OpenVR worker's single poll
/// thread along with everything else that touches OpenVR.
/// </para>
/// </summary>
internal sealed class D3D11OverlayDevice : IOverlayTextureSource, IDisposable
{
    /// <summary>
    /// Backoff between device creation attempts; the last entry repeats
    /// forever. Deliberately the same schedule and shape as
    /// <c>StreamerBotEventStream.ReconnectDelays</c> - a driver coming back
    /// and a WebSocket coming back have the same character: usually seconds,
    /// occasionally never, and polling hard helps neither.
    /// </summary>
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly Action<string> _log;
    private readonly Func<TimeSpan> _clock;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private int _failedAttempts;
    private TimeSpan? _retryNotBefore;
    private bool _everCreated;
    private bool _disposed;

    /// <param name="clock">
    /// Monotonic elapsed time, injected so the backoff can be tested without
    /// waiting through it. Production passes a process-wide stopwatch.
    /// </param>
    public D3D11OverlayDevice(Action<string> log, Func<TimeSpan>? clock = null)
    {
        _log = log;
        _clock = clock ?? DefaultClock;
    }

    private static readonly Stopwatch ProcessClock = Stopwatch.StartNew();

    private static TimeSpan DefaultClock() => ProcessClock.Elapsed;

    /// <summary>True when a device exists right now. Diagnostics and self-tests only.</summary>
    public bool IsAvailable => _device is not null;

    /// <summary>
    /// The live device, for the self-test that reads a texture back. Null
    /// whenever <see cref="IsAvailable"/> is false.
    /// </summary>
    internal ID3D11Device? DeviceForTesting => _device;

    public IOverlayTexture? TryCreateTexture(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (_disposed || !TryEnsureDevice())
        {
            return null;
        }

        try
        {
            return new D3D11OverlayTexture(_device!, _context!, width, height);
        }
        catch (Exception exception)
        {
            // Creating a texture is the first thing that touches a freshly
            // created device, so this is where a half-dead device usually
            // shows itself rather than at creation.
            ReportDeviceLost(exception);
            return null;
        }
    }

    public void ReportDeviceLost(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        var hadDevice = _device is not null;
        DisposeDevice();
        _failedAttempts++;
        var delay = RetryDelays[Math.Min(_failedAttempts - 1, RetryDelays.Length - 1)];
        _retryNotBefore = _clock() + delay;

        if (hadDevice)
        {
            _log(
                "The Direct3D device for overlay textures was lost "
                + $"({exception.Message}). Overlays have dropped to SetOverlayRaw and will "
                + $"blink on repaint; retrying in {delay.TotalSeconds:0.#}s.");
        }
    }

    private bool TryEnsureDevice()
    {
        if (_device is not null)
        {
            return true;
        }

        if (_retryNotBefore is { } notBefore && _clock() < notBefore)
        {
            return false;
        }

        try
        {
            // BgraSupport costs nothing and keeps the door open for D2D/WPF
            // interop; the textures themselves are R8G8B8A8.
            var result = D3D11.D3D11CreateDevice(
                nint.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1],
                out var device,
                out var context);
            if (result.Failure || device is null || context is null)
            {
                device?.Dispose();
                context?.Dispose();
                ScheduleRetry($"device creation failed ({result})");
                return false;
            }

            _device = device;
            _context = context;
            _failedAttempts = 0;
            _retryNotBefore = null;
            _log(_everCreated
                ? "The Direct3D device for overlay textures is back. Overlays are returning to "
                  + "the blink-free SetOverlayTexture path."
                : "The Direct3D device for overlay textures is ready.");
            _everCreated = true;
            return true;
        }
        catch (Exception exception)
        {
            ScheduleRetry(exception.Message);
            return false;
        }
    }

    /// <summary>
    /// A failed <em>creation</em>, as opposed to a lost device. Same backoff,
    /// but only worth logging the first time and then on a long interval -
    /// a machine with no usable GPU would otherwise log forever.
    /// </summary>
    private void ScheduleRetry(string reason)
    {
        _failedAttempts++;
        var delay = RetryDelays[Math.Min(_failedAttempts - 1, RetryDelays.Length - 1)];
        _retryNotBefore = _clock() + delay;
        if (_failedAttempts == 1)
        {
            _log(
                $"No Direct3D device is available for overlay textures ({reason}). "
                + "Overlays will use SetOverlayRaw, which blinks on repaint.");
        }
    }

    private void DisposeDevice()
    {
        _context?.Dispose();
        _device?.Dispose();
        _context = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeDevice();
    }
}
