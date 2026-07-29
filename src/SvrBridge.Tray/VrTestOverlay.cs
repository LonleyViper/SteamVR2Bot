using System.Drawing;
using System.Drawing.Imaging;
using SvrBridge.Core;

namespace SvrBridge.Tray;

/// <summary>
/// A hard-coded panel pinned to the left controller, used to prove the overlay
/// substrate works against real hardware.
/// <para>
/// This is a development aid, not a feature. It exists because the vtable
/// indices, the raw texture upload and the tracked-device transform cannot be
/// verified without a headset, and a self-test that only proves create/find/
/// destroy says nothing about whether anything is actually drawn in the right
/// place. It defaults to off and nothing in the product depends on it.
/// </para>
/// <para>
/// Deliberately GDI+ and deliberately throwaway. The renderer for the real chat
/// window is a Phase 3 decision, and picking one here - by making this look like
/// the beginning of a rendering layer - would prejudge it.
/// </para>
/// </summary>
internal sealed class VrTestOverlay : IDisposable
{
    private const string OverlayKey = "ie.lonelyviper.svrbridge.testoverlay";
    private const int TextureWidth = 512;
    private const int TextureHeight = 256;

    private readonly VrOverlaySurface _surface;
    private readonly Action<string> _log;

    // The index the transform is currently bound to. Kept only to notice when
    // it changes; it is never used as the source of truth. See Tick.
    private uint? _boundDeviceIndex;
    private bool _warnedAboutMissingController;
    private bool _disposed;

    private VrTestOverlay(VrOverlaySurface surface, Action<string> log)
    {
        _surface = surface;
        _log = log;
    }

    /// <summary>
    /// Creates the overlay and uploads its one static texture. Returns null when
    /// this SteamVR version has no overlay interface, which is not worth
    /// failing the worker over.
    /// </summary>
    public static VrTestOverlay? TryCreate(OpenVrInput openVr, Action<string> log)
    {
        if (!openVr.SupportsOverlaySurfaces)
        {
            log("The VR test overlay was skipped: this SteamVR version has no overlay interface.");
            return null;
        }

        // SteamVR destroys an overlay when the process owning it exits, so a
        // surviving key means a live owner rather than debris - which makes the
        // CreateOverlay below fail with KeyInUse. Saying so up front turns a
        // bare error code into something the user can act on.
        if (openVr.TryFindOverlayHandle(OverlayKey, out _))
        {
            log(
                "A VR test overlay with this key already exists. "
                + "Another SteamVR2Bot instance is probably still running.");
        }

        var surface = openVr.CreateOverlaySurface(OverlayKey, "SteamVR2Bot test overlay");
        try
        {
            var overlay = new VrTestOverlay(surface, log);
            overlay.Paint();
            surface.SetWidthInMeters(0.25f);
            surface.SetAlpha(0.9f);
            surface.SetSortOrder(0);
            surface.SetCurvature(0.1f);
            surface.Show();
            log("The VR test overlay is on. Look at your left controller.");
            return overlay;
        }
        catch
        {
            // Never leave a handle behind when construction fails partway: it
            // would hold the key and block the next attempt.
            surface.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Re-resolves the left controller and re-applies the transform when the
    /// index has moved.
    /// <para>
    /// The index is asked for every tick rather than cached at creation. Tracked
    /// device indices are not stable across controller sleep, reconnect or a
    /// battery change, and a role can come back unassigned. An overlay bound to
    /// an index that has gone stale detaches silently - SteamVR reports no
    /// error, the panel simply stops following the hand - so noticing the change
    /// is the only way to survive it.
    /// </para>
    /// </summary>
    public void Tick(OpenVrInput openVr)
    {
        if (_disposed)
        {
            return;
        }

        var deviceIndex = openVr.TryGetControllerDeviceIndex(ControllerHand.Left);
        if (deviceIndex is null)
        {
            // No left controller at all: asleep, off, or role unassigned. Not an
            // error - it is the normal state between sessions - so the overlay
            // keeps its last transform and waits.
            if (!_warnedAboutMissingController)
            {
                _warnedAboutMissingController = true;
                _log("The VR test overlay is waiting for a left controller.");
            }

            _boundDeviceIndex = null;
            return;
        }

        _warnedAboutMissingController = false;
        if (_boundDeviceIndex == deviceIndex)
        {
            return;
        }

        // Sits above and slightly in front of the controller origin, tipped back
        // like a watch face so it faces the wearer when the hand is raised.
        var transform =
            VrOverlayTransform.Translation(0f, 0.06f, -0.12f)
            * VrOverlayTransform.RotationX(-0.6f);
        _surface.AttachToDevice(deviceIndex.Value, transform);
        _boundDeviceIndex = deviceIndex;
        _log($"The VR test overlay is following left controller device {deviceIndex.Value}.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _surface.Dispose();
    }

    /// <summary>
    /// Fills one texture and hands the pixels straight to SteamVR - no PNG, no
    /// disk, no decode. This is the path the chat window needs and the reason
    /// <c>SetOverlayRaw</c> was added.
    /// </summary>
    private void Paint()
    {
        using var bitmap = new Bitmap(TextureWidth, TextureHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(255, 24, 32, 48));
            using var border = new Pen(Color.FromArgb(255, 96, 200, 255), 4f);
            graphics.DrawRectangle(border, 2, 2, TextureWidth - 5, TextureHeight - 5);

            using var title = new Font("Segoe UI", 28f, FontStyle.Bold);
            using var caption = new Font("Segoe UI", 14f);
            using var text = new SolidBrush(Color.White);
            using var dim = new SolidBrush(Color.FromArgb(255, 150, 170, 200));
            using var centred = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            graphics.DrawString(
                "SteamVR2Bot",
                title,
                text,
                new RectangleF(0, 60, TextureWidth, 60),
                centred);
            graphics.DrawString(
                "overlay test - left controller",
                caption,
                dim,
                new RectangleF(0, 130, TextureWidth, 40),
                centred);
        }

        _surface.SetTexture(
            ToRgba(bitmap),
            TextureWidth,
            TextureHeight);
    }

    /// <summary>
    /// GDI+ <c>Format32bppArgb</c> is BGRA in memory on little-endian Windows;
    /// <c>SetOverlayRaw</c> wants RGBA. Without the swap the overlay renders
    /// with red and blue exchanged, which looks like a plausible colour choice
    /// rather than a bug - so it is worth stating why this calls
    /// <see cref="OverlayPixelFormat.SwapRedAndBlue"/> below.
    /// </summary>
    private static byte[] ToRgba(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            for (var y = 0; y < bitmap.Height; y++)
            {
                // Copy row by row: Stride can exceed width * 4 for alignment,
                // so the source is not one contiguous run of pixel data.
                var source = data.Scan0 + (y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(
                    source,
                    pixels,
                    y * bitmap.Width * 4,
                    bitmap.Width * 4);
            }

            OverlayPixelFormat.SwapRedAndBlue(pixels);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
