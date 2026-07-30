namespace SvrBridge.Core;

/// <summary>
/// Pixel conversions shared by every overlay renderer feeding
/// <see cref="VrOverlaySurface.SetTexture(byte[], int, int, int)"/>.
/// <para>
/// Kept as pure byte-array math with no imaging library behind it, so it needs
/// no headset and no rendering toolkit to test - which matters, because a
/// mistake here is the bug class that looks plausible on screen and is
/// invisible in a log: wrong colours read as a deliberate palette, and a
/// premultiplied fade looks correct at full opacity and only wrong mid-fade.
/// </para>
/// </summary>
public static class OverlayPixelFormat
{
    /// <summary>
    /// Converts a WPF <c>PixelFormats.Pbgra32</c> buffer - premultiplied
    /// alpha, BGRA in memory - into the straight-alpha RGBA every overlay
    /// upload path expects, in place.
    /// <para>
    /// Named rather than left as two calls at the call site because the
    /// premultiply step is the difference between the two source formats and
    /// the mistake it guards against is silent: applying it to GDI+ output
    /// (see <see cref="ConvertGdiBgra32ToRgba"/>), which is already straight
    /// alpha, divides every channel by alpha a second time and washes the
    /// colours out - most visibly on the semi-transparent panel backgrounds
    /// both renderers use. Making each source format its own named entry
    /// point is what stops a future conversion picking the wrong one.
    /// </para>
    /// </summary>
    public static void ConvertWpfPbgra32ToRgba(byte[] pixels)
    {
        // Order matters: un-premultiply while the buffer is still in WPF's
        // native BGRA layout, then swap channels last. Swapping first would
        // un-premultiply the wrong two channels against alpha.
        UnpremultiplyBgra(pixels);
        SwapRedAndBlue(pixels);
    }

    /// <summary>
    /// Converts a GDI+ <c>PixelFormat.Format32bppArgb</c> buffer - straight
    /// alpha, BGRA in memory - into straight-alpha RGBA, in place.
    /// <para>
    /// A channel swap and nothing else. GDI+ is <em>not</em> premultiplied
    /// (that is <c>Format32bppPArgb</c>, which this codebase does not use), so
    /// there is deliberately no un-premultiply step here - see
    /// <see cref="ConvertWpfPbgra32ToRgba"/>.
    /// </para>
    /// </summary>
    public static void ConvertGdiBgra32ToRgba(byte[] pixels) => SwapRedAndBlue(pixels);

    /// <summary>
    /// Swaps the red and blue byte of every 4-byte pixel in place, converting
    /// between BGRA and RGBA. GDI+'s <c>Format32bppArgb</c> and WPF's
    /// <c>Pbgra32</c> both store BGRA in memory on little-endian Windows;
    /// <see cref="VrOverlaySurface.SetTexture(byte[], int, int, int)"/> wants RGBA.
    /// </summary>
    public static void SwapRedAndBlue(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
        }
    }

    /// <summary>
    /// Converts premultiplied-alpha BGRA (as produced by WPF's
    /// <c>PixelFormats.Pbgra32</c>) into straight-alpha BGRA in place.
    /// <para>
    /// OpenVR's raw overlay texture expects straight alpha. Left premultiplied,
    /// a fade driven by <c>SetOverlayAlpha</c> multiplies the same colour
    /// channels a second time, so the notification looks right at full opacity
    /// - the one moment premultiplied and straight alpha agree - and
    /// increasingly wrong as it fades, which is exactly the window the fade
    /// itself makes hard to notice.
    /// </para>
    /// </summary>
    public static void UnpremultiplyBgra(byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        for (var index = 0; index + 3 < pixels.Length; index += 4)
        {
            var alpha = pixels[index + 3];
            if (alpha == 0)
            {
                pixels[index] = 0;
                pixels[index + 1] = 0;
                pixels[index + 2] = 0;
                continue;
            }

            if (alpha == 255)
            {
                continue;
            }

            pixels[index] = Unpremultiply(pixels[index], alpha);
            pixels[index + 1] = Unpremultiply(pixels[index + 1], alpha);
            pixels[index + 2] = Unpremultiply(pixels[index + 2], alpha);
        }
    }

    private static byte Unpremultiply(byte channel, byte alpha) =>
        (byte)Math.Min(255, channel * 255 / alpha);
}
