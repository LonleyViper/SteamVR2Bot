# Hand-off — 2026-07-29

Branch `main`, ahead of `origin/main` by 6 commits, nothing stashed. The working
tree holds the in-progress Chat/Notifications work plus today's grip fix — all
real work, none of it to be discarded.

**Read this before touching input priority or reopening the grip bug.**

---

## 1. Grip regression — RESOLVED, do not re-investigate

The bug: while SteamVR2Bot ran, **Grip stopped working inside other VR games**
(confirmed in Contractors VR and Showdown). Closing the app restored it
instantly, every time. The user has no shortcut mapped to Grip.

### Root cause

`OpenVrInput` activated its action set at `k_nActionSetOverlayGlobalPriorityMin`
(`0x01000000`). openvr.h defines that as the threshold at which an action set
**takes input away from the scene application**.

Two facts make that fatal:

- Priority is **per-action-set, not per-action**. There is one set,
  `/actions/svrbridge`, holding everything.
- `bindings_vive_controller.json` claims grip, trigger, trackpad and menu on
  **both hands unconditionally** — independently of what the user has mapped.

So the app was not observing those controls, it was taking all eight from every
running game.

It only bites on this machine because SteamVR's experimental override is on:
`C:\Program Files (x86)\Steam\config\steamvr.vrsettings` carries
`"globalActionSetPriority" : true`, set during the 2026-07-27 input
investigation and never turned off. Any future priority work is meaningless
without accounting for that setting.

This was the loose end of the 2026-07-27 "Priority elevation ruled out" work in
`LIVE_TEST_RESULTS.md`. That change scoped the *elevation* to the recorder and
stated the intent correctly — "a running VR game is never outranked outside
recording" — but the **base** priority was left at the bottom of the global
band. The elevation was reverted; the base was not.

### Fix

`OpenVrInput.ActionSetPriority = 0` (ordinary band), with the full reasoning
recorded at the constant so it does not get re-raised casually.

Verified in-headset by the user, both directions:

| Check | Result |
|---|---|
| Grip inside a running VR game with the app active | **PASS** |
| Mapped shortcuts still fire from inside a running game | **PASS** |

Nothing was traded away. The elevated priority was never delivering an edge this
app needs — the 2026-07-27 runs had already shown that from the other side, when
SteamVR accepted `0x01FFFFFF` and deactivated the set under dashboard focus
anyway.

### The bisect was a dead end — do not restart it

A previous session spent a long stretch bisecting `9ae5204` vs `1b6daa3`. That
could never have converged: the steal dates to **2026-07-27** and is present in
every build since, so no commit boundary isolates it.

Neither suspect commit was ever involved:

- `9ae5204` (event stream) touches **no OpenVR code at all**. `ControllerSetup.cs`
  sounds input-related but the diff only adds a `Debug` enum value and a
  `ContainsUserContent` record field.
- `1b6daa3` (overlay substrate) binds 11 extra `IVROverlay` delegates but never
  calls them with the dev toggle off — `testOverlay` stays `null`. The self-test
  that does exercise those indices only runs under `--self-test`.

Four signals actively pointed the wrong way. Recognise them if something similar
recurs:

| Signal | Why it misled |
|---|---|
| The app's own log showed Grip edges arriving normally | It is this app receiving them that **is** the bug. |
| SteamVR's Controller Binding UI showed correct bindings | Config was fine; **live routing** broke, which that UI does not show. |
| Every per-commit build reproduced it | Pre-dates all of them. |
| `artifacts\publish` was believed pre-regression and "worked" | Its exe is stamped 2026-07-29 **12:43** — after every feature commit that day — and ships the identical grip-claiming `actions.json`. Treat that "known-good" result as unexplained; it did not survive contact with the timestamps. |

**Rule of thumb going forward:** if a control stops working inside other VR games
while this app runs, check action-set priority and what the binding file claims
*before* bisecting anything.

---

## 2. Repo state

Verified clean: both suites pass, three projects build with **0 warnings,
0 errors**, no stray processes.

```
dotnet build src\SvrBridge.Tray\SvrBridge.Tray.csproj     -> 0W 0E
dotnet build src\SvrBridge\SvrBridge.csproj               -> 0W 0E
SteamVR2Bot.exe --self-test                               -> exit 0
SteamVR2Bot.Diagnostics.exe --self-test                   -> exit 0 (SELF-TEST PASS)
```

