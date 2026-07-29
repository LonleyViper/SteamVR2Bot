# SteamVR2Bot POC — live test results

Date: 2026-07-27  
Machine: Windows gaming PC  
Status: **complete — direct SteamVR action architecture is viable**

## Scope and secret handling

The supplied continuation handoff was used because `HANDOFF.md` is not present
in this repository. The proof retains the requested architecture:

```text
SteamVR logical actions -> SteamVR2Bot chord detector
    -> Streamer.bot WebSocket DoAction
```

No keyboard emulation, OpenVR2Key dependency, browser overlay, or unrelated
feature was added. The live Streamer.bot server has authentication disabled, so
no password was needed or copied. The only bridge configuration is the ignored
`artifacts/publish/appsettings.json`; `git check-ignore` is used to verify that
it cannot be committed accidentally.

## Environment

| Item | Live result |
|---|---|
| OS/time zone | Windows gaming PC; Europe/Dublin |
| .NET | SDK 10.0.302; `Microsoft.NETCore.App` 10.0.10 x64 |
| SteamVR | Installed at `C:\Program Files (x86)\Steam\steamapps\common\SteamVR`; build ID 23791826 |
| OpenVR | `openvr_api.dll` loaded from SteamVR `bin\win64`; live interface `IVRInput_011` |
| HMD | Lighthouse HMD serial `LHR-11E50FF6`; four base stations were discovered |
| Controllers | Vive controllers (`vive_controller`), serials `LHR-28FD5B57` and `LHR-549439CD`; both powered on and tracked |
| Streamer.bot | 1.0.4 at `E:\Streamer.bot`; local WebSocket server `ws://127.0.0.1:8080/1` |
| Test action | `SVR POC Test`, GUID `eea943b0-e358-47d3-8ccf-3a0dc49435d3`; enabled, 0 triggers, 0 sub-actions |

The test action is deliberately acknowledgement-only. This makes it harmless
while still providing an exact `DoAction` acknowledgement and Streamer.bot
action-history record for every attempt.

## Commands and raw results

### Prerequisite correction

Initial inspection confirmed that `dotnet` was absent. The current Microsoft
.NET 10 SDK was installed to the user profile with Microsoft's official
`dotnet-install.ps1` script after the machine-wide installer paused awaiting
elevation:

```text
Installed version is 10.0.302
Installation finished
```

No target-framework change was required.

### Release build and isolated self-test

Commands:

```powershell
dotnet build .\src\SvrBridge\SvrBridge.csproj --configuration Release
dotnet run --project .\src\SvrBridge\SvrBridge.csproj `
  --configuration Release --no-build -- --self-test
```

Raw result:

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
Connected to Streamer.bot at ws://127.0.0.1:<mock-port>/
Streamer.bot acknowledged 'SVR POC Test'.
SELF-TEST PASS: chord detection, authentication, and DoAction round trip.
```

`dotnet format --verify-no-changes` also passed.

### Publication and published self-test

Commands:

```powershell
.\scripts\Publish-Poc.ps1
.\artifacts\publish\SteamVR2Bot.Diagnostics.exe --self-test
```

Result: publication succeeded and the published executable repeated the same
passing authenticated mock WebSocket round trip.

### Streamer.bot action and configuration

Streamer.bot's saved main WebSocket server was inspected without displaying
credential values. It is configured for `127.0.0.1:8080/1`, auto-starts, and
has authentication disabled. Streamer.bot was restarted and the listener was
verified on TCP 8080.

A dedicated empty action was created through the Streamer.bot UI and saved:

```text
id: eea943b0-e358-47d3-8ccf-3a0dc49435d3
name: SVR POC Test
enabled: true
group: 30 VR Commands
triggers: 0
sub-actions: 0
```

The bridge uses the GUID, with the name retained for readable acknowledgement
logging.

### Twenty-attempt simulation proof

Command:

```powershell
.\artifacts\publish\SteamVR2Bot.Diagnostics.exe --simulate
```

The test toggled the modifier down once, then sent 20 trigger presses at
700 ms intervals (configured cooldown: 500 ms), followed by `Q`.

Raw sequence excerpt:

```text
Simulation mode:
Button One / modifier: DOWN
Button Two pressed; chord fired: True
Connected to Streamer.bot at ws://127.0.0.1:8080/1
Streamer.bot acknowledged 'SVR POC Test'.
```

This trigger/acknowledgement pair repeated exactly 20 times.

| Measure | Result |
|---|---:|
| Trigger presses | 20 |
| Chord detections | 20 |
| Streamer.bot acknowledgements | 20 |
| Suppressed/false fires | 0 |
| Duplicate acknowledgements | 0 |
| WebSocket connections | 1 |
| Process exit code | 0 |

