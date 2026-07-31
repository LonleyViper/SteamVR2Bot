# Handoff — 2026-07-30, overlay laser input and grab-to-place (Phase 5)

Supersedes the overlay-texture-conversion hand-off from earlier the same day,
which is now committed history — see `LIVE_TEST_RESULTS.md` for that record.
This file is the current-state snapshot; read it before touching anything that
draws in VR or accepts input there.

## State: shipped and live-confirmed

The chat window can be grabbed by a move handle in its top-right corner and
placed anywhere relative to its anchor, in full six degrees of freedom. The
placement persists per anchor mode and survives a restart. The VR **Settings**
tab gained a placement reset and a **Grow on gaze** toggle; the window now hides
itself when turned away or left too far off.

Fifteen of seventeen headset rows pass. Everything in this hand-off is committed
on `phase5-overlay-grab-to-place`. Build is clean with zero warnings,
`dotnet format --verify-no-changes` passes on both projects, and both self-test
suites pass including on the packaged single-file build.

## The three things to read before touching overlays

### 1. `SetOverlayInputMethod` alone delivers nothing

It declares that an overlay *would* accept mouse events. It does not cause any
to be produced. Outside the dashboard, SteamVR is not pointing anything at
overlays at all, so the move handle received no clicks whatsoever on the first
headset run.

The other half is `VROverlayFlags_MakeOverlaysInteractiveIfVisible` — `1 << 16`,
set through `SetOverlayFlag`, vtable anchor 11. From `openvr.h`:

> If this is set and the overlay's input method is not none, the system-wide
> laser mouse mode will be activated whenever this overlay is visible.

**"System-wide" is the load-bearing word.** This does not make one overlay
interactive; it puts SteamVR into laser mouse mode for as long as that overlay
is *visible*. A permanently-present panel must therefore toggle the flag, not
set it at creation. `ChatOverlay.SetInputEnabled` is the single writer of both
halves and drives them under the gaze gate — flag off first, on last, so the
laser is never live while the overlay has stopped accepting what it delivers.

Live-confirmed with the flag active: normal play in a real VR game is
unaffected. That also retires the Phase 4 laser-input probe's caveat, which had
measured "the game keeps its trigger" with the mechanism switched off.

### 2. A zero overlay transform is silently fatal

SteamVR accepts an all-zero 3x4 transform without an error and collapses the
overlay quad to nothing. No exception, no log line, no misplaced panel — the
window simply does not exist. The log reads perfectly: "The chat window is on",
"following the left controller (device 6)".

It arrived because `ChatPlacement` changed persisted shape (three floats per
anchor mode → a full transform) and the older JSON had no property the new shape
recognised, so `System.Text.Json` left the struct at all zeros. Not absent, not
an error, and finite — so every check that existed passed it through, and the
tray then saved the zeros back.

`VrOverlayTransform.IsUsable` now requires the rotation block to *be* a rotation
(orthonormal, tolerance 0.01), and `OverlayPlacement.Sanitised()` is applied on
settings load and argument parse so a bad value is never carried forward.

**The general rule:** when a persisted field changes shape without a version
marker, the type must recognise its own invalid values. A deserialiser cannot
tell "written by an older shape" from "legitimately zero".

### 3. Do not bend `MotionSample` to serve overlay UI

Its body frame is yaw-only *by design*, so glancing down mid-gesture cannot turn
a level sweep into a diagonal one. That is right for gesture recognition and
useless for anything needing device rotation or a true eye line — the first
grab-to-place built on it could only translate, which the headset rejected
immediately.

`OpenVrInput.TryGetDevicePose` is the seam: additive, reads the same `_poses`
array `SampleMotion` builds from, and converts nothing (`HmdMatrix34` and
`VrOverlayTransform` are the same layout by construction). `MotionSampling.cs`
was not modified in this phase.

## How the pieces fit

| Concern | Where |
|---|---|
| Saved offsets, one full transform per anchor mode | `OverlayPlacement` (Core) |
| The rigid grab's arithmetic | `OverlayDrag` (Core), same file |
| Gaze / facing / distance from one pose pair | `PanelView` (Core) |
| Hide when turned away or far, with hysteresis | `PanelVisibilityGate` (Core) |
| Hover, gaze gate, grab/release signalling | `ChatOverlayInput` (Tray) |
| The one rectangle table, read by renderer and hit test | `ChatOverlayLayout` (Tray) |
| Wiring, poses, visibility, persistence | `ChatOverlay` (Tray) |

The grab records one constant at mouse-down and restores it every tick:

```
PanelInPointer = inverse(pointerPose) * (anchorPose * offset)          // once, on grab
offset         = inverse(anchorPose) * (pointerPose * PanelInPointer)  // every tick
```

Overlay mouse events are only the grab and release signal — they carry 2D panel
coordinates, not a world ray, so they cannot drive the movement.

Other confirmed OpenVR facts from this phase:

- SteamVR draws the laser pointer dot itself on a regular overlay. No
  `SetOverlayCursor`, and no need to derive its vtable index.
