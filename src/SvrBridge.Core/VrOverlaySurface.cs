using System.Numerics;
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
    /// The inverse of a <b>rigid</b> transform - one whose rotation block is
    /// orthonormal, which every tracked-device pose and every composition of
    /// them is.
    /// <para>
    /// Deliberately not a general matrix inverse. For a rigid transform the
    /// answer is the transposed rotation and the negated, re-rotated
    /// translation, which is exact, allocation-free and cannot be
    /// ill-conditioned - where a general inverse would need a determinant that
    /// is always 1 here anyway. It is named for the precondition so nobody
    /// reaches for it on a matrix carrying scale or shear, where it silently
    /// returns the wrong answer rather than failing.
    /// </para>
    /// </summary>
    public VrOverlayTransform InverseRigid() => new(
        M00, M10, M20, -((M00 * M03) + (M10 * M13) + (M20 * M23)),
        M01, M11, M21, -((M01 * M03) + (M11 * M13) + (M21 * M23)),
        M02, M12, M22, -((M02 * M03) + (M12 * M13) + (M22 * M23)));

    /// <summary>Applies this OpenVR column-vector transform to a position.</summary>
    public Vector3 TransformPoint(Vector3 point) => new(
        (M00 * point.X) + (M01 * point.Y) + (M02 * point.Z) + M03,
        (M10 * point.X) + (M11 * point.Y) + (M12 * point.Z) + M13,
        (M20 * point.X) + (M21 * point.Y) + (M22 * point.Z) + M23);

    /// <summary>Applies this transform's rotation to a direction.</summary>
    public Vector3 TransformDirection(Vector3 direction) => new(
        (M00 * direction.X) + (M01 * direction.Y) + (M02 * direction.Z),
        (M10 * direction.X) + (M11 * direction.Y) + (M12 * direction.Z),
        (M20 * direction.X) + (M21 * direction.Y) + (M22 * direction.Z));

    /// <summary>
    /// Whether this is a transform SteamVR can actually place a panel with:
    /// every element finite, <b>and</b> a rotation block that is really a
    /// rotation.
    /// <para>
    /// The finiteness half catches a pose read during a tracking dropout. The
    /// orthonormality half catches something subtler and, on the evidence,
    /// more likely: a value that is structurally present but meaningless. An
    /// all-zero matrix is finite, deserialises without complaint from JSON
    /// that simply had different property names, and is accepted by SteamVR
    /// without an error - it collapses the overlay quad to nothing, so the
    /// panel does not move or misdraw, it silently ceases to exist. That is
    /// exactly what a settings file written before this type changed shape
    /// produced, and no amount of "did an exception happen" tells you so.
    /// </para>
    /// <para>
    /// The tolerance is loose because the legitimate values here are tracked
    /// device poses composed with inverses of tracked device poses, which
    /// accumulate real float error; it only has to be tight enough to reject
    /// garbage, and zero is not a close call.
    /// </para>
    /// </summary>
    public bool IsUsable()
    {
        if (!ToFloats().All(float.IsFinite))
        {
            return false;
        }

        const float tolerance = 0.01f;
        return IsUnit(M00, M01, M02)
               && IsUnit(M10, M11, M12)
               && IsUnit(M20, M21, M22)
               && IsPerpendicular(M00, M01, M02, M10, M11, M12)
               && IsPerpendicular(M00, M01, M02, M20, M21, M22)
               && IsPerpendicular(M10, M11, M12, M20, M21, M22);

        static bool IsUnit(float x, float y, float z) =>
            MathF.Abs(MathF.Sqrt((x * x) + (y * y) + (z * z)) - 1f) <= tolerance;

        static bool IsPerpendicular(
            float ax, float ay, float az,
            float bx, float by, float bz) =>
            MathF.Abs((ax * bx) + (ay * by) + (az * bz)) <= tolerance;
    }

    /// <summary>
    /// This transform with its translation column brought inside
    /// <paramref name="limitMeters"/> on every axis, rotation untouched.
    /// </summary>
    public VrOverlayTransform WithTranslationClamped(float limitMeters) => this with
    {
        M03 = Math.Clamp(M03, -limitMeters, limitMeters),
        M13 = Math.Clamp(M13, -limitMeters, limitMeters),
        M23 = Math.Clamp(M23, -limitMeters, limitMeters)
    };

    /// <summary>The twelve elements in declaration order - for persistence and validation, not arithmetic.</summary>
    public float[] ToFloats() =>
    [
        M00, M01, M02, M03,
        M10, M11, M12, M13,
        M20, M21, M22, M23
    ];

    /// <summary>Rebuilds one from twelve elements at <paramref name="offset"/> - the inverse of <see cref="ToFloats"/>.</summary>
    public static VrOverlayTransform FromFloats(IReadOnlyList<float> values, int offset) => new(
        values[offset], values[offset + 1], values[offset + 2], values[offset + 3],
        values[offset + 4], values[offset + 5], values[offset + 6], values[offset + 7],
        values[offset + 8], values[offset + 9], values[offset + 10], values[offset + 11]);

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

