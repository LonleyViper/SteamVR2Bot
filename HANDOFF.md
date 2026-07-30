# Handoff — 2026-07-30, overlay texture conversion

Supersedes the Phase 3 hand-off note (2026-07-29), which is now committed
history — see `LIVE_TEST_RESULTS.md` for that record. This file is the
current-state snapshot; read it before touching anything that draws in VR.

## State: live-confirmed fix, shipping off by default

The overlay blink is fixable on every surface except the SteamVR dashboard,
and the dashboard exception is understood, reproducible on demand, and
documented rather than open. The fix itself ships **off by default**: it is
this app's first native GPU dependency, live-confirmed working but not yet
exercised across the range of GPUs and drivers real users run, so it lands
behind a developer toggle rather than changing default behaviour outright.

| Surface | Upload path (default) | Upload path (developer opt-in) | Blink |
|---|---|---|---|
| Chat | `SetOverlayRaw` | `SetOverlayTexture` (D3D11) | **Fixable, off by default** |
| Notifications | `SetOverlayRaw` | `SetOverlayTexture` (D3D11) | **Fixable, off by default** |
| Test overlay | `SetOverlayRaw` | `SetOverlayTexture` (D3D11) | N/A, static |
| **SteamVR dashboard** | **`SetOverlayRaw`** | *(no working alternative)* | **Still blinks** |

Turn the fix on from Tray → **Chat test harness (developer)** → **Chat &
notification overlays via SetOverlayTexture (developer)**. Never persisted -
unchecked, and back to `SetOverlayRaw`, at every launch. See
`OverlayTextureUploader.TexturePathEnabled` and each overlay's own
`SetTexturePathEnabled`.

Everything in this hand-off is committed. Build is clean in Debug and Release
with zero warnings, both self-test suites pass including on the packaged
single-file build, and `dotnet format --verify-no-changes` passes.

## The one thing to read before touching overlays

**A `CreateDashboardOverlay` handle accepts `SetOverlayTexture`, returns
success, and never displays the result.** The panel freezes on whatever it last
showed. Regular overlays are all fine.

This cost two headset sessions to find because the symptom does not look like a
texture problem — it looks like the UI has stopped responding. Navigation keeps
working perfectly behind a picture that never changes, so clicks land on pages
the wearer cannot see, and it reads as "the wizard is broken".

**Two wrong diagnoses were reached and disproved before the right one.** Both
are written up in `LIVE_TEST_RESULTS.md` with the evidence that killed them, so
nobody re-derives them:

1. *A GPU synchronisation race* — that `Flush` submits the copy without waiting,
   letting SteamVR read the previous frame. Disproved: an Event query that waits
   for completion changed nothing. The wait was **kept** anyway (handing over a
   pointer before the copy lands is genuinely wrong) but it is not the fix.
2. *Upload frequency* — that a surface must keep calling `SetOverlayTexture` to
   stay live. Disproved: notifications repaint exactly as rarely as the
   dashboard and work fine, and forcing a 10 Hz reissue on the dashboard changed
   nothing. That machinery was removed rather than left in.

**Also correct one record if you read the older rows:** "dashboard blink gone"
was recorded as a PASS when the dashboard was simply not updating at all. A
frozen image cannot blink. That row measured nothing; it is marked invalid.

### How to reproduce it on demand

Tray → **Chat test harness (developer)** → **Dashboard via SetOverlayTexture**.
Tick it and the dashboard freezes; untick and it recovers. Never persisted,
unchecked at every launch. This exists so the finding can be re-checked after a
SteamVR update without a rebuild.

## What changed this session

1. **`SetOverlayTexture` bound** at vtable index 60 (`OpenVrInput`). The header
   was re-fetched and its SHA-256 matched the recorded revision byte for byte;
   all twenty-one existing indices reproduced exactly. Derivation is in the
   `TryGetOverlayTable` doc comment.
2. **`Vortice.Windows` 3.8.3** added to `SvrBridge.Tray` — the project's first
   NuGet dependency, an accepted change per the revised §3 Risk 2 of
   `CHAT_AND_NOTIFICATIONS_PLAN.md`. Not SharpDX. The single-file publish still
   works.
3. **New upload layer** (`OverlayTextureUpload.cs`, `D3D11OverlayDevice.cs`,
   `D3D11OverlayTexture.cs`): one shared D3D11 device for the whole worker,
   per-overlay persistent textures never reallocated per frame, and one routing
   seam every surface goes through.
4. **Recoverable device loss.** A failed write drops that surface to
   `SetOverlayRaw` immediately; the device is retried on the same backoff
   `StreamerBotEventStream` uses (1s, 2s, 5s, 10s, 30s forever). Confirmed live
   with a `Ctrl`+`Shift`+`Win`+`B` driver reset.
5. **The dashboard's PNG-to-disk path is gone**, and stays gone even though the
   dashboard is back on `SetOverlayRaw`. `VrDashboardRenderer`'s eleven page
   renderers return pixels instead of a file path; `NextDashboardImagePath`,
   `_imageSequence` and `_oldImagesCleaned` are removed, as is the dead
   `imagePath` that travelled tray → worker and was never read. A page change is
   now a memory copy rather than an encode-write-decode round trip.