- `SetOverlayMouseScale` works on a regular overlay. Set it to the panel's pixel
  size so events hit-test directly against the drawn rectangles.
- Overlay mouse coordinates have their origin at the **bottom-left**. Flip Y,
  the same as `TryGetDashboardInteraction` already does.
- Button-event coordinates are unreliable — use the last hovered index.
- An overlay's texture faces its own **+Z**, which is why a tracked-device-
  relative panel placed in front of its device already faces back at it.

## Two behaviour decisions worth not reverting

**Grow on gaze defaults to off.** This deliberately breaks the rule every other
setting in `UserSettings` follows — default to whatever the app did before the
setting existed, so an upgrade changes nothing. A full phase of headset use said
the animation was distracting to read against, and shipping a default that has
to be turned off first is the wrong way round. An install that saved a
preference keeps it; only a file predating the toggle takes the new default. The
self-test says in a comment why it is the odd one out, so nobody aligns it back
with its neighbours.

**A drag overrules both gates.** It holds the gaze gate open and forces
visibility for its whole duration. Without that, dragging the window away from
the wrist walks the anchor out of the gaze cone, input turns off, and the
release that would end the drag can never arrive — the panel sticks to the hand
with no way to let go. The drag/hold invariant is now *reconciled* every tick
rather than enumerated, because enumerating the paths that can withdraw input is
how that bug happened.

## Open

- **Row 18 is not run.** With grow-on-gaze off, point at the handle while
  looking away, then while looking at it — only the second should grab. The
  animation is off; the input gate is not. This is now the *default* path, so it
  is worth running.
- **Rows 4b, 4c, 5b, 6b, 6c and 11–14 were superseded** by later fixes rather
  than individually re-run. The behaviour they cover is exercised by rows 15–22,
  which pass, but they are recorded honestly as not run.
- **Notifications deliberately have no laser input.** Phase 5's hard constraint;
  they are transient and must never capture the pointer. `PanelVisibilityGate`
  is likewise chat-only.
- **World-lock is still deferred.** It needs `SetOverlayTransformAbsolute`,
  which the tracked-device-relative path cannot express.
- The dashboard blink remains a characterised platform limitation with three
  untried leads — unchanged by this phase.
- Buttons that trigger Streamer.bot actions are the next increment on this
  plumbing. Adding one means adding a rectangle to `ChatOverlayLayout.Buttons`
  and an arm to the `ButtonDown` switch — not restructuring anything.

## Untracked, deliberately

`PHASE0_PROMPT.md`–`PHASE5_PROMPT.md`, `PHASE5_WRIST_PLACEMENT_PROMPT.md`,
`SPIKE_D3D11_TEXTURE_PROMPT.md`, `FIX_DASHBOARD_BLINK_PROMPT.md`, and
`VR UI Screenshots/` stay out of the repo, matching every previous phase.

## Environment notes

- `dotnet` is not on `PATH`. The SDK is at
  `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`;
  `C:\Program Files\dotnet\dotnet.exe` is a runtime-only muxer with no SDK and
  will fail with "No .NET SDKs were found".
- There is no solution file. Build the two projects by path:
  `src\SvrBridge.Tray\SvrBridge.Tray.csproj` and `src\SvrBridge\SvrBridge.csproj`.
- Published build: `artifacts\publish\SteamVR2Bot.exe` — the one SteamVR
  auto-launches and the only one live tests should use. Always publish before a
  headset test; never `dotnet run` from source.
- **The publish fails while SteamVR2Bot is running** (locked exe). Close it
  first — `Get-Process -Name "SteamVR2Bot*" | Stop-Process -Force` — which kills
  both the tray app and its OpenVR worker. The user has asked that this be done
  without stopping to ask.
- Logs: `%LOCALAPPDATA%\SteamVR2Bot\Logs\svr-bridge-YYYYMMDD.jsonl`. The chat
  window now logs the first laser event of each interaction and every button
  event, with coordinates and what they hit — that line is the fastest way to
  tell "SteamVR sent nothing" from "moves arrived, button did not".
- Self-tests: `SteamVR2Bot.exe --self-test` and
  `diagnostics\SteamVR2Bot.Diagnostics.exe --self-test`.
- A real `openvr.h` (`IVROverlay_027`) is on this machine under
  `Documents\GitHub\OBS_Reshade_Plugin\...\deps\openvr\headers\` — the source
  used to derive the overlay flag, and the right place to check the next
  constant rather than guessing it.

## A note on method, earned twice this phase

**A clean log is not evidence a VR feature works.** Two of the three real
defects here produced no error of any kind: the zero transform, and the stranded
drag. Both were found by reading state directly — the settings file, the actual
transform — not by trusting that nothing threw.

**Instrument the discriminator before the headset trip, not after.** "The handle
does not respond" has two causes needing opposite fixes: nothing arriving at
all, versus moves arriving but no button events. Not being able to tell them
apart cost a whole session.
