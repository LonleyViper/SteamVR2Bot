namespace SvrBridge.Tray;

/// <summary>Which of the two upload mechanisms a repaint actually used.</summary>
internal enum OverlayUploadPath
{
    /// <summary>
    /// <c>SetOverlayRaw</c>: SteamVR allocates and uploads a new texture per
    /// call. Blinks on every write - see the blink investigation and the D3D11
    /// spike in <c>LIVE_TEST_RESULTS.md</c>. Kept as the fallback, never
    /// deleted: it is what runs when no GPU device is available, and it is the
    /// control for any future comparison.
    /// </summary>
    Raw,

    /// <summary>
    /// <c>SetOverlayTexture</c> against a persistent Direct3D 11 texture
    /// written in place. Does not blink.
    /// </summary>
    Texture
}

/// <summary>
/// The overlay handle an upload lands on, abstracted so the routing in
/// <see cref="OverlayTextureUploader"/> can be tested without SteamVR. Both
/// implementations are thin adapters - one over
/// <see cref="SvrBridge.Core.VrOverlaySurface"/> for the regular overlays, one
/// over the dashboard handle inside <see cref="SvrBridge.Core.OpenVrInput"/>.
/// </summary>
internal interface IOverlayUploadTarget
{
    void SetRawTexture(byte[] rgba, int width, int height);

    void SetNativeTexture(nint nativeD3D11Texture);
}

/// <summary>One persistent GPU texture, sized once and written in place.</summary>
internal interface IOverlayTexture : IDisposable
{
    int Width { get; }

    int Height { get; }

    nint NativeTexture { get; }

    /// <summary>
    /// Replaces the contents with straight-alpha RGBA laid out tightly at
    /// <c>Width * 4</c> per row. Throws on device loss, which the caller is
    /// expected to report to the texture's source.
    /// </summary>
    void Write(byte[] rgba);
}

/// <summary>
/// Where textures come from. Returns null whenever no device is currently
/// available - never created, or lost and still inside the retry backoff - so
/// the caller falls back rather than failing.
/// </summary>
internal interface IOverlayTextureSource
{
    IOverlayTexture? TryCreateTexture(int width, int height);

    /// <summary>
    /// Reports that a texture operation failed, so the source can drop the
    /// device and start its recreate backoff. Every texture it handed out is
    /// dead after this.
    /// </summary>
    void ReportDeviceLost(Exception exception);
}

/// <summary>
/// Routes one overlay's repaints to the GPU texture path when a device is
/// available and to <c>SetOverlayRaw</c> when it is not, and survives the
/// transition in both directions.
/// <para>
/// One per overlay - it owns that overlay's texture, which is why textures are
/// per-overlay while the device behind them is shared: the surfaces differ in
/// size (512x768 chat, 1400x900 dashboard) but a Direct3D device is a
/// heavyweight object with no isolation benefit between them.
/// </para>
/// <para>
/// Not thread-safe, deliberately: every instance is owned by the OpenVR
/// worker's single poll thread, the same thread every other OpenVR call is
/// made from.
/// </para>
/// </summary>
internal sealed class OverlayTextureUploader : IDisposable
{
    private readonly IOverlayUploadTarget _target;
    private readonly IOverlayTextureSource _source;
    private readonly string _surfaceName;
    private readonly Action<string> _log;

    private IOverlayTexture? _texture;
    private bool _textureHandedToSteamVr;
    private OverlayUploadPath? _lastReportedPath;
    private bool _disposed;

    public OverlayTextureUploader(
        IOverlayUploadTarget target,
        IOverlayTextureSource source,
        string surfaceName,
        Action<string> log)
    {
        _target = target;
        _source = source;
        _surfaceName = surfaceName;
        _log = log;
    }

    /// <summary>
    /// Whether <c>SetOverlayTexture</c> is reissued on every write rather than
    /// only the first.
    /// <para>
    /// <b>Reissuing is required. Do not "optimise" it away.</b> This was tested
    /// in the headset on 2026-07-30 (see <c>LIVE_TEST_RESULTS.md</c>, the
    /// conversion matrix row 8): turning it off stopped the chat window
    /// updating entirely - "nothing comes through". Writing the persistent
    /// texture in place is not enough on its own, because SteamVR only ingests
    /// the contents at the <c>SetOverlayTexture</c> call itself. The texture
    /// still has to be persistent and never reallocated - that is what removes
    /// the blink - but the call still has to be made.
    /// </para>
    /// <para>
    /// Settable only so the same comparison can be rerun after a SteamVR
    /// update, on one surface, without a rebuild.
    /// </para>
    /// </summary>
    public bool ReissueTextureEveryWrite { get; set; } = true;

    /// <summary>
    /// Whether this surface may use the GPU texture path at all. False forces
    /// <c>SetOverlayRaw</c> even when a device is available.
    /// <para>
    /// <b>The SteamVR dashboard sets this false, and that is a finding, not a
    /// preference.</b> A <c>CreateDashboardOverlay</c> handle accepts
    /// <c>SetOverlayTexture</c> - it returns success every time - but never
    /// displays the result: the panel stays on whatever it had, so the wizard
    /// looked frozen and every click looked ignored. Every <em>regular</em>
    /// overlay works, including notifications, which repaint exactly as rarely
    /// as the dashboard does, so it is the overlay type and not the update
    /// rate. Measured in the headset on 2026-07-30 across three builds; see
    /// <c>LIVE_TEST_RESULTS.md</c>.
    /// </para>
    /// <para>
    /// Settable so the dashboard can be flipped back to the texture path from
    /// the tray's developer menu and re-checked after a SteamVR update,
    /// without a rebuild.
    /// </para>
    /// </summary>
    public bool TexturePathEnabled { get; set; } = true;

