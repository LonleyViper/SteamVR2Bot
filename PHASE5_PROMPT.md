# Claude Code prompt — Phase 5: convert every overlay to the D3D11 texture path

Model: **Opus.** The spike did the hard interop, but this adds graphics device
lifecycle, device-loss recovery, and the dashboard overlay — whose failure mode
is a wedged or blank overlay rather than a red test.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` §3 Risk 2 (revised) and the spike's entry
in `LIVE_TEST_RESULTS.md` before starting.

## Scope

Roll the proven `SetOverlayTexture` path across every overlay, and answer the
questions the spike deliberately left open.

Do **not** add buttons, `SetOverlayInputMethod`, or the reposition handle. That
is the next phase and it should land on a converted, stable substrate.

## Background

The spike proved that a persistent Direct3D 11 texture written in place and
delivered via `SetOverlayTexture` eliminates the blink that both CPU upload
paths produce. It proved it on **one regular overlay (chat)** only.

## B1. Open questions the spike left — answer these deliberately

1. **Does `SetOverlayTexture` need reissuing on every write?** The spike reissues
   it each time, matching desktop-mirror overlays. Writing in place without
   reissuing was never tested. Test it. If writing alone works, prefer it. If
   reissuing is needed, keep it and **record why in a comment** so nobody
   "optimises" it away later.
2. **One D3D11 device shared across all overlays, not one each.** A device is a
   heavyweight object with no isolation benefit here. Textures are per-overlay —
   they differ in size — but the device is shared.
3. **Device loss must be recoverable, not permanent.** The spike falls back to
   `SetOverlayRaw` forever once the device is lost. TDRs and driver updates
   happen mid-session and the app may run for hours afterwards, so a permanent
   fallback means the blink returns and never leaves until restart. Fall back
   immediately, then attempt to recreate the device on a backoff — the
   `StreamerBotEventStream` reconnect policy is the pattern to follow. Log the
   transition in both directions.
4. **Keep `SetOverlayRaw` as the fallback path.** Do not delete it. It is what
   runs when no device is available, and it is the control for any future
   comparison.

## B2. Convert the remaining overlays

Notifications and the test overlay are regular overlays like chat — mechanical.

**The dashboard is the risky one.** Three differences from what the spike proved:

- It is a **dashboard overlay** (`CreateDashboardOverlay`), not a regular one.
  `SetOverlayTexture` should behave identically on a dashboard overlay handle,
  but that is **unverified** — the spike only used a regular overlay. Verify it
  early rather than at the end; if a dashboard overlay behaves differently, that
  changes the shape of this work and should be reported, not worked around
  quietly.
- Its renderer is **GDI+**, producing `Format32bppArgb` — **straight alpha**,
  BGRA in memory. The chat path is WPF `Pbgra32` — **premultiplied**. The
  conversion into the D3D texture must handle both correctly. `OverlayPixelFormat`
  already documents this distinction; applying the WPF un-premultiply to GDI+
  output would wash the colours out.
- It is 1400×900 against chat's 512×768, so textures are per-overlay.

Converting the dashboard also removes `SetOverlayFromFile` from the update path,
along with `NextDashboardImagePath`, `_imageSequence` and the `_oldImagesCleaned`
cleanup machinery. Remove them once nothing uses them.

**Leave the dashboard thumbnail on `SetOverlayFromFile`.** It is a one-time load
of the static app icon, guarded by `_dashboardThumbnailInitialized`. That is a
genuinely file-based operation and converting it risks the SteamVR taskbar
thumbnail showing the current page instead of the icon — a bug someone already
fixed once, judging by the comment there.

## B3. Row pitch and byte order — the two that look like success

- **Row pitch.** A mapped D3D11 texture's row pitch is usually not `width * 4`;
  drivers pad rows for alignment. A single block memcpy produces a sheared
  image. Copy row by row.
- **Byte order.** Wrong channel order reads as a deliberate palette rather than
  a defect. There is now a third source format in play alongside the two
  existing ones, so keep a live colour check in the matrix.

## Hard constraints

1. **Vortice.Windows only.** No other new packages. Not SharpDX.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not add `SetOverlayInputMethod`** to chat or notifications.
4. **Do not change any layout, page flow, or visual design.** This is a
   texture-delivery change. Anything that looks different afterwards, other than
   the blink being gone, is a regression.
5. `VrDashboardLayout`'s rectangles and `IndexAt` hit testing stay untouched.

## Verification — required

1. **Run spike matrix rows 4–7**, which were never run: gaze animation with the
   texture path active; switching back to raw without wedging the overlay;
   dashboard and notifications unaffected. They are regression checks and this
   conversion must pass them.
2. **Self-tests:** the row-pitch copy handles a pitch wider than the row; the
   GDI+ and WPF source formats each convert to the expected bytes for a known
   solid colour, and un-premultiply is applied to the WPF path only; device-loss
   fallback selects the raw path and recovery returns to the texture path.
3. All existing tests pass. Build clean, no new warnings.
4. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, its convention
   being that an unrun row is recorded as "not run" and never assumed passing:
   - **The dashboard blink is gone** under rapid Tolerance-slider clicking — the
     original reproduction case.
   - The chat blink is gone on new messages; notifications still fade correctly.
   - **Colours correct on every surface** — dashboard, chat, notifications.
   - The full shortcut wizard still works end to end: create, edit, delete.
   - The taskbar thumbnail still shows the app icon, not the current page.
   - Controller sleep/wake still reattaches the chat window.
   - **An existing shortcut still fires exactly once.**

## Optional probe, only if the conversion is finished and stable

This answers a question that has shaped several phases and has never been
tested. Skip it if the conversion needs the attention — do not let it compromise
the work above.

Temporarily enable `SetOverlayInputMethod` on the chat overlay, get into a VR
game, and point a controller at the chat window. **Does the game still receive
the trigger, or does the overlay consume it?** Record the answer in
`LIVE_TEST_RESULTS.md` and **revert the change** — this is a probe, not a
feature. The next phase's design depends on the answer.

## When you are done

Summarise what changed, state your answers to each of B1's four questions,
confirm whether a dashboard overlay handle behaved identically to a regular one,
list the manual headset steps, and report the probe result if it was run.
