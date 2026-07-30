using System.Runtime.InteropServices;

namespace SvrBridge.Core;

/// <summary>
/// A rigid transform in OpenVR's own 3x4 layout: three rows of four floats
/// where the fourth column is the translation in metres.
/// <para>
/// Deliberately not <see cref="System.Numerics.Matrix4x4"/>. That type is
/// row-vector with translation in the fourth <em>row</em>, while OpenVR is
/// column-vector with translation in the fourth <em>column</em>, so converting
/// between them means transposing the rotation and moving the translation. That
/// is a silent-wrongness conversion - it compiles, it runs, and the overlay
/// simply hangs in the wrong place at the wrong angle. Mirroring the native
/// layout exactly removes the conversion rather than documenting it.
/// </para>
/// </summary>
public readonly record struct VrOverlayTransform(
    float M00, float M01, float M02, float M03,
    float M10, float M11, float M12, float M13,
    float M20, float M21, float M22, float M23)
{
    public static VrOverlayTransform Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);

    /// <summary>
    /// Metres, in the tracked device's own frame: +X right, +Y up, +Z back
    /// towards the wearer. A controller's origin sits roughly at the handle, so
    /// a wrist panel wants a small -Z and a small +Y.
    /// </summary>
    public static VrOverlayTransform Translation(float x, float y, float z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z);

    /// <summary>Pitch, in radians. Positive tips the top of the panel away.</summary>
    public static VrOverlayTransform RotationX(float radians)
    {
        var (sin, cos) = MathF.SinCos(radians);
        return new VrOverlayTransform(
            1, 0, 0, 0,
            0, cos, -sin, 0,
            0, sin, cos, 0);
    }

    /// <summary>Yaw, in radians.</summary>
    public static VrOverlayTransform RotationY(float radians)
    {
        var (sin, cos) = MathF.SinCos(radians);
        return new VrOverlayTransform(
            cos, 0, sin, 0,
            0, 1, 0, 0,
            -sin, 0, cos, 0);
    }

    /// <summary>Roll, in radians.</summary>
    public static VrOverlayTransform RotationZ(float radians)
    {
        var (sin, cos) = MathF.SinCos(radians);
        return new VrOverlayTransform(
            cos, -sin, 0, 0,
            sin, cos, 0, 0,
            0, 0, 1, 0);
    }

    /// <summary>
    /// Composition, read right to left as usual: <c>a * b</c> applies b first.
    /// The implied fourth row is (0, 0, 0, 1), so this is an ordinary 4x4
    /// multiply with the constant row elided.
    /// </summary>
    public static VrOverlayTransform operator *(VrOverlayTransform a, VrOverlayTransform b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M00 * b.M03) + (a.M01 * b.M13) + (a.M02 * b.M23) + a.M03,
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M10 * b.M03) + (a.M11 * b.M13) + (a.M12 * b.M23) + a.M13,
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22),
        (a.M20 * b.M03) + (a.M21 * b.M13) + (a.M22 * b.M23) + a.M23);
}

/// <summary>
/// The overlay calls <see cref="VrOverlaySurface"/> needs, separated from the
/// raw function table so the vtable indices, the delegate types and the
/// marshalling all stay inside <see cref="OpenVrInput"/>. Every method throws
/// on a non-zero <c>EVROverlayError</c>.
/// </summary>
internal interface IVrOverlayApi
{
    ulong CreateOverlay(string key, string name);

    /// <summary>False when no overlay with that key exists, which is not an error.</summary>
    bool TryFindOverlay(string key, out ulong handle);

    void DestroyOverlay(ulong handle);

    void SetOverlayRaw(ulong handle, nint buffer, uint width, uint height, uint bytesPerPixel);

    /// <summary>
    /// The GPU counterpart of <see cref="SetOverlayRaw"/>: hands SteamVR a
    /// native <c>ID3D11Texture2D</c> it holds a reference to, rather than a
    /// CPU buffer it copies out of. Takes no dimensions, because the texture
    /// carries its own.
    /// </summary>
    void SetOverlayTexture(ulong handle, nint nativeD3D11Texture);