Streamer.bot's own log independently records all 20 `DoAction` requests,
queue operations, and executions for the same action GUID.

### SteamVR application registration

The original `vrpathreg addmanifest` approach is not supported by the installed
`vrpathreg.exe`. The corrected implementation uses
`IVRApplications_006.AddApplicationManifest` from a
`VRApplication_Utility`.

Command:

```powershell
.\scripts\Register-SteamVrApp.ps1
```

Raw result:

```text
OpenVR DLL: C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openvr_api.dll
Registered SteamVR application manifest: C:\Users\Chann\Documents\Codex Steamvr Bindings\artifacts\publish\app.vrmanifest
```

SteamVR persisted the published manifest path in
`C:\Program Files (x86)\Steam\config\appconfig.json`.

### Live OpenVR initialization

With SteamVR running, the published bridge was started and left polling.

Raw result:

```text
OpenVR DLL: C:\Program Files (x86)\Steam\steamapps\common\SteamVR\bin\win64\openvr_api.dll
OpenVR input interface: IVRInput_011
SteamVR input active. Press Ctrl+C to stop.
Gesture: hold Button One, then press Button Two.
```

SteamVR associated the process with
`ie.lonelyviper.svrbridge.poc`, accepted the action manifest, and logged no ABI,
manifest, priority, or polling failure. The remaining expected log message is:

```text
[Input] ie.lonelyviper.svrbridge.poc (lighthouse_hmd) has no configured binding.
Input will not be available
```

This message is harmless because the bridge has no headset buttons. After both
Vive controllers were powered on, a SteamVR binding-editor defect prevented the
first personal binding from being saved:

```text
Cannot read properties of undefined (reading 'trim')
```

The app now ships the same two-hand mapping as its standard
`vive_controller` default binding:

```text
left grip  -> Chord Button One / Modifier
right grip -> Chord Button Two / Trigger
```

The published bridge was restarted. SteamVR independently confirmed the
binding was loaded:

```text
[Input] ie.lonelyviper.svrbridge.poc (vive_controller) attempting to load
default config from .../bindings_vive_controller.json
[Workshop] Successfully loaded binding file
'...\artifacts\publish\bindings_vive_controller.json'
for app 'ie.lonelyviper.svrbridge.poc'.
```

### Physical-controller smoke test

With the SteamVR dashboard closed, one physical Vive-controller gesture was
performed: hold left grip, press and release right grip, then release left
grip. The bridge recorded exactly one state transition and one chord:

```text
INPUT one=DOWN two=up
INPUT one=DOWN two=DOWN
CHORD detected.
Connected to Streamer.bot at ws://127.0.0.1:8080/1
Streamer.bot acknowledged 'SVR POC Test'.
INPUT one=DOWN two=up
INPUT one=up two=up
```

Streamer.bot independently recorded one matching `DoAction`, one queue entry,
and one execution at 11:25:27 local time. No duplicate or false fire occurred.

### Initial SteamVR Home/shell grip run

The dashboard-closed grip run produced 40 raw right-grip DOWN transitions and
29 acknowledged actions. The operator's physical count was uncertain. This run
proved that overlay input survived in the SteamVR shell, but it is not retained
as the final no-miss/no-duplicate result because the side-grip mapping was later
replaced by the more reliable index trigger.

| Measure | Result |
|---|---:|
| Raw SteamVR trigger-down transitions | 40 |
| Chord detections | 29 |
| Unique WebSocket request IDs | 29 |
| Streamer.bot queue entries | 29 |
| Streamer.bot executions | 29 |
| Bridge acknowledgements | 29 |
| Duplicate or false fires | 0 |
| Minimum inter-press gap | 612 ms |
| Maximum inter-press gap | 2815.7 ms |

On the first accepted press, Streamer.bot closed the bridge's idle WebSocket without a
close handshake. The bridge's single automatic retry reconnected immediately,
delivered that same request successfully, and produced exactly one execution.
The remaining 28 presses used the recovered connection. This provides an
additional live check of the bounded reconnect behavior.

### GERONIMO right-index-trigger run

After a right-grip test exposed missed rapid presses, the live cooldown was
reduced from 500 ms to 250 ms. A second grip run produced 19/19 actions from 19
SteamVR events, but SteamVR did not surface one of the operator's 20 physical
grip attempts. The packaged Vive mapping was therefore changed to left grip
plus right index trigger.

With GERONIMO active and the SteamVR dashboard closed, the final game run
produced:

| Measure | Result |
|---|---:|
| Physical trigger attempts | 20 |
| Raw SteamVR trigger-down transitions | 20 |
| Chord detections | 20 |
| Unique WebSocket request IDs | 20 |
| Streamer.bot queue entries | 20 |
| Streamer.bot executions | 20 |
| Bridge acknowledgements | 20 |
| Duplicate or false fires | 0 |
| Minimum inter-press gap | 454.8 ms |
| Maximum inter-press gap | 624.3 ms |

### Final SteamVR shell/Home right-index-trigger run

GERONIMO was fully closed before this run. An independent OpenVR
`IVRApplications_008.GetCurrentSceneProcessId` probe returned PID `0`, and no
GERONIMO process remained. This confirms the run occurred in SteamVR's no-game
shell rather than with a scene application still owning the headset.

With the SteamVR dashboard closed, the packaged left-grip plus right-index-
trigger mapping produced:

| Measure | Result |
|---|---:|
| Physical trigger attempts | 20 |
| Raw SteamVR trigger-down transitions | 20 |
| Chord detections | 20 |
| Unique WebSocket request IDs | 20 |
| Streamer.bot queue entries | 20 |
| Streamer.bot executions | 20 |
| Bridge acknowledgements | 20 |
| Duplicate or false fires | 0 |
| Minimum inter-press gap | 374.8 ms |
| Maximum inter-press gap | 469.3 ms |

## Test matrix

| Environment | Planned attempts | Raw inputs | Chords | Streamer.bot acknowledgements | Duplicates | Result |
|---|---:|---:|---:|---:|---:|---|
| Simulation mode | 20 | 20 gestures | 20 | 20 | 0 | **Pass** |
| SteamVR Home, initial grip mapping | 20 | Uncertain physical count; 40 raw events | 29 | 29 | Indeterminate | Superseded by right-trigger mapping |
| SteamVR Home, right-trigger mapping | 20 | 20 trigger-down events | 20 | 20 | 0 | **Pass** |
| GERONIMO, right-grip run | 20 | 20 trigger-down events | 12 | 12 | 0 | **Fail:** 8 presses fell inside the 500 ms cooldown |
| GERONIMO, 250 ms right-grip retest | 20 | 19 trigger-down events | 19 | 19 | 0 | **Fail:** one physical grip attempt was not surfaced by SteamVR |
| GERONIMO, right-trigger run | 20 | 20 trigger-down events | 20 | 20 | 0 | **Pass** |
| VR game 2, if available | 20 | Pending | Pending | Pending | Pending | Optional |

## Code changes retained

The live review found and retained the registration correction:

- `SteamVrApplications.cs` registers application manifests through
  `IVRApplications.AddApplicationManifest`.
- `Program.cs` exposes `--register-steamvr`.
- `Register-SteamVrApp.ps1` invokes the bridge registration mode and fails on a
  nonzero exit code.
- `actions.json` declares the packaged Vive-controller default binding.
- `bindings_vive_controller.json` maps left grip to the modifier and the right
  index trigger to the trigger action.
- The default cooldown is 250 ms. The release latch remains required before a
  second fire, while the shorter cooldown retained all rapid live inputs.

No change to the direct SteamVR-input-to-Streamer.bot-WebSocket architecture was
needed. The live runtime proved DLL discovery, OpenVR overlay initialization,
`IVRInput_011`, action-manifest loading, overlay-global action-set activation,
bounded WebSocket reconnect, and reliable Streamer.bot delivery. The decisive
scene-application coexistence test passed in GERONIMO with 20 consecutive
attempts, 20 acknowledgements, and no miss or duplicate. The matching SteamVR
shell run also passed 20/20.

## Tray application checkpoint — 2026-07-27

The first daily-use tray slice was exercised against the same live services:

| Check | Result |
|---|---|
| Self-contained app launch by double-click | **Pass** |
| Streamer.bot action discovery | **Pass:** 248 enabled actions returned |
| Existing action matched by stable ID | **Pass:** `SVR POC Test` |
| Harmless **Test Streamer.bot** delivery | **Pass:** one acknowledgement |
| In-app SteamVR registration | **Pass** |
| Bridge start through **Save and Start** | **Pass:** `Ready for your shortcut` |
| Protected-settings round trip | **Pass:** password not stored as readable text |
| Console authenticated mock round trip | **Pass** |
| Physical tray-host SteamVR run | **Pass:** 20/20, no duplicate |

The action picker displayed the Streamer.bot group and friendly action name, but
saved the plain action name and stable ID. No password or authentication
material appeared in the app activity or test output.

### Physical tray-host regression