Uncommitted, all intentional:

- **Grip fix** — `src/SvrBridge.Core/OpenVrInput.cs`, plus the root-cause section
  appended to `LIVE_TEST_RESULTS.md`.
- **Chat/Notifications WIP** — `BridgeEngine.cs`, `OpenVrSession.cs`,
  `MainForm.cs`, `OpenVrWorker.cs`, `SvrBridge.Tray.csproj`,
  `TrayApplicationContext.cs`, `TraySelfTests.cs`, `UserSettings.cs`,
  `VrTestOverlay.cs`; new `NotificationPlayer.cs`, `OverlayPixelFormat.cs`,
  `IVrPanelRenderer.cs`, `NotificationOverlay.cs`, `WpfNotificationRenderer.cs`,
  `WpfOverlayPixelPipeline.cs`, `WpfRenderThread.cs`.
- Planning docs: `CHAT_AND_NOTIFICATIONS_PLAN.md`, `PHASE0`–`PHASE3_PROMPT.md`.

The grip fix is logically independent of the notification work and can be
committed on its own. **Nothing has been committed yet** — the user has not asked
for it.

### Environment

- `dotnet` is **not** on `PATH` — use `$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe`.
- Debug build: `src\SvrBridge.Tray\bin\Debug\net10.0-windows\SteamVR2Bot.exe`.
- Logs: `%LOCALAPPDATA%\SteamVR2Bot\Logs\svr-bridge-YYYYMMDD.jsonl`.
- Settings: `%LOCALAPPDATA%\SteamVR2Bot\settings.json` — currently has
  `EventStreamEnabled: true` and `NotificationsEnabled: true`. Do not hand-edit
  without telling the user.
- Before any comparative test, confirm zero instances are running. A previous
  session's results were contaminated by leftover processes:

```powershell
Get-CimInstance Win32_Process -Filter "Name LIKE 'SteamVR2Bot%'" |
  Select-Object ProcessId, ExecutablePath
```

---

## 3. Open work

**Known-good next steps, in rough priority order:**

1. **Binding file claims all eight controls regardless of what is mapped.**
   Harmless at priority 0, but trigger/trackpad/menu are claimed for no reason.
   Generating `bindings_vive_controller.json` from the actual shortcut list is
   the principled fix, and it is a **prerequisite** before anyone raises the
   action-set priority again for any reason.
2. **`SaveAndApplySettingsAsync` → `RestartRuntimeAsync`** in
   `TrayApplicationContext.cs` tears down and rebuilds the entire OpenVR worker
   on *every* desktop settings change, including the two new checkboxes. The
   in-VR dashboard save/delete path already does a real hot update via
   `BridgeEngine.UpdateShortcuts` and never restarts the worker; the desktop
   settings page should do the same for settings that need no OpenVR re-init.
   Pre-dates today; unrelated to the grip bug.
3. **Chat/Notifications Phases 2–3** per `CHAT_AND_NOTIFICATIONS_PLAN.md`.
4. **Phase 4 (user-requested):** configurable notification colours, size,
   position and fade.

**Live-test gaps** — `LIVE_TEST_RESULTS.md` marks these not run: notification
head-relative positioning (step 4), idle persistence (7), dashboard navigation
(11), notification-while-dashboard-open (13), and delivery confirmation after
closing the dashboard (12). Frame-timing impact of an active notification is
**unmeasured** — recorded as unverified, not as "no impact".

---

## 4. Identifiers that must never change

| Identifier | Where | Why |
|---|---|---|
| `ie.lonelyviper.svrbridge.poc` | `src/SvrBridge/assets/app.vrmanifest` | SteamVR treats a new app key as a new application; the user's controller binding is registered against this key. |
| `/actions/svrbridge/...` | `actions.json`, `OpenVrInput.cs` | The packaged binding file maps these exact paths. |
| `"SVR Bridge settings v1"` | `UserSettings.cs` | DPAPI entropy for the protected Streamer.bot password. Changing it makes stored passwords unreadable. |

Keep the route direct: SteamVR input → detector → Streamer.bot WebSocket
`DoAction`. Preserve `BridgeEngine.UpdateShortcuts` hot updates, the child
OpenVR worker boundary, release latching, one request/one acknowledgement,
`ChordMode` numeric ordering (`Simultaneous=0, Modifier=1, LongPress=2,
DoublePress=3, SinglePress=4`), credential protection, and log redaction.
