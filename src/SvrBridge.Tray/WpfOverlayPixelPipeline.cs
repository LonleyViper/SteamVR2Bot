using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SvrBridge.Core;
// System.Drawing.Size is also in scope project-wide via the WinForms
// implicit usings, so WPF's layout size type needs a name of its own here.
using WpfSize = System.Windows.Size;

namespace SvrBridge.Tray;

/// <summary>
/// Rasterises a WPF <see cref="FrameworkElement"/> into RGBA bytes ready for
/// <see cref="VrOverlaySurface.SetTexture(byte[], int, int, int)"/>.
/// <para>
/// Must run on the STA thread that owns <paramref name="element"/> - callers
/// are expected to invoke this from inside a <see cref="WpfRenderThread"/>
/// dispatch, not from the WinForms UI thread or a thread pool thread.
/// </para>
/// <para>
/// This is the one path a solid-colour self-test can exercise without a
/// headset: <c>RenderTargetBitmap</c> always produces premultiplied
/// <c>Pbgra32</c>, so the un-premultiply and channel-swap steps below run on
/// every call, whether the element is real notification content or a plain
/// filled rectangle built purely to pin down the resulting bytes.
/// </para>
/// </summary>
internal static class WpfOverlayPixelPipeline
{
    public static byte[] RenderToRgba(FrameworkElement element, int width, int height)
    {
        var size = new WpfSize(width, height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();

        var target = new RenderTargetBitmap(width, height, 96d, 96d, PixelFormats.Pbgra32);
        target.Render(element);

        var stride = width * 4;
        var pixels = new byte[stride * height];
        target.CopyPixels(pixels, stride, 0);

        // Named per source format rather than composed here: the un-premultiply
        // is the difference between this and the GDI+ path, and applying the
        // wrong one washes the colours out silently. See OverlayPixelFormat.
        OverlayPixelFormat.ConvertWpfPbgra32ToRgba(pixels);
        return pixels;
    }
}