At 12:30 local time, the user performed a rapid 20-attempt run through
`SteamVR2Bot.exe`. No VR scene application was present when the run was
inspected, so this is recorded as the SteamVR shell regression.

| Measure | Result |
|---|---:|
| Raw right-trigger presses in tray activity | 20 |
| Tray action confirmations | 20 |
| Streamer.bot acknowledgements | 20 |
| Unique WebSocket request IDs | 20 |
| Streamer.bot queue entries | 20 |
| Streamer.bot executions | 20 |
| Duplicate or false fires | 0 |
| Minimum inter-press gap | 392 ms |
| Maximum inter-press gap | 517 ms |

### GERONIMO tray-host regression

GERONIMO started at 12:38:59 local time. SteamVR recorded PID `29408` changing
from `VRApplication_OpenXRInstance` to `VRApplication_OpenXRScene` at 12:39:01.
The tray connected as `VRApplication_Overlay` immediately afterward and loaded
the packaged Vive binding. The user then completed the dashboard-closed
controller run.

The operator produced 21 accepted physical trigger presses, one more than the
20-attempt minimum:

| Measure | Result |
|---|---:|
| Raw right-trigger presses in tray activity | 21 |
| Tray action confirmations | 21 |
| Streamer.bot acknowledgements | 21 |
| Unique WebSocket request IDs | 21 |
| Streamer.bot queue entries | 21 |
| Streamer.bot executions | 21 |
| Missed or duplicate actions | 0 |
| Minimum inter-press gap | 406 ms |
| Maximum inter-press gap | 573 ms |

This closes the first tray-application release gate. The extracted engine
preserved the proven input coexistence and one-gesture/one-action behavior in
both the SteamVR shell and an active VR scene application.

## Runtime-hardening development checkpoint — 2026-07-27

The next branch adds recovery, persistent diagnostics, and controller-aware
binding setup. Safe checks completed without changing the validated Vive
binding:

| Check | Result |
|---|---|
| Public OpenVR controller detection | **Pass:** left and right HTC Vive controllers |
| Current binding inspection | **Pass:** Left Grip + Right Trigger |
| Official SteamVR binding UI launch | **Pass** |
| Delayed Streamer.bot availability | **Pass:** bounded reconnect succeeded |
| Unconfirmed-delivery safety | **Pass:** request was not resent |
| Structured JSONL output | **Pass:** all entries parsed |
| Credential redaction | **Pass:** no readable password value |
| Protected settings with gesture mode | **Pass** |
| Release builds | **Pass:** zero warnings and errors |

This is an implementation checkpoint, not the restart acceptance result. The
remaining live matrix is listed in `NEXT_PHASE_PLAN.md`.

### Post-restart startup run

The tray process remained open while both SteamVR and Streamer.bot were
restarted. Process start times placed the tray at 13:08:52, SteamVR at 13:10:29,
and Streamer.bot at 13:10:40. The controller shortcut was then started at
13:14:01 and reached Ready before the physical run.

| Measure | Result |
|---|---:|
| Accepted controller presses | 18 |
| Streamer.bot acknowledgements | 18 |
| Missed or duplicate actions | 0 |
| Final bridge state | Stopped normally |

This passes post-restart startup and delivery. It does not close the active
reconnection gate because the controller shortcut was stopped during both
service restarts; that gate requires restarting each service while the shortcut
is already Ready.

### Active SteamVR restart failure and recovery fix

The first active restart attempt exposed a process-lifecycle flaw. SteamVR's
`vrserver.txt` recorded:

- 13:16:43 — sent a Quit event to tray PID `27692`.
- 13:16:48 — forcibly killed PID `27692` because it had not exited.

There was no .NET crash. The tray and OpenVR client were the same process, so
SteamVR's normal shutdown enforcement removed the host that was supposed to
retry. Streamer.bot could not show recovery activity after that host was gone.

The fix moves the OpenVR connection into a child input worker. The tray owns
settings, status, logs, and Streamer.bot delivery and no longer connects to
OpenVR itself.

A non-disruptive live worker-loss test then produced:

| Check | Result |
|---|---|
| Persistent tray PID | **Pass:** PID `23204` survived |
| Terminated input worker | PID `31380` |
| Reconnect status | **Pass:** retry announced after 1 second |
| Replacement input worker | **Pass:** PID `18832` |
| Binding restored | **Pass:** Left Grip + Right Trigger |
| Final state | **Pass:** Ready for your shortcut |

The self-contained published build repeated the boundary test successfully:
tray PID `21824` survived worker replacement `24908` → `23660`, then **Stop**
removed the worker cleanly.