/// <summary>Which laser-pointer event SteamVR delivered to an overlay.</summary>
public enum OverlayMouseEventKind
{
    /// <summary>The pointer moved across the panel.</summary>
    Move,

    /// <summary>The trigger went down while pointing at the panel.</summary>
    ButtonDown,

    /// <summary>The trigger came back up.</summary>
    ButtonUp,

    /// <summary>A SteamVR discrete controller scroll event.</summary>
    Scroll,

    /// <summary>The laser left the panel entirely.</summary>
    FocusLeave
}

/// <summary>
/// One laser-pointer event on an overlay.
/// <para>
/// <see cref="X"/> and <see cref="Y"/> are in whatever space
/// <see cref="VrOverlaySurface.SetMouseScale"/> established, <b>exactly as
/// SteamVR reported them</b> - which puts the origin at the bottom-left, the
/// opposite of every panel this app draws. Flipping is left to the caller,
/// which is the only layer that knows the panel's height; see
/// <c>ChatOverlay</c>, and the same flip the dashboard already applies in
/// <c>OpenVrInput.TryGetDashboardInteraction</c>.
/// </para>
/// <para>
/// <see cref="DeviceIndex"/> is the controller that generated the event, which
/// is how a caller tells which hand is doing the pointing. It can be
/// <c>k_unTrackedDeviceIndexInvalid</c>, so callers must have an answer for
/// "no hand resolved".
/// </para>
/// </summary>
public readonly record struct OverlayMouseEvent(
    OverlayMouseEventKind Kind,
    float X,
    float Y,
    uint DeviceIndex,
    float ScrollY = 0);

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

    /// <summary>0 is <c>None</c>, 1 is <c>Mouse</c> - the laser pointer.</summary>
    void SetOverlayInputMethod(ulong handle, int inputMethod);

    /// <summary>
    /// One <c>VROverlayFlags</c> value, passed as the bit constant
    /// <c>openvr.h</c> defines it as - see the existing
    /// <c>SendVRDiscreteScrollEvents</c> (<c>1 &lt;&lt; 6</c>) call on the
    /// dashboard.
    /// </summary>
    void SetOverlayFlag(ulong handle, int flag, bool enabled);

    /// <summary>
    /// The coordinate space overlay mouse events are reported in. Setting it
    /// to the panel's own pixel size is what lets a hit test compare an event
    /// straight against the rectangle table the renderer drew from.
    /// </summary>
    void SetOverlayMouseScale(ulong handle, float width, float height);

    /// <summary>
    /// Drains this overlay's event queue, appending every laser-pointer event
    /// to <paramref name="into"/> and discarding the rest. Appends rather than
    /// returning a new list so a caller polling every tick can reuse one
    /// buffer.
    /// </summary>
    void PollOverlayMouseEvents(ulong handle, List<OverlayMouseEvent> into);

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
    /// Turns the SteamVR laser pointer on or off for this overlay.
    /// <para>
    /// <b>This is not a feature, it is a probe, and it can take input away from
    /// a running VR game.</b> Whether an overlay that accepts laser input
    /// swallows the trigger from the game underneath it is the open question
    /// the next phase's design depends on - see the probe in
    /// <c>LIVE_TEST_RESULTS.md</c>. Nothing turns this on in normal operation,
    /// and the one caller that does turns it off again on a timer precisely
    /// because the failure mode is "the wearer cannot use their game".
    /// </para>
    /// <para>
    /// Related and already known: this app's global-priority action set
    /// already takes grip, trigger, trackpad and menu from a running game -
    /// see the action-set priority note. That is a separate mechanism from
    /// this one, and a result here has to be read against it rather than
    /// confused with it.
    /// </para>
    /// </summary>
    public void SetAcceptsLaserInput(bool accepts)
    {
        ThrowIfDisposed();
        _api.SetOverlayInputMethod(_handle, accepts ? 1 : 0);
    }

    /// <summary>
    /// <c>VROverlayFlags_MakeOverlaysInteractiveIfVisible</c>, whose value
    /// <c>openvr.h</c> gives as <c>1 &lt;&lt; 16</c>.
    /// <para>
    /// <b>This is the switch that actually turns the laser on, and
    /// <see cref="SetAcceptsLaserInput"/> is not.</b> The header is explicit
    /// about the division of labour: "if this is set and the overlay's input
    /// method is not none, the system-wide laser mouse mode will be activated
    /// whenever this overlay is visible." An input method on its own only
    /// declares that this overlay would accept mouse events if any were being
    /// generated; outside the dashboard, none are. Phase 5's first headset run
    /// failed on exactly that gap - the move handle never received a click
    /// because SteamVR was never pointing anything at it.
    /// </para>
    /// <para>
    /// <b>System-wide, and that word is the whole risk.</b> This does not make
    /// one overlay interactive - it puts SteamVR into laser mouse mode for as
    /// long as this overlay is visible, which for a permanently-present wrist
    /// panel would mean always, in every game. So it is toggled with the same
    /// gaze gate as the input method rather than set once at creation. See
    /// <c>ChatOverlay.SetInputEnabled</c>, which is the only caller and drives
    /// both together.
    /// </para>
    /// </summary>
    public void SetMakesOverlaysInteractive(bool interactive)
    {
        ThrowIfDisposed();
        _api.SetOverlayFlag(_handle, 1 << 16, interactive);
    }

    /// <summary>
    /// Requests <c>VROverlayFlags_SendVRDiscreteScrollEvents</c> (<c>1 &lt;&lt; 6</c>),
    /// the same header-derived and dashboard-proven flag used for SteamVR
    /// controller scrolling elsewhere in this app.
    /// </summary>
    public void SetSendsDiscreteScrollEvents(bool enabled)
    {
        ThrowIfDisposed();
        _api.SetOverlayFlag(_handle, 1 << 6, enabled);
    }

    /// <summary>
    /// Declares the coordinate space this overlay's mouse events arrive in.
    /// Set it to the texture's own pixel dimensions and an event's x/y can be
    /// hit-tested directly against the rectangles the renderer drew from - the
    /// property that stops a laser click landing on a different control than
    /// the one being pointed at.
    /// </summary>
    public void SetMouseScale(float width, float height)
    {
        ThrowIfDisposed();
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0f || height <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                $"{width}x{height}",
                "An overlay mouse scale must be positive in both axes.");
        }

        _api.SetOverlayMouseScale(_handle, width, height);
    }

    /// <summary>
    /// Drains this overlay's own event queue into <paramref name="into"/>,
    /// which is cleared first.
    /// <para>
    /// Per-overlay, not the system queue: draining here cannot swallow input
    /// destined for anything else, which is the same reason
    /// <c>OpenVrInput.IsQuitRequested</c> can drain the system queue without
    /// starving the dashboard.
    /// </para>
    /// </summary>
    public void PollMouseEvents(List<OverlayMouseEvent> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        ThrowIfDisposed();
        into.Clear();
        _api.PollOverlayMouseEvents(_handle, into);
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