    void SetOverlayWidthInMeters(ulong handle, float widthInMeters);

    void ShowOverlay(ulong handle);

    void HideOverlay(ulong handle);

    bool IsOverlayVisible(ulong handle);

    void SetOverlayAlpha(ulong handle, float alpha);

    void SetOverlaySortOrder(ulong handle, uint sortOrder);

    void SetOverlayCurvature(ulong handle, float curvature);

    void SetOverlayTransformTrackedDeviceRelative(
        ulong handle,
        uint deviceIndex,
        VrOverlayTransform transform);
}

/// <summary>
/// One SteamVR overlay: its handle, its texture, and where it sits.
/// <para>
/// This is the substrate the chat window and the notification queue are both
/// built on, so it is <b>multi-instance by construction</b>. The dashboard is
/// baked into <see cref="OpenVrInput"/> as a single handle field, which is
/// exactly the shape that would have to be unpicked later once a second and
/// third overlay exist. Nothing here is static and nothing assumes it is alone.
/// </para>
/// <para>
/// Disposal is not optional housekeeping. An overlay handle that outlives its
/// owner stays registered with SteamVR until the process exits - it keeps its
/// key reserved, so recreating the same surface fails with
/// <c>KeyInUse</c>, and it counts against the per-application overlay limit.
/// A leak here is visible to the user as an overlay that will not come back.
/// </para>
/// </summary>
public sealed class VrOverlaySurface : IDisposable
{
    private readonly IVrOverlayApi _api;
    private ulong _handle;
    private bool _disposed;

    internal VrOverlaySurface(IVrOverlayApi api, string key, ulong handle)
    {
        _api = api;
        Key = key;
        _handle = handle;
    }

    /// <summary>The key this overlay was registered under, unique per process.</summary>
    public string Key { get; }

    /// <summary>The raw <c>VROverlayHandle_t</c>, for diagnostics and self-tests.</summary>
    public ulong Handle => _handle;

    public bool IsVisible => !_disposed && _api.IsOverlayVisible(_handle);

    /// <summary>
    /// Uploads a texture from a managed buffer. The pixel order SteamVR expects
    /// is RGBA, which is the opposite of the BGRA that a GDI+
    /// <c>Format32bppArgb</c> bitmap produces, so a caller handing over bitmap
    /// bytes has to swap the channels first.
    /// </summary>
    public void SetTexture(byte[] pixels, int width, int height, int bytesPerPixel = 4)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ValidateTextureSize(width, height, bytesPerPixel);

        var required = (long)width * height * bytesPerPixel;
        if (pixels.Length < required)
        {
            throw new ArgumentException(
                $"The pixel buffer holds {pixels.Length} bytes but "
                + $"{width}x{height} at {bytesPerPixel} bytes per pixel needs {required}.",
                nameof(pixels));
        }