Automated coverage also confirms that a Streamer.bot restart leaves an uncertain
request unresent and allows the following request to reconnect successfully.
An actual SteamVR restart remains the final confirmation of this new process
boundary.

## Dashboard stability headset retest — 2026-07-27

The packaged dashboard interaction candidate was exercised in the headset from
17:00 through 17:05 local time. The tray stayed at PID `29648` and the OpenVR
worker stayed at PID `26568`.

The run included repeated shortcut creation, heavy tolerance-slider
interaction, action browsing, saving, deleting, and recreating shortcuts. Every
logged wizard page change was followed by `SteamVR dashboard image loaded`;
there was no image-load failure, page-update exception, worker replacement, or
unexpected dashboard deactivation. The user reported that the UI appeared to
be functioning.

This passes the reported shortcut-creation and slider-disappearance regression
for the tested session. Continue watching it during normal use, but do not keep
it classified as an active reproducible blocker without new evidence.

Focused-dashboard raw input remains constrained by SteamVR. At 17:05:06 the
dashboard deactivated and hid; at 17:05:07 the bridge received `Right Grip`,
rendered Review, and SteamVR immediately activated and showed the dashboard
again. This confirms that the recorder, automatic review return, and worker are
functioning, while the physical input is unavailable until the system
dashboard yields focus.

## Focused-dashboard input probe — 2026-07-27

The read-only input probe ran in the headset from 18:13 to 18:15 local time on
tray PID `33608` and OpenVR worker PID `27056`. Neither PID changed, and the
run logged no warning or error at any level.

Two recorder sessions were captured. Both give the same answer, and it is
unambiguous: **SteamVR deactivates our action set while its dashboard is
open.**

While the dashboard was open, every one-second summary reported the same thing:

```text
input probe: dashboard visible=1 active=1 | actions 0/10 active, pressed none
    | legacy L=0x0 R=0x0 | overlay events 1094.
```

Each action read `err=0 active=0 state=0 origin=0x0` — not an error, not
`NoData`, simply no bound origin. Within 33-47 ms of the dashboard closing, all
eight physical actions acquired real origins and the press landed:

```text
18:14:29.514  input probe dashboard: visible=0 active=0.
18:14:29.547  input probe action right_grip: err=0 active=1 state=0 changed=0
                  origin=0x200037200000026B.
18:14:29.702  input probe action right_grip: err=0 active=1 state=1 changed=1
                  origin=0x200037200000026B.
18:14:29.719  SteamVR dashboard page: Review.
```

The session starting 18:13:43 shows the same shape at 18:14:02.520 through
18:14:03.197.

This resolves several open questions at once:

| Question | Live answer |
|---|---|
| Is the recorder dropping the edge? | No. Both times the action became genuinely pressed, capture advanced to Review within 17 ms. |
| Is SteamVR returning `NoData`? | No. `err=0` in every state. |
| Does the legacy `GetControllerState` path still work? | No. `legacy L=0x0 R=0x0` in all three states, including at the moment of a confirmed press with a live origin. It is dead under the current input system. |
| Do raw button events reach the overlay queue? | No `ButtonPress` was seen, but only `PollNextOverlayEvent` is polled; `IVRSystem::PollNextEvent` is not. Treat this cell as untested rather than negative. |
| Are `button_one`/`button_two` bound? | They never became active in any state; they appear unbound in the current Vive binding. |

SteamVR's experimental overlay input override was already enabled before this
run — `steamvr.vrsettings` contains `"globalActionSetPriority" : true`. No
SteamVR setting was changed.

### Change made in response

SteamVR2Bot was activating its action set at `k_nActionSetOverlayGlobalPriorityMin`
(`0x01000000`), the bottom of the overlay-global band. The recorder now requests
`k_nActionSetOverlayGlobalPriorityMax` (`0x01FFFFFF`) while the `RecordInput`
page is showing and returns to the standard priority on every other page, so a
running VR game is never outranked outside recording. If SteamVR rejects the
elevated value, `UpdateActionState` falls back to the standard priority and
stops asking rather than taking the input worker down mid-recording.

This is not yet confirmed in the headset. It is confirmed only that the change
builds, passes both suites, publishes, and starts cleanly.

### Priority elevation ruled out, raw button forwarding discovered — 2026-07-27

Two further headset runs at 18:24 and 18:31 tested the recorder-scoped
elevation to `k_nActionSetOverlayGlobalPriorityMax`.

SteamVR **accepted** the elevated priority — no rejection, no fallback, `err=0`
throughout — and deactivated the action set anyway:

```text
18:24:21.300  SteamVR action set priority: 0x01FFFFFF.
18:24:22.307  input probe: dashboard visible=1 active=1 | actions 0/10 active,
                  pressed none | legacy L=0x0 R=0x0 | overlay events 96.
```

**Priority is not the lever. Path 2 is exhausted.**

The same runs showed that SteamVR does forward some raw controller buttons to
the overlay event queue while its dashboard is open and focused:

```text
18:24:22.101  input probe overlay ButtonPress: device=6 button=2 (Right Grip).
18:24:22.307  input probe overlay ButtonUnpress: device=6 button=2 (Right Grip).
18:31:41.882  input probe overlay ButtonPress: device=6 button=1 (Right Menu Button).
18:31:42.088  input probe overlay ButtonUnpress: device=6 button=1 (Right Menu Button).
```

Ten clean Grip press/release pairs and a Menu pair, correctly resolved to hand
and friendly name, all at `dashboard visible=1 active=1`.

The pattern across both runs is consistent with SteamVR forwarding the controls
it does not use to drive its own dashboard, and converting the ones it does use
into UI events instead:

| Input | Reaches us while dashboard focused | Delivered as |
|---|---|---|
| Grip | Yes | `VREvent_ButtonPress` / `ButtonUnpress`, button 2 |
| Menu | Yes | `VREvent_ButtonPress` / `ButtonUnpress`, button 1 |
| Trackpad | Yes, but not as a button | `VREvent_ScrollDiscrete` (305) |
| Trigger | No | nothing observed; no button event and no `VREvent_MouseButtonDown` |

`VREvent_MouseButtonDown`/`Up` (301/302) never appeared in any probe window.
The recorder page offers nothing to click, so this run cannot say whether a
laser click on our own page would arrive; it does confirm that no trigger press
reached us by any route.

This is the likely shape of the platform constraint: while the SteamVR
dashboard is focused, the trigger *is* the dashboard's click and the trackpad
*is* its scroll. Recording those two as shortcut inputs at the same moment the
user is operating the UI with them may not be possible at all. Grip and Menu
are unaffected and are proven recordable in place.

### Decision

The investigation is closed. Priority elevation was reverted, since SteamVR
accepted it and changed nothing; the action set stays at `0x01000000`. The
existing flow is kept: the recorder offers the controller-aware picker, and its
instruction now states the live route plainly — close the SteamVR menu, press
the input, and SteamVR2Bot reopens on Review by itself. The read-only probe
stays wired, scoped to the recorder page, for future diagnosis. The
live-proven-dead legacy `GetControllerState` polling and its probe fields were
removed afterward; explicit SteamVR actions remain the physical-input source.

## Rename to SteamVR2Bot and desktop window fix — 2026-07-27

### Desktop window could not be opened

Reproduced and fixed. Launching the packaged tray with a hidden window state
produced a process with **no main window at all**, permanently:

| Launch | `MainWindowHandle` | Visible |
|---|---|---|
| `Start-Process -WindowStyle Hidden` | 0 | no window exists |
| `Start-Process` | present | yes |

Windows keeps the hidden state a process was launched with for that process's
first `ShowWindow` call and ignores the state that call asks for. WinForms still
recorded the form as visible, so `ShowMainWindow` skipped `Show()` and only
called `Activate()`, which does nothing to a window that was never displayed.
Both the tray icon and its Open item silently did nothing, with no way back.

`ShowMainWindow` now asks Windows through `IsWindowVisible` instead of trusting
`Form.Visible`, and issues a second `ShowWindow` when the window really is
hidden. Startup routes through the same method. Verified against the exact
failure condition:

```text
Start-Process -WindowStyle Hidden
pid=24204  handle=12454906  title='SteamVR2Bot — VR shortcuts'
IsWindowVisible : True
```

Publishing and restarting no longer starts the app hidden.

### Rename

`SVR Bridge` is now `SteamVR2Bot` across the desktop window, tray icon and menu,
SteamVR dashboard, application manifest, logs, scripts, and documentation.
Executables are `SteamVR2Bot.exe` and `diagnostics\SteamVR2Bot.Diagnostics.exe`;
the stale old-named executables were removed from `artifacts\publish`.

Three identifiers were **deliberately left unchanged**, because changing them
breaks working setups for no user-visible gain:

| Identifier | Why it stayed |
|---|---|
| `ie.lonelyviper.svrbridge.poc` app key | SteamVR would treat a new key as a new application and orphan the existing Vive binding, which is registered against this key in `steamvr.vrsettings`. |
| `/actions/svrbridge/...` action paths | The packaged binding file maps these exact paths; renaming them invalidates every binding. |
| `"SVR Bridge settings v1"` DPAPI entropy | It is part of the key protecting any saved Streamer.bot password. Changing it makes stored passwords unreadable. |