6. **The dashboard thumbnail deliberately stays on `SetOverlayFromFile`** — a
   one-time load of the app icon. Its old "fall back to the current page image"
   branch was removed; that branch was the bug someone had already fixed once.
   Live-confirmed the taskbar still shows the icon.
7. **Two named pixel conversions** on `OverlayPixelFormat`:
   `ConvertGdiBgra32ToRgba` (swap only) and `ConvertWpfPbgra32ToRgba`
   (un-premultiply then swap). Applying the WPF one to GDI+ output washes the
   colours out — invisible at full opacity, visible on exactly the
   semi-transparent panel backgrounds both renderers use.
8. **The gaze-animation fix from the previous session** (`GazeScaleAnimation.cs`)
   was still uncommitted and is included here — the committed tree did not build
   without it.

## Open: the dashboard blink

**Yes, this is worth coming back to.** It is the last surface still affected and
the only thing between this app and "no blink anywhere".

It should be **its own narrow experiment, not bundled into other work.** Three
untried leads, cheapest first:

1. **`SetOverlayRenderingPid`.** `openvr.h` says `SetOverlayTexture` "can only be
   called by the overlay's creator or renderer process". The worker does create
   the overlay so this ought to hold already, but it is the only documented
   precondition on the call and it was never tested explicitly. Cheapest thing
   to rule out.
2. **A regular overlay standing in for the dashboard** rather than a
   `CreateDashboardOverlay` one. Regular overlays demonstrably work. Costs the
   SteamVR taskbar integration and the dashboard's own input handling — a large
   trade for a blink, and probably only worth it if the panel is being reworked
   anyway.
3. **A keyed-mutex shared texture** (`SHARED_KEYEDMUTEX` rather than `SHARED`).
   The correct cross-device sharing primitive; unknown whether SteamVR's overlay
   path acquires it. If it does not, this deadlocks or fails rather than
   degrading, so try it last.

Before starting: **budget a headset session for evidence, not for a fix.** The
two wrong diagnoses above both came from reasoning at the desk and both survived
a plausibility check. The activity log records every dashboard click coordinate
and page transition, which is what finally distinguished "clicks not landing"
from "page changing behind a stale picture" — use it early.

## Also still open, carried forward from Phase 3

- **Row 10** — more than 40 chat messages in one session (ring-buffer cap and
  eviction). The in-app tray harness (**Fill the ring buffer**) now covers this;
  it no longer depends on the Streamer.bot `!svrtest` action that never fired.
- **Row 17b** — an emote arriving in the first second or two of connecting, to
  prove the styled-text → real-image upgrade path rather than the steady state.
- **Notification burst behaviour** was never conclusively observed ("it only
  showed one"). `NotificationPlayer` shows one at a time by design, so this is
  probably correct rather than a bug, but it has not been isolated.
- The **`!svrtest` Streamer.bot action** was never root-caused. Largely
  superseded by the tray-menu harness, which needs no Streamer.bot action at
  all.

## Next phase

Buttons, `SetOverlayInputMethod` on chat, and the reposition handle were
deliberately excluded from this work so they would land on a converted, stable
substrate. That substrate now exists for the regular overlays.

One probe was scoped for this session and **not run**: temporarily enabling
`SetOverlayInputMethod` on the chat overlay, getting into a VR game, and
pointing a controller at the chat window to see whether the game still receives
the trigger or the overlay consumes it. It needs a headset and a running game.
The next phase's design depends on the answer, so it is worth doing first and
on its own — note the existing memory that a global-priority action set already
steals input from running games.

## Untracked, deliberately

`PHASE0_PROMPT.md`–`PHASE5_PROMPT.md`, `PHASE5_WRIST_PLACEMENT_PROMPT.md`,
`SPIKE_D3D11_TEXTURE_PROMPT.md`, `FIX_DASHBOARD_BLINK_PROMPT.md`, and
`VR UI Screenshots/` stay out of the repo, matching the pattern from every
previous phase. `CHAT_AND_NOTIFICATIONS_PLAN.md` **is** tracked and its §3
Risk 2 revision is committed here.

## Environment notes

- `dotnet` is not on `PATH`. The SDK is at
  `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`;
  `C:\Program Files\dotnet\dotnet.exe` is a runtime-only muxer with no SDK and
  will fail with "No .NET SDKs were found".
- Published build: `artifacts\publish\SteamVR2Bot.exe` — the one SteamVR
  auto-launches and the only one live tests should use. Always publish before a
  headset test; never `dotnet run` from source.
- **The publish fails while SteamVR2Bot is running** (locked exe). Close it
  first — `Get-Process -Name "SteamVR2Bot*" | Stop-Process -Force` — which kills
  both the tray app and its OpenVR worker. The user has asked that this be done
  without stopping to ask.
- Logs: `%LOCALAPPDATA%\SteamVR2Bot\Logs\svr-bridge-YYYYMMDD.jsonl`.
- Self-tests: `SteamVR2Bot.exe --self-test` (WinExe — run it via
  `Start-Process -Wait -PassThru` or the exit code comes back empty) and
  `diagnostics\SteamVR2Bot.Diagnostics.exe --self-test`.
