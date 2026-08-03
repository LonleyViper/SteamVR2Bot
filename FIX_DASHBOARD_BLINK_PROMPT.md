# Claude Code prompt — fix: VR dashboard blinks on update

Model: **Sonnet.** No new interop — `SetOverlayRaw` is already bound and proven.
The risk is regression in a validated UI, not subtlety in the change itself.

**Both the VR dashboard and the chat window blink on update, and they are two
separate bugs. Both parts below apply — do both.**

This was established by test, not assumption: with the chat window turned off
entirely — removing all of Part A's overlay traffic — the dashboard still blinked
when navigating between pages. So chat's animation is not the dashboard's cause,
and the dashboard's texture path is not chat's cause.

Do Part A first anyway. It is smaller, and it is a defect on its own terms.

Paste everything below the line.

---

# Part A — the gaze animation never stops (confirmed, fix this first)

`ChatOverlay.AnimateGaze` runs once per OpenVR poll — roughly every 10 ms — and
ends with:

```csharp
_currentWidth += (targetWidth - _currentWidth) * t;
_currentAlpha += (targetAlpha - _currentAlpha) * t;
_surface.SetWidthInMeters(_currentWidth);
_surface.SetAlpha(_currentAlpha);
```

Exponential easing **asymptotes — it never reaches its target.** So these two
overlay API calls fire roughly 200 times a second, permanently, even with the
window completely at rest and the values changing by amounts far below anything
visible. `SetOverlayWidthInMeters` makes SteamVR recompute the overlay's
geometry each time.

This originated in the Phase 3 prompt, which said the gaze animation was "free"
because no texture repaint is involved and could "run at frame rate." That was
true about texture cost and omitted the important half: it must stop when it
converges.

## Fix

- Treat the animation as **converged** once both values are within a small
  epsilon of their targets. Snap exactly to the target at that point.
- **While converged, do not call `SetWidthInMeters` or `SetAlpha` at all.** Not
  "call with the same value" — skip the calls entirely. Steady state should be
  zero overlay API calls per tick.
- Only leave the converged state when the gaze target actually changes.
- **Check `NotificationOverlay` and `VrTestOverlay` for the same pattern** —
  `ChatOverlay.Tick`'s own doc comment says they share this design. Notifications
  animate only while showing so they are less exposed, but apply the same
  convergence rule wherever the pattern appears.

## Confirming Part A independently

Part A's fix is verified by the chat window specifically — the dashboard will
still blink after it, because that is Part B's doing. Judge Part A by: the chat
window's gaze grow/shrink still looks smooth and unchanged, and the converged
state issues zero overlay calls per tick.

---

# Part B — dashboard texture reallocation (also required)

## The bug

The VR dashboard blinks whenever its content updates — most visibly when
interacting with the Phase 4b settings tab, where live-apply means an update per
slider click and toggle.

## The cause

The dashboard is the only overlay still using `SetOverlayFromFile`. Every update
in `OpenVrInput.UpdateDashboard` does:

1. `VrDashboardRenderer.NextDashboardImagePath()` — `Interlocked.Increment` on
   `_imageSequence`, producing a **new unique path every time**.
2. GDI+ renders to a `Bitmap`, which is saved to disk as a PNG.
3. `SetOverlayFromFile(_dashboardHandle, path)` — SteamVR reads the file from
   disk, decodes the PNG, and allocates a **new texture**, because the path
   changed and it cannot update in place.

The blink is the gap between the old texture being released and the new one
being decoded and uploaded.

This was tolerable while the dashboard only re-rendered on discrete wizard page
changes, where the flicker was masked by the page changing anyway. Phase 4b's
live-apply settings raised the update rate to once per interaction, and the
flicker became a constant blink.

This is Risk 2 in `CHAT_AND_NOTIFICATIONS_PLAN.md` §3, which predicted this
failure mode for rapid updates.

## The fix

Route the dashboard through `SetOverlayRaw` — the same path chat and
notifications already use. It is vtable index 62, bound since Phase 1 and
hardware-proven. No disk, no encode, no decode, no reallocation.

The GDI+ `Bitmap` is already in memory in `VrDashboardRenderer`; it is currently
saved to a PNG and discarded. Instead:

- `LockBits` the bitmap to get its pixel buffer.
- Convert with the existing `OverlayPixelFormat` helpers. **GDI+'s
  `Format32bppArgb` is straight-alpha BGRA in memory**, so it needs
  `SwapRedAndBlue` only — *not* `UnpremultiplyBgra`, which exists for WPF's
  `Pbgra32`. Applying un-premultiply here would be a bug. The helper's own doc
  comment covers this distinction; read it.
- Feed the buffer to the existing `SetOverlayRaw` path.

### Consequences to follow through

- `NextDashboardImagePath`, `_imageSequence` and the `_oldImagesCleaned` cleanup
  machinery exist only to service the file-based path. Remove them once nothing
  uses them; do not leave dead file churn behind.
- Each `Render*` entry point in `VrDashboardRenderer` currently returns a
  `string` path. They need to return pixels plus dimensions instead. Keep the
  change mechanical and uniform across all of them — do not redesign the
  renderer.
- **The dashboard thumbnail must keep working.** It is set once, guarded by
  `_dashboardThumbnailInitialized`, and loads the static app icon
  `SteamVR2Bot.png` from disk. That is a genuinely file-based, one-time
  operation. Leaving it on `SetOverlayFromFile` is correct — do not convert it,
  and do not let the thumbnail start showing the current page.

## Hard constraints

1. **No NuGet packages.**
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not change any dashboard layout, page flow or visual design.** This is a
   texture-delivery change. If anything looks different afterwards other than
   the blink being gone, that is a regression.
4. `VrDashboardLayout`'s rectangles and `IndexAt` hit testing must be untouched —
   the renderer and click handler must still read the same table.
5. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests for Part A:** the gaze animation reaches a converged state and
   stops issuing overlay calls; a converged animation issues **zero**
   `SetWidthInMeters` / `SetAlpha` calls per tick (assert against a fake
   `IVrOverlayApi` that counts calls); changing the gaze target leaves the
   converged state and re-converges. The existing gaze hysteresis tests must
   still pass — convergence must not change *when* the window grows or shrinks,
   only when the calls stop.
2. **Self-tests for Part B, if done:** the existing dashboard layout and
   hit-testing tests still pass unchanged; a new test asserts the GDI+
   conversion path produces the expected bytes for a known solid colour, and
   specifically that un-premultiply is **not** applied to straight-alpha input.
2. All existing tests pass. Build clean, no new warnings.
3. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, following that
   file's convention that an unrun row is recorded as "not run". Cover:
   - The blink is gone on the settings tab during rapid slider and toggle use.
   - The blink is gone when moving between wizard pages.
   - **Colours are correct, not channel-swapped or washed out** — this is the
     one bug class that looks like a deliberate palette rather than a defect.
   - The full shortcut wizard still works end to end: create, edit, delete.
   - The dashboard thumbnail in the SteamVR taskbar still shows the app icon,
     not the current page.
   - Chat and notifications still render correctly.
   - **An existing shortcut still fires exactly once.**

## When you are done

Summarise what changed, confirm the self-tests pass, list the manual headset
steps, and state whether the wizard-page blink was present before the fix — that
answers whether this was a pre-existing issue Phase 4b exposed or something
Phase 4b introduced.