`%LOCALAPPDATA%\SVR Bridge` moves to `%LOCALAPPDATA%\SteamVR2Bot` once, on first
resolve, before anything can create the new folder. The live migration carried
across three saved shortcuts, the Streamer.bot address, the log history, and the
cached dashboard images; the old folder was not recreated afterwards.
`TestRenamedDataDirectoryMigration` covers the move, the repeat-resolve no-op,
a fresh install, and a leftover old folder losing to the current one.

## Phase 1 — overlay substrate — 2026-07-29

### Scope

New `IVROverlay` vtable indices, a multi-instance `VrOverlaySurface`, and a
hard-coded test overlay pinned to the left controller. No chat window, no
notifications, no gaze detection, no WPF — those are Phases 2–5.

### Index derivation and cross-check

Indices were derived in one pass over the `IVROverlay` declaration order in
ValveSoftware/openvr `headers/openvr.h`, at the revision whose
`IVROverlay_Version` is literally `"IVROverlay_028"` — the same string
`TryGetOverlayTable` requests. SHA-256 of the header used:

```text
1E6ED57199896CC1F7C5484E50FA18955E97BE15BE690BEB28D998C877EAD7FD
```

That revision also carries `IVRSystem_026` and `IVRInput_011`, the two other
versions this codebase requests, so all three agree on one header. The installed
SteamVR was independently confirmed to implement `IVROverlay_028` by reading the
interface strings out of `bin\vrclient_x64.dll`.

All ten previously hardware-validated anchors landed exactly where the
enumeration predicted:

| Method | Expected | Enumerated |
|---|---:|---:|
| `SetOverlayFlag` | 11 | 11 |
| `SetOverlayWidthInMeters` | 22 | 22 |
| `PollNextOverlayEvent` | 48 | 48 |
| `SetOverlayInputMethod` | 50 | 50 |
| `SetOverlayMouseScale` | 52 | 52 |
| `SetOverlayFromFile` | 63 | 63 |
| `CreateDashboardOverlay` | 67 | 67 |
| `IsDashboardVisible` | 68 | 68 |
| `IsActiveDashboardOverlay` | 69 | 69 |
| `ShowDashboard` | 72 | 72 |

10/10. The eleven indices added from the same pass are therefore trustworthy:

| Method | Index |
|---|---:|
| `FindOverlay` | 0 |
| `CreateOverlay` | 1 |
| `DestroyOverlay` | 3 |
| `SetOverlayAlpha` | 16 |
| `SetOverlaySortOrder` | 20 |
| `SetOverlayCurvature` | 24 |
| `SetOverlayTransformTrackedDeviceRelative` | 35 |
| `ShowOverlay` | 43 |
| `HideOverlay` | 44 |
| `IsOverlayVisible` | 45 |
| `SetOverlayRaw` | 62 |

### Live index verification — passed

Every new index was exercised against running SteamVR before any headset test,
through a throwaway probe built against `SvrBridge.Core`:

```text
SupportsOverlaySurfaces = True
before create: found = False          (expect False)
CreateOverlay handle = 236223201307   (expect non-zero)
FindOverlay found = True, handle = 236223201307   (expect True + same)
SetOverlayWidthInMeters ok
SetOverlayAlpha ok
SetOverlaySortOrder ok
SetOverlayCurvature ok
SetOverlayRaw ok
left controller device index = 5
SetOverlayTransformTrackedDeviceRelative ok
ShowOverlay ok, IsOverlayVisible = True    (expect True)
HideOverlay ok, IsOverlayVisible = False   (expect False)
after destroy: found = False          (expect False)
RESULT: PASS
```

`ShowOverlay`, `HideOverlay` and `IsOverlayVisible` are the strongest evidence
in that run: all three take `ulong` and are indistinguishable by signature, so a
swapped index would compile and run — and visibility tracked correctly in both
directions.

### Automated suites

| Suite | Result |
|---|---|
| `SteamVR2Bot.Diagnostics.exe --self-test` | PASS |
| `SteamVR2Bot.exe --self-test` | PASS (exit 0) |
| Build, all three projects | 0 warnings, 0 errors |

`TestOverlayHandleRoundTrip` was added to `TraySelfTests`: create → find →
destroy → find-again, so a wrong vtable index fails loudly at startup instead of
as an access violation mid-stream. It skips when SteamVR is not running, which
is why the manual matrix below still matters.

### Manual headset test — substrate and regressions passed, 2026-07-29