        // SteamVR reads the buffer synchronously and keeps no reference, so the
        // pin only has to outlive the call - but it absolutely has to do that.
        // Without it a collection during the call would hand SteamVR a pointer
        // into moved memory.
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            SetTexture(pin.AddrOfPinnedObject(), width, height, bytesPerPixel);
        }
        finally
        {
            pin.Free();
        }
    }

    /// <summary>
    /// Uploads a texture from memory the caller already owns and has pinned -
    /// a GDI+ <c>BitmapData.Scan0</c>, for instance. The buffer must stay valid
    /// until this returns.
    /// </summary>
    public void SetTexture(nint buffer, int width, int height, int bytesPerPixel = 4)
    {
        ThrowIfDisposed();
        ValidateTextureSize(width, height, bytesPerPixel);
        if (buffer == nint.Zero)
        {
            throw new ArgumentException("The pixel buffer pointer is null.", nameof(buffer));
        }

        _api.SetOverlayRaw(_handle, buffer, (uint)width, (uint)height, (uint)bytesPerPixel);
    }

    /// <summary>
    /// The alternative upload path added by the D3D11 texture spike: hands
    /// SteamVR a native <c>ID3D11Texture2D</c> it keeps a reference to,
    /// instead of the CPU buffer <see cref="SetTexture(nint, int, int, int)"/>
    /// makes SteamVR allocate and copy on every call.
    /// <para>
    /// Deliberately takes a raw pointer rather than any graphics type. The
    /// device, the texture and their lifetime belong to the caller - this
    /// assembly has no graphics dependency and is not acquiring one. The
    /// texture must outlive every frame SteamVR draws with it, which in
    /// practice means until the overlay is destroyed or another texture
    /// replaces it.
    /// </para>
    /// </summary>
    public void SetD3D11Texture(nint nativeD3D11Texture)
    {
        ThrowIfDisposed();
        if (nativeD3D11Texture == nint.Zero)
        {
            throw new ArgumentException(
                "The native D3D11 texture pointer is null.",
                nameof(nativeD3D11Texture));
        }

        _api.SetOverlayTexture(_handle, nativeD3D11Texture);
    }

    /// <summary>
    /// Width in metres. Height follows from the texture's aspect ratio, so this
    /// is the only size control there is.
    /// </summary>
    public void SetWidthInMeters(float widthInMeters)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(widthInMeters) || widthInMeters <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthInMeters),
                widthInMeters,
                "An overlay width must be a positive number of metres.");
        }

        _api.SetOverlayWidthInMeters(_handle, widthInMeters);
    }

    public void Show()
    {
        ThrowIfDisposed();
        _api.ShowOverlay(_handle);
    }

    public void Hide()
    {
        ThrowIfDisposed();
        _api.HideOverlay(_handle);
    }

    /// <summary>Opacity from 0 to 1. Clamped rather than rejected, because this is animated.</summary>
    public void SetAlpha(float alpha)
    {
        ThrowIfDisposed();
        _api.SetOverlayAlpha(_handle, Math.Clamp(alpha, 0f, 1f));
    }

    /// <summary>Higher sorts in front when two overlays occupy the same space.</summary>
    public void SetSortOrder(uint sortOrder)
    {
        ThrowIfDisposed();
        _api.SetOverlaySortOrder(_handle, sortOrder);
    }

    /// <summary>
    /// 0 is flat and 1 wraps the panel into a full cylinder around the viewer.
    /// Small values measurably help readability at wrist distance.
    /// </summary>
    public void SetCurvature(float curvature)
    {
        ThrowIfDisposed();
        _api.SetOverlayCurvature(_handle, Math.Clamp(curvature, 0f, 1f));
    }

    /// <summary>
    /// Pins the overlay to a tracked device, so it moves with that device
    /// without this app having to write a pose every frame.
    /// <para>
    /// The device index must be re-resolved rather than cached. Indices are not
    /// stable across controller sleep, reconnect or a battery change, and a
    /// role can come back unassigned. Binding to an index that later goes stale
    /// detaches the overlay silently - no error is returned, the panel simply
    /// stops following the hand. Callers should re-apply this whenever the
    /// resolved index changes.
    /// </para>
    /// </summary>
    public void AttachToDevice(uint deviceIndex, VrOverlayTransform transform)
    {
        ThrowIfDisposed();
        _api.SetOverlayTransformTrackedDeviceRelative(_handle, deviceIndex, transform);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var handle = _handle;
        _handle = 0;
        if (handle == 0)
        {
            return;
        }

        try
        {
            _api.DestroyOverlay(handle);
        }
        catch (Exception)
        {
            // SteamVR may already have gone away, which is the common case
            // during shutdown. Faulting here would mask whatever is actually
            // tearing the session down.
        }
    }

    private static void ValidateTextureSize(int width, int height, int bytesPerPixel)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        }

        // SteamVR accepts 3 and 4; anything else is a caller mistake that would
        // otherwise be read as a stride and produce a sheared image.
        if (bytesPerPixel is not (3 or 4))
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerPixel),
                bytesPerPixel,
                "A raw overlay texture must be 3 or 4 bytes per pixel.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
