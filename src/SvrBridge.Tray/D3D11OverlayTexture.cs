using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
// Vortice.DXGI declares a MapFlags of its own, for surface mapping rather
// than resource mapping. Both are in scope here and the two are not
// interchangeable, so the D3D11 one is named explicitly.
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace SvrBridge.Tray;

/// <summary>
/// One persistent Direct3D 11 render target handed to SteamVR through
/// <see cref="SvrBridge.Core.VrOverlaySurface.SetD3D11Texture"/>, which is
/// what removes the blink both CPU upload paths produce - see the spike entry
/// in <c>LIVE_TEST_RESULTS.md</c>.
/// <para>
/// <b>Created once per overlay and never reallocated per frame - that is the
/// whole mechanism.</b> <c>SetOverlayRaw</c> takes width, height and
/// bytes-per-pixel on every call, so SteamVR has to treat each repaint as a
/// new texture; this is written in place instead. Any change here that
/// reallocates per frame silently turns the fix back into the thing it fixed.
/// </para>
/// <para>
/// <b>Two textures, not one.</b> The texture SteamVR holds has to be a
/// default-usage shared resource, which D3D11 will not let the CPU map; a
/// mappable texture has to be <c>Staging</c>, which cannot be shared. So
/// writes land in a staging texture and are copied across on the GPU. Both are
/// allocated once, so a repaint costs one device-local copy and no allocation.
/// </para>
/// <para>
/// The device is <b>not</b> owned here - see <see cref="D3D11OverlayDevice"/>,
/// which is shared across every overlay and hands these out.
/// </para>
/// </summary>
internal sealed class D3D11OverlayTexture : IOverlayTexture
{
    /// <summary>
    /// Long enough that a busy GPU finishing a 1400x900 copy is never mistaken
    /// for a dead one, short enough that a genuinely hung device does not take
    /// the overlay thread with it. A copy this size completes in well under a
    /// millisecond in practice.
    /// </summary>
    private static readonly TimeSpan CopyCompletionTimeout = TimeSpan.FromMilliseconds(500);

    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _shared;
    private readonly ID3D11Texture2D _staging;
    private readonly ID3D11Query _copyComplete;
    private bool _disposed;

    /// <summary>
    /// Throws rather than returning null on failure: the caller
    /// (<see cref="D3D11OverlayDevice.TryCreateTexture"/>) treats a throw here
    /// as evidence the device is gone and starts its recovery backoff, which
    /// is exactly the right response to a texture that cannot be allocated.
    /// </summary>
    public D3D11OverlayTexture(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        _context = context;
        Width = width;
        Height = height;

        // R8G8B8A8, not B8G8R8A8: every renderer in this app already produces
        // straight-alpha RGBA for SetOverlayRaw (see OverlayPixelFormat's two
        // named source conversions), and matching that byte order here means
        // the delivery changed and the pixels did not. Getting this wrong is
        // the bug class that reads as a deliberate palette rather than a
        // defect.
        var sharedDescription = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            // SteamVR's compositor is a separate process with its own device;
            // without this it cannot open the texture at all.
            MiscFlags = ResourceOptionFlags.Shared
        };