Run on the development PC with the Debug build, which is the binary SteamVR is
registered against in `appconfig.json`. Results are recorded from what the user
reported observing; unticked rows were not checked and are **not** assumed to
have passed.

Preparation: SteamVR running, both Vive controllers on and tracked, SteamVR2Bot
running, at least one saved shortcut bound to a Streamer.bot action.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Tray menu → **Show VR test overlay (developer)** | Menu item checks; activity log shows "The VR test overlay is on" and "following left controller device N" | **PASS** |
| 2 | Put the headset on and look at the left controller | A dark panel with a blue border reading "SteamVR2Bot / overlay test - left controller" sits just above the controller, tipped towards you | **PASS** |
| 3 | Check colours | Border is blue and background dark navy — **not** orange/brown. Wrong colours mean the BGRA→RGBA swap is inverted | **PASS** |
| 4 | Move the left controller around | Panel follows the hand with no lag or detachment | **PASS** |
| 5 | Put the left controller down until it sleeps, then wake it | Panel reattaches on its own. Log shows a new "following left controller device N" line, possibly with a different N | **PASS** |
| 6 | Turn the left controller off entirely | Log shows "waiting for a left controller"; app does not crash or spam | not run |
| 7 | Turn it back on | Panel reattaches | covered by 5 |
| 8 | Open the SteamVR dashboard → SteamVR2Bot | Dashboard opens and renders as before | **PASS** |
| 9 | Click through Create Shortcut → gesture type → tolerance → action picker | All pages render and respond as before; the test overlay stays visible alongside | **PASS** |
| 10 | Save a shortcut | Saves, appears in the list, no worker restart | not run |
| 11 | Close the dashboard and fire an existing shortcut | Streamer.bot action fires exactly once, no duplicates | **PASS** — user-reported working; no miss/duplicate count was taken |
| 12 | Tray menu → uncheck **Show VR test overlay** | Panel disappears; log shows "The VR test overlay is off" | not run |
| 13 | Re-check it | Panel reappears — proves `DestroyOverlay` released the key rather than leaking it | not run |
| 14 | Exit SteamVR2Bot, then relaunch and re-enable | Panel appears; no `KeyInUse` error | not run |

### What steps 1–9 establish

This is the first time the Phase 1 substrate has been proven against a headset
rather than against SteamVR's API alone:

- **The eleven new vtable indices are correct in practice.** A panel appeared,
  in the right place, with the right pixels. That exercises `CreateOverlay`,
  `SetOverlayRaw`, `SetOverlayWidthInMeters`, `ShowOverlay`,
  `SetOverlayTransformTrackedDeviceRelative`, `SetOverlayAlpha`,
  `SetOverlaySortOrder` and `SetOverlayCurvature` end to end.
- **`SetOverlayRaw` renders correctly**, including the BGRA→RGBA channel swap.
  Step 3 is the only check that catches an inverted swap, because wrong colours
  otherwise read as a deliberate palette rather than a bug.
- **Device-index re-resolution survives a real sleep/wake cycle** (step 5). This
  is the hazard §4 of the plan flagged from OpenVRTwitchChat, and it is the one
  that fails silently — a cached index would have detached the panel with no
  error anywhere.
- **The dashboard did not regress** (steps 8–9). `VrOverlayFunctions` changed
  from a positional record to named properties and the dashboard shares that
  table, so this was the real risk of the refactor and it is now retired.
- **Shortcut delivery did not regress** (step 11). Existing shortcuts still
  reach Streamer.bot with the new overlay table in place. This is the check that
  matters most, because a rendering dashboard proves the table is wired but says
  nothing about whether a gesture still completes the
  `raw edge -> detector -> DoAction -> acknowledgement` route.

With 11 passing, **the product contract is intact and Phase 1 is functionally
complete.** What remains is teardown hygiene, not whether the substrate works.

### Still outstanding

- Steps 12–14 cover overlay teardown: whether `DestroyOverlay` actually releases
  the key rather than leaking it. A leak shows up as `KeyInUse` on the second
  enable, so toggling the overlay off and back on once is enough to find it.
  This is the only outstanding item that could indicate a real defect.
- Step 10 (saving a *new* shortcut from the VR wizard) was not run; step 11
  covered existing ones only. The save path touches settings persistence and
  `UpdateShortcuts`, neither of which this change went near.
- Step 6 (controller fully powered off) is the unassigned-role path. Low risk,
  since step 5 already exercised re-resolution across a sleep/wake cycle.

No miss/duplicate count was taken for step 11. The delivery-count discipline
used for the original 20/20 and 21/21 runs was not repeated here, because this
was a regression check on an unchanged delivery path rather than a change to it.
