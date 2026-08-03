# Claude Code prompt — spike: does `SetOverlayTexture` kill the blink?

Model: **Opus.** New interop against an unbound vtable index, a native graphics
device, and pointer-level texture work. Same failure class as Phase 1 — a wrong
index is an access violation, not a failed assert.

**This is a spike, not a feature.** Its only job is to answer one question with
evidence. Do not convert the app. Do not refactor the renderers. If the answer is
no, the branch is thrown away and that is a success, not a wasted session.

Paste everything below the line.

---

## The question

**Does uploading via `SetOverlayTexture` with a persistent D3D11 texture
eliminate the overlay blink that `SetOverlayRaw` produces?**

Answer it on one overlay, with a live headset test, and report. Nothing else.

## Background

`LIVE_TEST_RESULTS.md` (search "blink") records a thorough investigation
concluding this is inherent SteamVR behaviour. That investigation was sound but
its conclusion is scoped too broadly: **both paths it tested — `SetOverlayRaw`
and `SetOverlayFromFile` — are the same mechanism**, a CPU-side upload where
SteamVR allocates and uploads. Finding they behave identically shows both CPU
upload paths blink, not that all texture replacement blinks.

The untested family is `SetOverlayTexture` with a GPU texture. Desktop-mirror and
video-player overlays update at video rates through it without blinking, so the
blink cannot be inherent to texture replacement as such.

The specific reason to expect a difference: `SetOverlayRaw(handle, buffer, width,
height, bytesPerPixel)` takes **dimensions on every call**, implying SteamVR
treats each call as a new texture. `SetOverlayTexture` hands over a texture
SteamVR holds a persistent reference to, written in place.

That is a hypothesis. This spike tests it.

## What to build — the minimum that answers the question

1. **Add Vortice.Windows** to `SvrBridge.Tray`. This is the project's first
   NuGet dependency and that is an accepted, deliberate change — the zero-package
   state was a convention, not a requirement. Do not use SharpDX; it is
   unmaintained.
2. **A D3D11 device** and one **shared** `ID3D11Texture2D` render target sized to
   the chat panel's fixed 512×768 (`WpfChatRenderer.PanelWidth` / `PanelHeight`).
   Created once, never reallocated — that is the entire point.
3. **Bind `SetOverlayTexture`.** It is not currently in `VrOverlayFunctions`.
   Derive its vtable index using **exactly** the method documented in
   `OpenVrInput.TryGetOverlayTable`'s doc comment: enumerate `IVROverlay`
   declaration order once from the `openvr.h` whose version is `IVROverlay_028`,
   then cross-check that the existing validated anchors land where predicted. If
   any anchor moves, stop and report. Do not guess a single index.
   `Texture_t` needs the native texture pointer, `ETextureType.DirectX`, and
   `EColorSpace.Auto`.
4. **Feed it the existing WPF output.** `WpfChatRenderer` already produces a
   pixel buffer. `Map` the D3D texture and copy that buffer in, respecting row
   pitch — the mapped row pitch will usually not equal `width * 4`, so copy
   row by row rather than one block memcpy. **Do not rewrite the renderer, the
   layout, the wrapping, the emotes or the badges.** Only the delivery changes.
   Note the pixel format needs to match what the existing pipeline produces —
   check `OverlayPixelFormat` and `WpfOverlayPixelPipeline` rather than assuming.
5. **A developer toggle to switch paths at runtime** if that is cheap — being
   able to flip between `SetOverlayRaw` and `SetOverlayTexture` in one session,
   on the same overlay, makes the comparison direct instead of relying on memory
   of yesterday's blink.

## What NOT to do

- Do not convert the dashboard, notifications, or the test overlay.
- Do not remove `SetOverlayRaw` or any existing path.
- Do not refactor `VrOverlaySurface` beyond what is needed to add one
  alternative upload path alongside the current one.
- Do not tidy, reorganise or improve anything else while in there.

## Verification

The self-tests here matter less than usual — this is a question, not a feature.
Still required:

1. The vtable index derivation cross-check passed, stated explicitly with which
   header version was used.
2. Existing tests still pass; nothing already working regressed.
3. Build clean.

**The actual deliverable is a live headset comparison**, recorded in
`LIVE_TEST_RESULTS.md` following its existing conventions:

- Rapid repeated chat updates through `SetOverlayRaw` — blink present? (expected
  yes, this is the control)
- The same through `SetOverlayTexture` — blink present?
- The Tolerance-slider stress test that reproduced the blink originally, if the
  spike path can be pointed at anything comparable.
- Colours correct, not channel-swapped — the D3D texture format may not match
  the CPU path's byte order, and this is the bug class that reads as a
  deliberate palette rather than a defect.

## Report back

State plainly whether the blink is gone, reduced, or unchanged. **If it is
unchanged, say so and stop** — do not iterate toward a fix, do not convert
anything else, and do not soften the result. A clean negative closes a real
question and is worth as much as a positive.

If it is gone, do **not** proceed to convert the rest of the app in this session.
Report, and let the conversion be scoped as its own piece of work with its own
regression matrix.