        var stagingDescription = sharedDescription with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.None
        };

        _shared = device.CreateTexture2D(sharedDescription);
        try
        {
            _staging = device.CreateTexture2D(stagingDescription);
            try
            {
                _copyComplete = device.CreateQuery(QueryType.Event, QueryFlags.None);
            }
            catch
            {
                _staging.Dispose();
                throw;
            }
        }
        catch
        {
            _shared.Dispose();
            throw;
        }
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The raw <c>ID3D11Texture2D*</c> for OpenVR's <c>Texture_t</c>. Stable
    /// for the lifetime of this object.
    /// </summary>
    public nint NativeTexture => _shared.NativePointer;

    /// <summary>
    /// Replaces the contents with straight-alpha RGBA laid out tightly at
    /// <c>Width * 4</c> per row.
    /// <para>
    /// The copy is row by row rather than one block copy because a mapped row
    /// pitch is whatever the driver chose and is usually wider than
    /// <c>Width * 4</c>. Ignoring it does not fail, it shears the image - each
    /// row lands progressively further left than the one above, which reads as
    /// a diagonal smear.
    /// </para>
    /// </summary>
    public void Write(byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var rowBytes = Width * 4;
        var required = (long)rowBytes * Height;
        if (rgba.Length < required)
        {
            throw new ArgumentException(
                $"The pixel buffer holds {rgba.Length} bytes but {Width}x{Height} RGBA needs {required}.",
                nameof(rgba));
        }

        var mapped = _context.Map(_staging, 0, MapMode.Write, MapFlags.None);
        try
        {
            CopyRows(rgba, mapped.DataPointer, (int)mapped.RowPitch, rowBytes, Height);
        }
        finally
        {
            _context.Unmap(_staging, 0);
        }

        _context.CopyResource(_shared, _staging);
        WaitForCopyToComplete();
    }

    /// <summary>
    /// Blocks until the GPU has actually finished copying into the shared
    /// texture.
    /// <para>
    /// <b>Submitting the copy is not enough, and getting this wrong looks like
    /// success.</b> SteamVR's compositor reads this texture from its own
    /// device, and a plain <c>Flush</c> only queues the copy - it does not wait
    /// for it. Hand the pointer over before the copy lands and the compositor
    /// reads whatever was there before, so every surface displays the
    /// <em>previous</em> frame.
    /// </para>
    /// <para>
    /// That failure mode is invisible on a surface that repaints continuously -
    /// chat at 10 Hz is simply 100 ms stale, which nobody can see - and
    /// obvious on one that repaints only when something changes: the dashboard
    /// showed the page you were on before, so every click had to be made twice.
    /// It was found in the headset exactly that way (see
    /// <c>LIVE_TEST_RESULTS.md</c>), not by anything upstream failing.
    /// </para>
    /// <para>
    /// An Event query is the D3D11 way to ask "has everything submitted before
    /// this point finished?". Spinning on it costs well under a millisecond for
    /// these texture sizes.
    /// </para>
    /// </summary>
    private void WaitForCopyToComplete()
    {
        _context.End(_copyComplete);
        _context.Flush();

        var deadline = Stopwatch.GetTimestamp()
                       + (long)(Stopwatch.Frequency * CopyCompletionTimeout.TotalSeconds);
        while (!_context.GetData(_copyComplete, AsyncGetDataFlags.None, out int _))
        {
            if (Stopwatch.GetTimestamp() > deadline)
            {
                // Treated as device loss rather than shrugged off: a copy that
                // never completes means this texture's contents are unknowable,
                // and showing an unknown frame forever is worse than dropping
                // to the blinking-but-correct SetOverlayRaw path.
                throw new TimeoutException(
                    "The GPU did not finish copying the overlay texture within "
                    + $"{CopyCompletionTimeout.TotalMilliseconds:0} ms.");
            }

            Thread.SpinWait(64);
        }
    }

    /// <summary>
    /// The row-pitch-respecting copy, separated out so a self-test can drive
    /// it against a deliberately over-wide pitch without a GPU. A single block
    /// copy here is the mistake this exists to make impossible.
    /// </summary>
    internal static void CopyRows(
        byte[] source,
        nint destination,
        int destinationRowPitch,
        int rowBytes,
        int rowCount)
    {
        for (var row = 0; row < rowCount; row++)
        {
            Marshal.Copy(
                source,
                row * rowBytes,
                destination + (row * destinationRowPitch),
                rowBytes);
        }
    }

    /// <summary>
    /// Reads the texture back through a <b>second, independent</b> Direct3D
    /// device, opening it by shared handle exactly as SteamVR's compositor
    /// does.
    /// <para>
    /// It proves the shared handle opens and the format survives cross-device,
    /// which <see cref="ReadBackForTesting"/> cannot: that one uses the same
    /// device and context that wrote the texture, so D3D11 serialises it for
    /// free.
    /// </para>
    /// <para>
    /// <b>It does not reproduce the timing race
    /// <see cref="WaitForCopyToComplete"/> exists to prevent, and was measured
    /// not to.</b> With the wait removed this still passes every time, because
    /// standing up a second device takes milliseconds and the copy has long
    /// since landed by the time the read happens. Only a real consumer reading
    /// immediately - SteamVR's compositor - hits the window. That bug is caught
    /// in the headset matrix, not here; do not read a pass on this test as
    /// evidence the wait is unnecessary.
    /// </para>
    /// </summary>
    /// <returns>Null when a second device cannot be created, which is not a failure.</returns>
    internal byte[]? ReadBackThroughASecondDeviceForTesting()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var sharedResource = _shared.QueryInterface<IDXGIResource>();
        var sharedHandle = sharedResource.SharedHandle;

        var result = D3D11.D3D11CreateDevice(
            nint.Zero,
            Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.None,
            [Vortice.Direct3D.FeatureLevel.Level_11_0],
            out var reader,
            out var readerContext);
        if (result.Failure || reader is null || readerContext is null)
        {
            reader?.Dispose();
            readerContext?.Dispose();
            return null;
        }

        using (reader)
        using (readerContext)
        {
            using var opened = reader.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
            var description = new Texture2DDescription
            {
                Width = (uint)Width,
                Height = (uint)Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            using var readback = reader.CreateTexture2D(description);
            readerContext.CopyResource(readback, opened);
            readerContext.Flush();
            return MapAndCopyOut(readerContext, readback);
        }
    }

    /// <summary>
    /// Reads the texture back into straight-alpha RGBA on the writing device,
    /// so a self-test can prove the channel order and the row-pitch copy
    /// survive a real GPU round trip. Allocates a staging texture per call -
    /// only ever used by tests. See
    /// <see cref="ReadBackThroughASecondDeviceForTesting"/> for what this
    /// deliberately cannot see.
    /// </summary>
    internal byte[] ReadBackForTesting(ID3D11Device device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var description = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        using var readback = device.CreateTexture2D(description);
        _context.CopyResource(readback, _shared);
        return MapAndCopyOut(_context, readback);
    }

    private byte[] MapAndCopyOut(ID3D11DeviceContext context, ID3D11Texture2D readback)
    {
        var rowBytes = Width * 4;
        var pixels = new byte[rowBytes * Height];
        var mapped = context.Map(readback, 0, MapMode.Read, MapFlags.None);
        try
        {
            for (var row = 0; row < Height; row++)
            {
                Marshal.Copy(
                    mapped.DataPointer + (row * (int)mapped.RowPitch),
                    pixels,
                    row * rowBytes,
                    rowBytes);
            }
        }
        finally
        {
            context.Unmap(readback, 0);
        }

        return pixels;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _copyComplete.Dispose();
        _staging.Dispose();
        _shared.Dispose();
    }
}