    /// <summary>The path the most recent <see cref="Upload"/> actually took.</summary>
    public OverlayUploadPath LastPath { get; private set; } = OverlayUploadPath.Raw;

    /// <summary>
    /// Uploads one frame of straight-alpha RGBA. Never throws for a graphics
    /// reason: a failure drops to <see cref="OverlayUploadPath.Raw"/> for this
    /// frame and tells the source, which starts its recovery backoff.
    /// </summary>
    public OverlayUploadPath Upload(byte[] rgba, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (TryUploadTexture(rgba, width, height))
        {
            Report(OverlayUploadPath.Texture);
            return OverlayUploadPath.Texture;
        }

        _target.SetRawTexture(rgba, width, height);
        Report(OverlayUploadPath.Raw);
        return OverlayUploadPath.Raw;
    }

    private bool TryUploadTexture(byte[] rgba, int width, int height)
    {
        if (!TexturePathEnabled)
        {
            return false;
        }

        // A size change means the old texture is the wrong shape. Reallocating
        // once here is fine and is not the per-frame reallocation the whole
        // design exists to avoid - in practice no surface resizes at all.
        if (_texture is not null && (_texture.Width != width || _texture.Height != height))
        {
            DiscardTexture();
        }

        _texture ??= AcquireTexture(width, height);
        if (_texture is null)
        {
            return false;
        }

        try
        {
            _texture.Write(rgba);
            if (ReissueTextureEveryWrite || !_textureHandedToSteamVr)
            {
                _target.SetNativeTexture(_texture.NativeTexture);
                _textureHandedToSteamVr = true;
            }

            return true;
        }
        catch (Exception exception)
        {
            // Could be device loss (a TDR, a driver update) or a SteamVR-side
            // rejection. Both are handled the same way: drop to raw now, let
            // the source decide when to try a device again.
            _log($"{_surfaceName}: the GPU texture upload failed ({exception.Message}).");
            DiscardTexture();
            _source.ReportDeviceLost(exception);
            return false;
        }
    }

    private IOverlayTexture? AcquireTexture(int width, int height)
    {
        var texture = _source.TryCreateTexture(width, height);
        if (texture is null)
        {
            return null;
        }

        // A fresh texture has never been handed over, even if a previous one
        // was. Missing this is how a recovered device ends up writing into a
        // texture SteamVR is not looking at - the overlay freezes on its last
        // frame with no error anywhere.
        _textureHandedToSteamVr = false;
        return texture;
    }

    private void DiscardTexture()
    {
        _texture?.Dispose();
        _texture = null;
        _textureHandedToSteamVr = false;
    }

    /// <summary>
    /// Logs only on a change of path, not per frame - these transitions happen
    /// at repaint rates and matter as events, not as a running commentary.
    /// </summary>
    private void Report(OverlayUploadPath path)
    {
        LastPath = path;
        if (_lastReportedPath == path)
        {
            return;
        }

        var first = _lastReportedPath is null;
        _lastReportedPath = path;
        if (first)
        {
            // Which path a surface starts on is worth one line each; neither
            // is a transition, and calling the first raw upload a "fallback"
            // would be wrong on a machine that never had a device at all.
            _log(path == OverlayUploadPath.Texture
                ? $"{_surfaceName}: uploading via SetOverlayTexture."
                : $"{_surfaceName}: uploading via SetOverlayRaw, which blinks on repaint.");
            return;
        }

        _log(path == OverlayUploadPath.Texture
            ? $"{_surfaceName}: back on SetOverlayTexture - the blink-free path."
            : $"{_surfaceName}: fell back to SetOverlayRaw - repaints will blink until a GPU device returns.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DiscardTexture();
    }
}

/// <summary>
/// Adapts a <see cref="SvrBridge.Core.VrOverlaySurface"/> - the regular
/// overlays - to <see cref="IOverlayUploadTarget"/>.
/// </summary>
internal sealed class VrOverlaySurfaceUploadTarget(SvrBridge.Core.VrOverlaySurface surface)
    : IOverlayUploadTarget
{
    public void SetRawTexture(byte[] rgba, int width, int height) =>
        surface.SetTexture(rgba, width, height);

    public void SetNativeTexture(nint nativeD3D11Texture) =>
        surface.SetD3D11Texture(nativeD3D11Texture);
}

/// <summary>
/// Adapts the SteamVR dashboard overlay - which is not a
/// <see cref="SvrBridge.Core.VrOverlaySurface"/>, because it comes from
/// <c>CreateDashboardOverlay</c> and lives as a handle inside
/// <see cref="SvrBridge.Core.OpenVrInput"/> - to the same seam.
/// </summary>
internal sealed class DashboardUploadTarget(SvrBridge.Core.OpenVrInput openVr)
    : IOverlayUploadTarget
{
    public void SetRawTexture(byte[] rgba, int width, int height) =>
        openVr.SetDashboardTexture(rgba, width, height);

    public void SetNativeTexture(nint nativeD3D11Texture) =>
        openVr.SetDashboardD3D11Texture(nativeD3D11Texture);
}
