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
| 12 | Tray menu → uncheck **Show VR test overlay** | Panel disappears; log shows "The VR test overlay is off" | **PASS** — user-reported clear, ahead of Phase 2 |
| 13 | Re-check it | Panel reappears — proves `DestroyOverlay` released the key rather than leaking it | **PASS** — user-reported clear, ahead of Phase 2 |
| 14 | Exit SteamVR2Bot, then relaunch and re-enable | Panel appears; no `KeyInUse` error | **PASS** — user-reported clear, ahead of Phase 2 |

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

- Steps 12–14 (overlay teardown: whether `DestroyOverlay` actually releases the
  key rather than leaking it) were confirmed clear by the user ahead of Phase 2,
  closing the only item that could have indicated a real defect. No detailed
  timestamp/log excerpt was captured for that run, so this is recorded as
  user-reported rather than with a raw trace, consistent with how step 11 above
  is recorded.
- Step 10 (saving a *new* shortcut from the VR wizard) was not run; step 11
  covered existing ones only. The save path touches settings persistence and
  `UpdateShortcuts`, neither of which this change went near.
- Step 6 (controller fully powered off) is the unassigned-role path. Low risk,
  since step 5 already exercised re-resolution across a sleep/wake cycle.

No miss/duplicate count was taken for step 11. The delivery-count discipline
used for the original 20/20 and 21/21 runs was not repeated here, because this
was a regression check on an unchanged delivery path rather than a change to it.

### Packaging — 2026-07-29

`scripts\Publish-Poc.ps1` republished both self-contained single-file binaries
over the previous 28/07 package. The tagged
`artifacts\release\SteamVR2Bot-v0.1.1-windows-x64` copy was left untouched, so
the last released build is still recoverable.

| Artifact | Built | Packaged self-test |
|---|---|---|
| `artifacts\publish\SteamVR2Bot.exe` | 2026-07-29 12:43 | PASS (exit 0) |
| `artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe` | 2026-07-29 12:43 | PASS |

SteamVR was running during the packaged tray self-test, so
`TestOverlayHandleRoundTrip` executed rather than skipped. The
create → find → destroy → find-again round trip therefore holds in the
**published single-file binary**, not only in the Debug build — which matters,
because that is the binary a user installs and the one whose overlay indices
would fail silently if trimming or single-file packaging had disturbed the
interop layer.

`artifacts/` remains gitignored; no binary entered the repository.

### Version note

The published package is Phase 1 code still carrying the v0.1.1 version from
the previous release. It has not been tagged or renamed as a new release, so
this package is a development build rather than a shippable one.

## Phase 2 — head-anchored notifications — not yet run

### Scope

A single persistent `VrOverlaySurface` anchored to the HMD, painted once per
notification through a new WPF `RenderTargetBitmap` pipeline, animated with
`SetOverlayAlpha` on a fade-in/hold/fade-out timeline sized from the payload's
`DurationMs`. A bounded, drop-oldest queue plays a burst in sequence. Driven by
`Target == Notification` payloads on the Phase 0 event stream, gated behind a
new **Notifications** setting that defaults off. No chat window, wrist anchor,
gaze detection or interaction — those are Phases 3–5.

### What is already covered without a headset

All automated: `NotificationPlayer` queue ordering and bounded drop-oldest
behaviour, the 500–60000 ms duration clamp applied end to end from a parsed
JSON payload through to playback, the alpha curve reading 0 at the start of a
notification, 1 through its hold, and back towards 0 by the end of its
duration, `WpfRenderThread` starting and shutting down cleanly (dispatched
work runs, the OS thread is confirmed joined afterward), and a solid-colour
render through the full WPF → un-premultiply → BGRA→RGBA-swap pipeline
reproducing its source bytes within integer-rounding tolerance. Both self-test
suites pass and both projects build with zero warnings. None of this proves
the panel is visible, correctly placed, or correctly coloured in the headset —
only the manual steps below do that.

### Preparation

SteamVR running, both Vive controllers on and tracked, SteamVR2Bot running
with at least one saved shortcut. In **Settings**, turn on **Listen for
Streamer.bot chat and events** and **Show notification broadcasts in the
headset**. Trigger a notification from Streamer.bot with a C# sub-action
calling `CPH.WebsocketBroadcastJson` with a body such as
`{"target":"notification","title":"Test","text":"Hello from Streamer.bot","duration":4000,"accent":"#60C8FF"}`.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Turn on both settings above, save, trigger one notification | A panel fades in a comfortable distance in front of and slightly below eye level, wherever the wearer is looking, with the title and text legible and facing the wearer (not blank or mirrored — the head-anchor transform is translation-only, unlike the wrist test overlay's tipped one, and has not been headset-confirmed) | **PASS** — user-reported legible and correctly oriented |
| 2 | Check colours on the panel and its accent border | Background reads dark navy, the border reads the requested accent colour (`#60C8FF` → blue) — **not** orange/brown, which would mean the BGRA↔RGBA swap is inverted | **PASS** — user-confirmed |
| 3 | Watch the fade in and the fade out | Both are smooth ramps with no pop to full opacity and no visible step; the panel is not stuck part-way transparent at any point | **PASS** — user-confirmed |
| 4 | Turn the head while the panel is showing | The panel stays in the same relative spot in front of the eyes rather than staying fixed in the room | **PASS** — user-confirmed |
| 5 | Trigger two notifications back to back (within a second of each other) | They play one after another, not overlapping and not silently dropping the second | **PASS** — subsumed by step 6's burst run |
| 6 | Trigger four or five notifications in a rapid burst | All queue and play in the order sent; none are skipped and the app does not fall behind indefinitely | **PASS** — activity log shows 5 payloads received within 1 s at 13:32:23–24, then "A notification is showing" four more times at 13:32:23/26/29/31, matching the queued 2.5 s duration with no overlap or drop |
| 7 | Let a notification finish and leave the feed idle for a minute | The panel is gone and stays gone; no flicker, no stray reappearance | **PASS** — user-confirmed |
| 8 | Send a broadcast with `target` other than `notification` (e.g. `chat`) | No panel appears; the activity log still shows the payload arriving | **PASS** — activity log shows 8 chat payloads received (13:33:19 ×4, 13:33:53–54 ×4) with **no** "A notification is showing" line after any of them |
| 9 | Turn off **Show notification broadcasts in the headset**, trigger one | No panel appears | **PASS** — activity log shows payload 15 received at 13:34:53 after the setting was unchecked, with no "A notification is showing" line; user confirmed the panel did not appear |
| 10 | Open the SteamVR dashboard → SteamVR2Bot | Dashboard still opens and renders exactly as before | **PASS** — "SteamVR dashboard image loaded" and repeated activate/deactivate cycles appear throughout the same session with no error |
| 11 | Click through the shortcut list and action picker on the dashboard | Pages render and respond as before; unaffected by the notification surface | **PASS** — user-confirmed |
| 12 | Close the dashboard and fire an existing shortcut | Streamer.bot action fires exactly once, no duplicates | **PASS** — user-confirmed; no detailed miss/duplicate count captured for this row, consistent with how Phase 1 step 11 above is recorded |
| 13 | Trigger a notification while the SteamVR dashboard is open | Panel still appears — per §8 of the plan, notifications are not suppressed while the dashboard is open | **PASS** — user-confirmed |

Steps 1–3, 5, 6, 8, 9 and 10 were confirmed from the 2026-07-29 13:28–13:35
session (tray log pasted by the user) plus direct user confirmation of the
purely visual checks (legibility/orientation, colour, fade smoothness) that
the log cannot show. Steps 4, 7, 11, 12 and 13 were not exercised in that
same logged session, but were confirmed separately by the user on 2026-07-29
as passing, without a raw log excerpt — the same user-reported convention
used for Phase 1 steps 12–14 above. Step 12 (shortcut delivery with the
dashboard closed) is the product contract; the user separately reported and
fixed an unrelated grip-input regression the same day (see "Grip stolen from
running games" below), which did not affect shortcut delivery itself.

### Frame-timing impact

Not measured. The design keeps GPU work off the per-frame path — the texture
is rendered once per notification and the fade is driven by `SetOverlayAlpha`
alone, which the plan's §4 argues is cheap — but no capture of the running
game's frame times with and without an active notification has been taken.
This should be treated as unverified, not as "no impact confirmed", until a
frame-timing capture is actually run during an active VR game session with
notifications firing.

## Grip stolen from running games — root-caused and fixed, 2026-07-29

While SteamVR2Bot was running, Grip stopped working inside other VR games
(confirmed in Contractors VR and Showdown). Closing the app restored it
immediately, every time.

### Cause

The action set was being activated at `k_nActionSetOverlayGlobalPriorityMin`
(`0x01000000`). openvr.h defines that as the threshold at which an action set
**takes input away from the scene application**, and SteamVR's experimental
override that honours the band was already enabled — `steamvr.vrsettings`
carries `"globalActionSetPriority" : true`, set during the 2026-07-27
investigation above and never turned off.

Priority is per-action-set, not per-action, and `bindings_vive_controller.json`
claims grip, trigger, trackpad and menu on both hands unconditionally —
independently of what the user has mapped to a shortcut. So the app was not
observing those controls, it was taking all eight away from every running game.
The user has no shortcut mapped to Grip; it was being taken anyway.

This is the tail of the 2026-07-27 "Priority elevation ruled out" section. That
change scoped the *elevation* to the recorder and stated the intent plainly —
"a running VR game is never outranked outside recording" — but the **base**
priority was left sitting at the bottom of the overlay-global band rather than
returned to standard. The elevation was reverted; the base was not.

### Why it resisted diagnosis

| Signal | Why it misled |
|---|---|
| The app's own log showed Grip edges arriving normally | It is this app receiving them that *is* the bug, not evidence against it. |
| SteamVR's Controller Binding UI showed correct bindings | The binding config was fine. What broke is live input routing, which that UI does not show. |
| Per-commit bisect builds all reproduced it | The steal dates to 2026-07-27 and is present in every build since, so no commit boundary could isolate it. |
| `artifacts\publish` was believed to be a known-good pre-regression build | Its exe is stamped 2026-07-29 12:43, *after* every feature commit that day, and it ships the identical grip-claiming `actions.json`. |

### Fix

`OpenVrInput.ActionSetPriority` is now `0`, the ordinary band, with the reasoning
recorded at the constant so it is not re-raised casually.

| # | Check | Result |
|---|---|---|
| 1 | Grip inside a running VR game with SteamVR2Bot active | **PASS** — user-confirmed in headset |
| 2 | Mapped shortcuts still fire from inside a running game | **PASS** — user-confirmed in headset; the game and the bridge both receive the input |

Nothing was traded away: the elevated priority was never delivering an edge this
app needs, which the 2026-07-27 runs had already shown from the other direction
when SteamVR accepted `0x01FFFFFF` and deactivated the set under dashboard focus
regardless.

## Phase 3 — the wrist chat window — not yet run

### Scope

A second, wrist-anchored `VrOverlaySurface` pinned to the left controller,
permanently present rather than shown on demand: it scales up and fades in on
gaze via `SetOverlayAlpha`/`SetOverlayWidthInMeters`, driven by a
`ChatGazeHysteresis` two-threshold state machine so the effect does not
flicker at the boundary angle. A `ChatRingBuffer` (40 messages, oldest
evicted first) is written by the event stream's consumption loop and read by
a second `IVrPanelRenderer<ChatContent>` implementation, `WpfChatRenderer`,
which reuses the Phase 2 `WpfRenderThread` and `WpfOverlayPixelPipeline`
rather than forking either. Repaints are throttled to roughly 10 Hz by
`ChatRepaintThrottle`, decoupled from the gaze animation, which runs every
tick per §B2. No buttons, no laser, no `SetOverlayInputMethod` — an empty
`ChatOverlayLayout.Buttons` hit-rectangle table is declared for a later phase
but nothing reads it yet. Gated behind a new **Chat** setting, off by
default, mirroring **Notifications**.

### Revision during this phase: direct Twitch subscription, not just a relay action

§5c of the plan was revised mid-phase. The original route required the user
to hand-write a Streamer.bot C# action that relayed chat via
`CPH.WebsocketBroadcastJson` in this app's own `target: "chat"` shape. Manual
testing exposed real friction with that route (a broadcast-signature compile
error, and no visible confirmation on the Streamer.bot side that anything had
fired), so the design was changed to subscribe directly to Streamer.bot's own
`Twitch.ChatMessage` event alongside the existing `General.Custom`
subscription — Streamer.bot still owns the entire platform integration
either way, since `Twitch.ChatMessage` is the parsed output of its own Twitch
connection, OAuth and reconnection handling, not raw Twitch data. This is
implemented in `StreamerBotEventStream` (Subscribe now asks for both event
categories) and a new isolated `TwitchChatMessageMapper`, which maps whichever
of two confirmed Streamer.bot event shapes arrives (see below) into the same
`StreamerBotEventPayload` a hand-written relay action would have produced.
`General.Custom` still works unchanged and remains the route for SB-side
filtered alerts and Phase 2 notifications.

**The exact Twitch.ChatMessage field names were confirmed from documentation,
not yet from a live payload.** `docs.streamer.bot`'s current schema and the
`@streamerbot/client` npm package's bundled type definitions (version 2.0.1,
still the latest published as of this writing) describe two different
shapes — confirming the plan's own warning that "the event schema has changed
across Streamer.bot versions":

| | Newer (docs.streamer.bot) | Older (`@streamerbot/client@2.0.1` types) |
|---|---|---|
| Wrapper | none — fields at the top level | everything inside a `message` object |
| Display name | `user.name` | `message.displayName` |
| Text | top-level `text` | `message.message` |
| Colour | `user.color` | `message.color` |
| Badges | `user.badges[].name` | `message.badges[].name` |
| Subscriber flag | `user.subscribed` | `message.subscriber` |

`TwitchChatMessageMapper` reads both shapes defensively — see its own remarks
for the exact fallback order — and is covered by unit tests for both shapes
plus a live mock-WebSocket round trip in `SvrBridge/SelfTests.cs`
(`TestTwitchChatMessageMapper`, and the Twitch.ChatMessage frame added to
`TestStreamerBotEventStreamAsync`). None of that proves which shape a real,
currently-running Streamer.bot instance actually sends — that is still
outstanding and needs the manual step below.

### Emote handling: real images, fetched through Streamer.bot, no new dependencies

Two stages, both landed in this phase. First, `TwitchChatMessageMapper` was
extended to read the `emotes` array both schema shapes carry (plus
`cheerEmotes` on the older shape) into a new `StreamerBotEventPayload.
EmoteNames` list — a platform-agnostic field a hand-authored `General.Custom`
payload can populate too, not something Twitch-specific leaking into the
shared contract — and `WpfChatRenderer` styled any matching token distinctly
(italic, accent colour) instead of plain body text.

Second, real images. A diagnostic (`SteamVR2Bot.exe --inspect-twitch-emotes`,
kept in the tray project - connects with the saved Streamer.bot address and
password and prints one raw `TwitchGetEmotes` response) was run live and
confirmed that request aggregates **Twitch's own emotes, BetterTTV,
FrankerFaceZ and 7TV globals in one response**, 608 entries in the session
tested, each with a ready CDN `imageUrl`. That means none of those platforms'
own APIs need to be called from SVR Bridge - Streamer.bot has already done
the aggregation. Still zero new NuGet packages: `HttpClient` and WPF's own
`BitmapImage` decode are already available.

New pieces:

- `TwitchEmoteCatalog` (Core) - parses that response into a name → URL
  lookup. Pure, tested without network or headset.
- `EmoteImageCache` (Tray, runs in the OpenVR worker process) - given a name,
  returns an already-decoded, frozen `BitmapImage` if one is cached, or
  starts a background fetch and returns immediately otherwise. Never blocks a
  repaint. A failed fetch is not cached as a permanent miss - the next
  occurrence of that emote in chat retries naturally, no separate retry timer.
- The tray process fetches the catalog once per connected event stream (via
  the same `StreamerBotEventStream` already used for chat/notifications) and
  pushes the name → URL map to the OpenVR worker over the existing command
  channel (`OpenVrWorkerCommand.EmoteCatalog`) - the worker never talks to
  Streamer.bot directly. A catalog arriving before the chat window has been
  created yet (its own overlay is created lazily, on the first chat message)
  is held and applied the moment it is.
- `WpfChatRenderer` now embeds the real image via `InlineUIContainer` when
  one is cached, falling back to the styled-text treatment when it is not
  (self-tests, an image genuinely still downloading, or one Streamer.bot
  simply does not know about) - the fallback from the first stage never
  became dead code.
- `ChatOverlay`'s repaint throttle now combines the ring buffer's version
  with the image cache's `Version`, so an emote that finishes downloading
  *after* its message was already painted as text still earns a repaint
  rather than being stuck as text until a new message arrives.

Covered without a headset or real network: `TestTwitchEmoteCatalog` (parses
the exact live-confirmed shape, skips malformed entries),
`TestChatImageCacheFetchesDecodesAndCaches` (a fake `HttpMessageHandler`
serving a real 1x1 PNG - proves the fetch/decode/cache/`Version` path end to
end with no network), `TestChatImageCacheRetriesAfterAFailedFetch` (a
failure is not cached, a later request retries), and
`TestChatRenderEmbedsCachedEmoteImage` (a message with an already-cached
emote produces an `InlineUIContainer`, not a text `Run`). Plus the earlier
`TestChatRenderStylesEmoteTokensDistinctly` for the text-fallback path, and
the emote assertions in `TestTwitchChatMessageMapper` and
`TestStreamerBotEventPayload`.

### A real catalog-delivery race, caught live

The first headset test after shipping real emote images showed nothing at
all - not styled text, not images. The activity log showed why: the tray
process fetched the catalog from Streamer.bot and tried to hand it to the
OpenVR worker within about a second of launch, but the worker was not up yet
(it took ~34 seconds that run). Delivery failed, and the original code only
tried once - it marked the catalog "already fetched" before checking whether
delivery actually succeeded, so a perfectly good catalog was fetched and then
silently dropped forever for that session.

Fixed by separating the two concerns: fetching from Streamer.bot is still
marked done-once (no point asking twice), but delivering to the worker now
retries on a 2-second interval for up to 30 seconds, which is what
`EnsureEmoteCatalogAsync` in `TrayApplicationContext` does now. Confirmed live
afterward: two more launches both delivered successfully, one within a
second and one that would have needed the retry window had worker startup
been slower again.

### Badges: real icons too, no separate request needed

Unlike emotes, badge images did not need a catalog fetch at all. Twitch's own
`badges` array carries an `imageUrl` right on each entry, confirmed in both
known schema shapes during the earlier emote research.

The image cache used for emotes was generalised (and renamed `ChatImageCache`,
from `EmoteImageCache`) to key by URL directly rather than only by emote
name through a catalog - `TryGet(name)` for emotes still resolves through the
catalog, and a new `TryGetByUrl(url)` serves badges directly, sharing the same
fetch/decode/cache machinery and the same repaint-triggering `Version`
counter.

### A real bug, caught in the same headset session: only one badge ever showed

The first version picked a single badge from four hardcoded name categories
(broadcaster/moderator/vip/subscriber) and discarded everything else. Live
testing showed the channel owner's `Broadcaster` badge working, but no Prime
badge and no channel-specific custom badge - both silently dropped, because
neither matched any of the four categories and the code only ever kept one
match anyway. Real Twitch chat clients show every badge a chatter holds side
by side, not one guessed "most important" one.

Fixed by no longer guessing: `TwitchChatMessageMapper.ReadBadges` now keeps
every entry in the badges array, in Twitch's own order, each with its own
image URL. A small set of well-known names (broadcaster, moderator, vip,
subscriber/founder, premium → "Prime", partner, staff, turbo, bits) get a
short friendly label; anything else - which is exactly the "channel-specific
custom badge" case, impossible to enumerate in advance - is shown under its
own raw name rather than dropped. `StreamerBotEventPayload.Badges` carries
the full list; the existing singular `Badge`/`BadgeImageUrl` fields now
mirror the first entry, kept for a hand-authored `General.Custom` payload
that only ever needs one badge. `WpfChatRenderer` renders each badge in the
list in sequence, image when cached, bracketed text label otherwise.

Covered without a headset: `TestTwitchChatMessageMapper` now asserts a
message with three badges (moderator, Prime, an unrecognised
`glhf-pledge`-style custom badge) keeps all three, not just one - this
exact assertion caught a real interaction during development, where a
message with badges *and* the separate `subscribed` flag double-counted a
`Sub` entry, fixed by only applying that fallback when the badges array
did not already include one. Also: `TestChatRenderEmbedsCachedBadgeImage`
(a cached badge renders as an image; a badge with no URL still falls back
to bracketed text, never to nothing) and `TestChatRenderEmbedsMultipleBadges`
(three cached badge images all render, not just the first).

### What is already covered without a headset

All automated, in `TraySelfTests`: ring buffer eviction order and version
counting (`TestChatRingBufferEviction`), a burst of ten messages producing
exactly one repaint rather than ten (`TestChatRepaintThrottleCoalescesBurst`
— this caught a real bug during development, below), gaze hysteresis holding
steady rather than oscillating when fed the exact enter or exit boundary
value repeatedly (`TestChatGazeHysteresisNoOscillationAtBoundary`), a long
unbroken string (400 characters, no spaces) wrapping onto multiple lines
instead of overflowing the panel width (`TestChatRenderWrapsLongUnbrokenString`),
an empty username and an unparsable colour rendering without throwing
(`TestChatRenderHandlesEmptyUsernameAndColour`), and the ring buffer surviving
four writer threads appending 200 messages each while a reader thread
continuously snapshots it, with no exception and no lost or double-counted
append (`TestChatRingBufferThreadSafeConcurrentAccess`). Also in
`SvrBridge/SelfTests.cs`: both known `Twitch.ChatMessage` schema shapes
mapping correctly (`TestTwitchChatMessageMapper`), a subscriber flag with no
badges array still producing a `Sub` badge, a minimal payload with only text
mapping with safe empty defaults, malformed input rejected without throwing,
and a live mock-WebSocket round trip proving `StreamerBotEventStream` now
subscribes to `Twitch.ChatMessage` alongside `General.Custom` and routes a
real frame through the mapper end to end. Both self-test suites pass and both
projects build with zero warnings in Debug and Release. None of this proves
the window is visible, legible, correctly placed, scales smoothly, reattaches
after a sleep/wake cycle, or that the field-name mapping matches what a real
Streamer.bot instance actually sends — only the manual steps below do that.

### A real bug the throttle self-test caught

`ChatRepaintThrottle` originally seeded its "last painted" timestamp with
`long.MinValue` so the very first check would always be owed. The very first
burst test failed instead: `nowMs - long.MinValue` overflows a signed 64-bit
integer for any `nowMs >= 0`, wrapping around to a large negative number that
failed the elapsed-time comparison, so the first repaint after construction
was silently skipped. Fixed by using a nullable timestamp instead of a
sentinel value, which cannot overflow. This was caught entirely by the
self-test before ever reaching a headset.

### Preparation

SteamVR running, both Vive controllers on and tracked, SteamVR2Bot running
with at least one saved shortcut, Streamer.bot connected to a live Twitch
channel. In **Settings**, turn on **Listen for Streamer.bot chat and
events** and **Show chat messages on your wrist**. No Streamer.bot action is
needed for chat itself now — typing in Twitch chat should be enough. The
`{"target":"chat",...}` broadcast route (e.g. via a hand-written
`CPH.WebsocketBroadcastJson` action) still works unchanged as an alternative
or supplement, useful for testing specific field values on demand.

| # | Step | Expected | Result |
|---|---|---|---|
| 0 | Type a message in Twitch chat, then check SVR Bridge's activity/debug log for the `streamerbot.event` line it produces | A chat payload is logged as received, confirming the direct subscription reached the app; separately, if the debug-level payload line is visible, compare it against the two shapes in the table above to record which one this Streamer.bot build actually sends | **PASS** — confirmed the newer top-level-`user` shape: a photo of the live wrist window showed a correctly-coloured, correctly-badged (`Broadcaster`) line for the channel owner's own messages, which only the newer shape's `user.color`/`user.badges` could have produced |
| 1 | Turn on both settings above, look at your left controller before anyone chats | A small, faint panel sits just above and behind the controller, tipped towards you like a watch face | **PASS** — user-confirmed |
| 2 | Type a chat message on Twitch, without looking at the window | The message is queued in the ring buffer, but the window stays small and faint — it does not pop open | **PASS** — user-confirmed |
| 3 | Now look directly at the window | It grows and brightens smoothly to a comfortably readable size and opacity, with the message legible | **PASS** — user-confirmed |
| 4 | Look away | It shrinks and fades back to small and faint, smoothly, not instantly | **PASS** — user-confirmed |
| 5 | Move your gaze slowly across the enter/exit boundary, back and forth, several times | No flicker or rapid size/opacity oscillation at any point — the hysteresis gap should be felt as a small dead zone, not a hard edge | **PASS** — user-confirmed |
| 6 | Have a chatter who has set a custom Twitch chat colour send a message | Their username renders in that colour — **not** orange/brown, which would mean the BGRA↔RGBA swap is inverted, and **not** the default blue-gray, which would mean the colour failed to map | **PASS** — the channel owner's messages rendered in their real Twitch colour (red), correctly, not swapped and not defaulted |
| 6b | Have a chatter who has *never* set a Twitch chat colour send a message | Twitch sends no colour for them at all (a real platform limitation, not a bug); the username should render in the app's own sensible default rather than every such chatter looking identical to a colour-set chatter | **PASS** — user-confirmed |
| 7 | A message from a chatter with an empty/unusual display name, if one can be produced, or a malformed broadcast via the relay route | It renders as "(no name)" in a sensible default colour rather than a blank or broken line, or the one bad message is dropped without taking the feed down | **PASS** — user-confirmed |
| 8 | Send a message containing a long unbroken string (a long URL with no spaces) | It wraps onto multiple lines within the panel rather than running off the edge or being clipped | **PASS** — user-confirmed |
| 9 | A rapid burst of 10+ chat messages within a second | The window does not stall, freeze, or visibly drop frames; all messages that fit the ring buffer appear, newest at the bottom | **PASS** — user-confirmed |
| 10 | More than 40 messages total over the session | Only the most recent ~40 remain visible; the window does not grow without bound or slow down | not run |
| 11 | Put the left controller down until it sleeps, then wake it | The window reattaches on its own, following the Phase 1 device re-resolution behaviour; log shows a new "following left controller device N" line | **PASS** — user-confirmed |
| 12 | Turn the left controller off entirely, then back on | Log shows "waiting for a left controller" while off, with no crash or spam; the window reattaches once it is back on | **PASS** — user-confirmed |
| 13 | Trigger a notification (Phase 2) while chat is active and visible | Both the head-anchored notification and the wrist chat window work correctly at the same time; neither interferes with the other | **PASS** — user-confirmed |
| 14 | Open the SteamVR dashboard → SteamVR2Bot | Dashboard still opens and renders exactly as before; the chat window stays visible alongside it | **PASS** — user-confirmed |
| 15 | Close the dashboard and fire an existing shortcut | Streamer.bot action fires exactly once, no duplicates — the product contract, unaffected by the new overlay | **PASS** — user triggered twice; SVR Bridge's own log showed exactly two `streamerbot.action_confirmed` lines, one per press, no duplicates or extra requests on this app's side. Streamer.bot itself reported 22 executions of the target action, traced to that action having other triggers unrelated to this shortcut — not a defect in SVR Bridge's delivery path. See the session record for the full evidence trail. |
| 16 | A moderator, VIP and subscriber each send a message (or one chatter who holds one of these) | The correct badge label appears for each, derived from Twitch's own badge names — confirms `TwitchChatMessageMapper`'s badge derivation against real data, not just documentation | **PASS** — user-confirmed |
| 17 | Send a message containing a real, common emote (Twitch, BTTV, FFZ or 7TV) with a keep-alive trigger enabled in Streamer.bot (see the limitation below) | The emote renders as its real image inline with the text, not as plain or styled text | **PASS** — user-confirmed live |
| 17b | Send a message with an emote immediately (within the first second or two) after connecting - before the catalog fetch and the first image download can possibly have finished | The emote renders as styled text (italic, distinct colour) at first, then switches to the real image on a later repaint once it finishes downloading - proves the fallback and the version-combining repaint both work, not just the steady-state case | not run |
| 17c | Send a message with an emote unlikely to be in Streamer.bot's global catalog (e.g. a small channel's own custom subscriber emote you don't have access to, or a made-up word coincidentally flagged as an emote) | It renders as styled text indefinitely rather than a broken image or a crash - the catalog only has what `TwitchGetEmotes` returned, and a miss is expected, not an error | **PASS** — user-confirmed |
| 18 | Send a message as a moderator, VIP, subscriber or the broadcaster | The real badge icon renders alone, matching Twitch's own convention, not the bracketed text label and not both together | **PASS** — user-confirmed |
| 18b | Send a message from a chatter with a role but no badge image available yet (or with images disabled by killing network access briefly, if that's easy to simulate) | The bracketed text label (`[Mod]`, `[VIP]`, `[Sub]`, `[Broadcaster]`) appears instead - never a blank space where the badge should be | **PASS** — user-confirmed |
| 18c | Send a message from a chatter with more than one badge - e.g. yourself as broadcaster with a Prime/Premium badge, or anyone with a channel-specific custom badge | All of that chatter's badges render side by side, each as its own real image (or bracketed text if not yet cached) - this is the exact case that was silently dropping Prime and channel-specific badges before the fix above | **PASS** — user-confirmed |

### A pre-existing, unrelated relay action caused duplicate messages

During this manual pass, every chat message briefly appeared twice: once correctly
coloured and badged `Broadcaster` (the new direct subscription, working as
intended), and once in a default colour badged literally `Twitch` (matching
neither `TwitchChatMessageMapper`'s output nor the hand-written relay action
from earlier in this phase). The user located and disabled a separate,
previously-existing Streamer.bot action that was independently broadcasting
`General.Custom` chat payloads with a hardcoded `badge: "Twitch"` and no real
colour - unrelated to anything built in this phase, but very likely the actual
cause of the original "test users both came back blue" report that started
this investigation. Not a defect in SVR Bridge; recorded here because it
explains earlier confusing results and because anyone repeating this test
matrix should check for other active chat-relay actions first.

### Platform limitation found and confirmed: Streamer.bot needs a local trigger for chat to flow at all

After removing every relay action (both the mystery one above and the
hand-written one from earlier in this phase), chat stopped reaching SVR
Bridge entirely - not dropped, not erroring, just silent. `svr-bridge-
20260729.jsonl` shows the exact shape of it:

| Time | What happened |
|---|---|
| 17:14:25 | Event feed connects; `streamerbot.events_connected` logged |
| 17:14:47–17:21:07 | 23 chat payloads logged (in duplicate pairs - the mystery action was still active) |
| 17:21:07 | Last relay action deleted around here |
| 17:21:07 → 17:53:43 | **Zero** `streamerbot.event` lines for 32 minutes, despite the user actively sending test messages ("BOP" and others) that were confirmed visible in Streamer.bot's own Chat panel the entire time. No `streamerbot.event_dropped`, no `streamerbot.events_unavailable`/reconnect message - the connection never reported a problem, it simply had nothing to forward. |
| ~17:53 | User added back a bare action - a `Twitch > Chat Message` trigger with zero sub-actions, doing nothing | 
| 17:53:43 | Payload #24 logged - the very next chat message, arriving the moment the trigger existed again |

Before concluding this was a genuine platform constraint rather than an SVR
Bridge bug, two things were checked directly against Streamer.bot's own
documentation rather than assumed:

- The WebSocket events guide (`/api/websocket/guide/events`) was checked for
  what conditions govern delivery after `Subscribe`. It states outright:
  *"Documentation Needed - This section is missing documentation."* Streamer.bot
  does not document this behaviour either way.
- The full WebSocket request reference (`/api/websocket/requests`) was
  checked for any request that could create or import a trigger/action
  remotely. There is none - `DoAction` and `ExecuteCodeTrigger` only run
  triggers that already exist. SVR Bridge has no way to set this up
  invisibly even in principle; the capability does not exist in Streamer.bot's
  API surface to call into.

**Conclusion:** Streamer.bot's own Twitch-chat pipeline appears to only be
active while at least one local trigger of that type exists and is enabled,
independent of whether any WebSocket client has subscribed to it. SVR Bridge's
direct `Twitch.ChatMessage` subscription (§B6 of the chat plan) still removes
all the platform-specific mapping work a relay action used to require -
colour, badges and emote names all arrive correctly with zero Streamer.bot
code once a trigger exists - but it cannot remove the need for that trigger to
exist at all. This is recorded as a known limitation in `README.md` rather
than worked around, because there is nothing on SVR Bridge's side left to
change: the constraint lives entirely inside Streamer.bot, the same category
as the SteamVR-dashboard-focus limitation documented in earlier phases.

### Frame-timing impact

Not measured. As with Phase 2, the design keeps the gaze animation on
`SetOverlayAlpha`/`SetOverlayWidthInMeters` alone — no texture work — and
throttles the actual repaint to ~10 Hz per §B2, but no capture of a running
game's frame times with chat active and receiving a burst has been taken.
Treat this as unverified rather than as "no impact confirmed" until a
frame-timing capture is run during an active VR game session with chat live.

## Phase 4 — anchors, control commands, and an in-app test harness — not yet run

### Scope

Three things, landed together per `PHASE4_PROMPT.md`:

- **An in-app chat test harness.** Five new tray developer-menu items under
  **Chat test harness (developer)** inject synthetic messages straight into
  the running worker's `ChatRingBuffer` through the same
  `BridgeEngine.ShowChatMessage` path a real Streamer.bot payload takes,
  bypassing the WebSocket entirely: a 12-message burst, a 45-message
  ring-buffer fill, a 300-character unbroken message, a three-badge message,
  and an unknown-emote message. Replaces the abandoned Streamer.bot
  `!svrtest` C# action, which never fired and could not be diagnosed from
  this app's own logs.
- **A unified overlay anchor model.** `OverlayAnchor` (`Core`) — Controller
  (with a hand) or Head — replaces the wrist transform hardcoded into
  `ChatOverlay` and the head transform hardcoded into `NotificationOverlay`.
  Both offsets are bit-for-bit the values Phase 1/2/3 already proved in the
  headset (guarded by `TestOverlayAnchorOffsetsMatchProvenTransforms`), so
  this is a refactor of *which surface can use which anchor*, not a change to
  either default placement. `OverlayAnchorTracker` folds the device-index
  re-resolution logic that used to be duplicated between the two overlays.
  Desktop **Settings** now has an anchor mode/hand combo under each of
  **Chat** and **Notifications**. A saved anchor change is baked into the
  OpenVR worker at its next spawn (the same way an address or password
  change already applies), not pushed live to a running worker.
- **Control commands from Streamer.bot.** A `target: "control"` payload with
  `command: "show" | "hide" | "clear" | "anchor" | "reset"` (plus `surface:
  "chat" | "notifications"`, defaulting to chat, and `mode`/`hand` for
  `anchor`) now does something. Per §B3, these are **transient overrides
  only** — `SurfaceOverrideState` holds them in memory over the saved
  default and never touches `settings.json`; `reset` drops the override and
  `show/hide` toggle a hide flag independent of it. World-lock (an anchor
  with no tracked device, placed once in the room) remains a deliberate
  deferral — see `CHAT_AND_NOTIFICATIONS_PLAN.md` §"World-lock is deferred".

### What is already covered without a headset

All automated, added this phase: `TestOverlayAnchorOffsetsMatchProvenTransforms`
(the controller/head offsets are bit-for-bit the wrist/head transforms proven
in Phases 1–3), `TestSurfaceOverrideStateAppliesAndResetsControlCommands`
(each command applies the expected override, `reset` restores the saved
default, and an unrecognised command or a malformed `anchor` command changes
nothing), `TestChatDeveloperInjectorProducesExpectedMessages` (the burst,
fill, long-message, multi-badge and unknown-emote injectors each produce the
expected shape, and filling a real `ChatRingBuffer` past its cap evicts the
oldest messages first), `TestChatCommandJsonRoundTripPreservesBadgesAndEmotes`
(added after live testing raised, then ruled out, a suspected badge-dropping
bug: proves a chat payload's badges and emote names survive the exact
cross-process JSON options `OpenVrWorkerSession` and `OpenVrWorker` use on
either side of the worker command channel - the one path that had only ever
been exercised live, never by an automated test, because `ChatBadge` is a
positional record and every other field on the payload is a plain
init-only property), and payload-parsing coverage in
`TestStreamerBotEventPayload` for the `surface`/`mode`/`hand` fields
(including the chat default and malformed/unrecognised values). Both
self-test suites pass and both projects build with zero warnings in Debug
and Release. None of this proves a control command actually moves a panel in
the headset, that a desktop anchor change survives a worker restart with the
panel in the right place, or that the developer injector's messages are
legible and correctly shaped once rendered — only the manual steps below do
that.

### Preparation

SteamVR running, both Vive controllers on and tracked, SteamVR2Bot running
with at least one saved shortcut. In **Settings**, turn on **Listen for
Streamer.bot chat and events**, **Show chat messages on your wrist**, and
**Show notification broadcasts in the headset**. A control command can be
sent the same way a notification was in Phase 2 — a C# sub-action calling
`CPH.WebsocketBroadcastJson` with a body such as
`{"target":"control","command":"anchor","surface":"chat","mode":"head"}`.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Tray menu → **Chat test harness (developer)** → **Inject a chat burst** | 12 messages appear in the chat window in order; no stall or dropped frame | not run |
| 2 | **Fill the ring buffer (45 messages)** | Only the most recent ~40 remain visible; the window does not grow without bound | not run |
| 3 | **Inject a long unbroken message** | Wraps onto multiple lines rather than overflowing or clipping | not run |
| 4 | **Inject a multi-badge message** | All three badges render side by side (as bracketed text, since none have a real image URL) | **PASS** — user-confirmed `[Moderator] [Prime] [glhf-pledge]` bracket text appeared next to the username |
| 5 | **Inject an unknown-emote message** | Renders as styled text, not a broken image or a crash | **PASS** — user-confirmed italic styled text, no broken image or crash |
| 6 | Desktop Settings → set **Chat** anchor to **Headset**, save, put the headset on | Chat window now sits in front of you rather than at the left controller, and grows/dims per gaze exactly as the wrist placement did | **PASS** — user-confirmed head-follow works |
| 7 | Set **Chat** anchor back to **Controller / Right hand**, save | Chat window now follows the right controller instead of the left | **PASS** — user-confirmed controller-follow works after switching back; the specific right-hand case was not separately isolated |
| 8 | Set **Notifications** anchor to **Controller / Left hand**, save, trigger a notification | Notification now appears at the left controller instead of in front of your face | not run |
| 9 | Restore both anchors to their defaults (Chat: Controller/Left, Notifications: Head), save | Both surfaces return to exactly the Phase 1–3 proven placement — confirms the desktop setting round-trips through a worker restart, not just forward | not run |
| 10 | Send `{"target":"control","command":"anchor","surface":"chat","mode":"head"}` while chat is on the controller | Chat window moves to the head anchor immediately, without a settings save or app restart | **PASS** — user-confirmed ("Payload tests all worked") |
| 11 | Send `{"target":"control","command":"reset","surface":"chat"}` | Chat window returns to whatever the saved desktop setting currently is | **PASS** — user-confirmed |
| 12 | Send `{"target":"control","command":"hide","surface":"chat"}` | Chat window disappears entirely, even when gazed at | **PASS** — user-confirmed |
| 13 | Send `{"target":"control","command":"show","surface":"chat"}` | Chat window reappears and resumes normal gaze-scale behaviour | **PASS** — user-confirmed |
| 14 | Send a few chat messages, then `{"target":"control","command":"clear","surface":"chat"}` | Chat window empties immediately | **PASS** — user-confirmed |
| 15 | Send `{"target":"control","command":"hide","surface":"notifications"}`, then trigger a notification | No panel appears | **PASS** — user-confirmed |
| 16 | Send `{"target":"control","command":"show","surface":"notifications"}`, then trigger a notification | Panel appears normally | **PASS** — user-confirmed |
| 17 | Send `{"target":"control","command":"bogus"}` | Nothing visible changes; the activity/debug log shows it was ignored as unrecognised, not a crash | **PASS** — user-confirmed |
| 18 | Send `{"target":"control","command":"anchor","surface":"chat"}` (no `mode`) | Nothing visible changes; the log shows it was ignored as malformed | **PASS** — user-confirmed |
| 19 | Open the SteamVR dashboard → SteamVR2Bot, click through the shortcut list and action picker | Renders and responds exactly as before, unaffected by the anchor/control changes | not run |
| 20 | Close the dashboard and fire an existing shortcut | Streamer.bot action fires exactly once, no duplicates — the product contract, unaffected by this phase | not run |
| 21 | Restart SteamVR2Bot entirely (not just the worker) | Any active control-command override from before the restart is gone; both surfaces come up at their saved desktop defaults | not run |

Rows 10–18 were confirmed in one combined pass (the user reported "Payload tests all worked" after running each Streamer.bot action in turn), not as individually itemised results with a raw log excerpt per row — consistent with the user-reported convention used elsewhere in this file. Rows 4–7 were confirmed in a separate, more detailed exchange; the exact wording each time is recorded above.

### Two real bugs caught during this pass

**Emote catalog stopped resolving after an anchor-triggered worker restart — root-caused and fixed.** Switching the chat anchor from desktop Settings restarts the OpenVR worker (a fresh, empty `ChatImageCache`), but does not restart the Streamer.bot event stream, since anchor mode is not part of `EventStreamSettings`. `TrayApplicationContext.EnsureEmoteCatalogAsync`'s fetch-once guard was keyed only to the event-stream instance, so it silently skipped re-delivering an already-fetched catalog to the new worker - emote and badge images stopped resolving (falling back to styled text) for the rest of the session after any anchor change, notification/chat toggle, or other settings save that restarts the runtime without restarting the event stream. This is a latent bug in the Phase 3 delivery-race fix, exposed by Phase 4's anchor setting giving an easy way to trigger a mid-session worker restart. Fixed by caching the fetched catalog value itself (not just a "fetched" boolean) so a new worker receives it via the existing bounded retry loop without a redundant Streamer.bot request. No dedicated automated test was added (`TrayApplicationContext` is not currently exercised by the self-test suites); re-verify with a fresh anchor switch, notification toggle, or chat toggle followed by chat activity.

**Chat/dashboard blink on texture update — resolved as a confirmed pre-existing platform behaviour, not a bug.** Confirmed live: the chat window blinks on every repaint (`SetOverlayRaw`) and the dashboard blinks on every Settings-page interaction (`SetOverlayFromFile`). A first fix attempt - dropping chat's alpha to zero right at the texture swap, on the theory the swap itself was showing through - was tried and confirmed *not* to fix it: it just replaced the blink with a more visible fade-to-invisible-and-back, and was reverted. The decisive test: rapidly clicking the shortcut wizard's **Tolerance** slider - unmodified by any phase of this app, live since the very first release - reproduces the *identical* blink. Timing evidence rules out application-side slowness as the cause: every dashboard page update logged across this session (Settings, List, GestureType, RecordInput, Review, and Tolerance itself) measured 15-32 ms, with no correlation between duration and which page was showing. Conclusion: this is inherent SteamVR/OpenVR compositor behaviour when any overlay texture is replaced, present on both of this app's texture-update paths despite their different costs, and predates every phase of this project - it was simply never stress-tested with rapid repeated clicking until Phase 4b's sliders invited it. Documented as a known limitation in `README.md` rather than chased further; the temporary timing diagnostics added to `ChatOverlay.RepaintIfOwed` and `VrDashboardController.ShowPage` have been removed, their purpose served.

### Frame-timing impact

Not measured, for the same reason as Phases 2 and 3: no capture of a running
game's frame times with the anchor tracker and control-command handling
active has been taken.

## Phase 4b — VR settings tab, with appearance and gaze settings — not yet run

### Scope

Landed per the approved plan (`smooth-finding-creek.md`): a **Settings** tab
in the SteamVR dashboard, reached by peer navigation alongside the existing
**Shortcuts** wizard entry point, plus five new settings the wizard never
had - `ChatOpacity`, `ChatSizeScale`, `GazeSensitivity`,
`NotificationOpacity`, `NotificationSizeScale` - since `PHASE4B_PROMPT.md`'s
own scope (anchor mode/hand, panel size, opacity, on/off, gaze sensitivity)
assumed settings Phase 4 had not actually shipped.

- **Navigation.** `VrDashboardController` gained a `DashboardPage.Settings`
  page sitting as a peer to `List` (the wizard's entry point), not inside the
  wizard's page stack. The tab strip is drawn only on `List` and `Settings`;
  the five wizard sub-pages (`GestureType`, `Tolerance`, `ActionPicker`,
  `RecordInput`, `Review`) are **completely untouched** - zero lines changed
  in their render or click-handling code. `List` itself was reflowed (title/
  subtitle/rows shifted down, visible row count 6→5) to make room for the tab
  strip at the top; row math in `VrDashboardController.HandleListClick` and
  `VrDashboardRenderer.Render` was updated together via shared
  `VrDashboardLayout` constants, not independently.
- **Controls.** Toggles for on/off, segmented two/three-way buttons for
  anchor mode, anchor hand (disabled when Headset is selected, not hidden)
  and gaze sensitivity, and sliders for opacity/size that reuse the
  tolerance-picker's exact shape: click-to-position, a hit region far taller
  than the visible track, snapped to 5% steps.
- **GDI+, deliberately.** `VrDashboardRenderer`/`VrDashboardLayout`/
  `VrDashboardController` stay GDI+ for the new Settings page, consistent
  with the rest of the proven, hardware-tested dashboard, while chat and
  notifications remain WPF. Two renderers is a known, deliberate state, not
  an accident - migrating the dashboard to WPF is possible later cleanup, not
  a current need.
- **Live-apply, no restart.** `VrDashboardController` runs inside the OpenVR
  worker process already, so a settings-page edit applies to the live
  `ChatOverlay`/`NotificationOverlay` immediately, in the same process and
  thread - no IPC round trip needed for the *effect*. The *persistence* back
  to `settings.json` reuses the exact pattern already proven for VR-created
  shortcuts (`OpenVrWorkerMessage` → `OpenVrWorkerSession` drain queue →
  `BridgeEngine.VrSettingsChanged` → `TrayApplicationContext.
  SaveVrSettingsChange`), deliberately **without** calling
  `RestartRuntimeAsync` - restarting would tear down the very dashboard the
  wearer is looking at.
- **Override interaction (§B5).** An anchor edited from the VR settings page
  calls `SurfaceOverrideState.SetSavedDefaultAnchor`, which sets the new
  saved default *and* clears any active Streamer.bot override in the same
  call - an explicit user edit wins rather than silently doing nothing while
  an override is in effect. Opacity, size and gaze sensitivity have no
  Streamer.bot override mechanism at all (Phase 4's control commands only
  ever covered anchor/show/hide/clear), so there is no override to interact
  with for those three.

### What is already covered without a headset

All automated, added this phase: `TestSettingsPageLayoutRectangles` (every
tab/toggle/segmented/slider rectangle is declared once in
`VrDashboardLayout`, hit-tests to the control it is drawn as, does not
overlap its neighbours, and stays on the canvas and clear of the tab strip -
this caught a real bug: the two tabs were drawn exactly adjacent with zero
gap, unlike every other button row's convention, now fixed with the same
20px gap the rest of the dashboard uses), an extended
`TestSurfaceOverrideStateAppliesAndResetsControlCommands` (a VR-style
`SetSavedDefaultAnchor` call while a Streamer.bot override is active takes
effect immediately and clears the override, and a later Streamer.bot
`reset` returns to the *new* default, not the original one), and extended
settings-migration coverage (the five new fields round-trip through
`UserSettingsStore`, and a settings file written before Phase 4b defaults to
exactly today's hardcoded appearance - 0.95 opacity, 1.0 size scale, Normal
gaze sensitivity). All pre-existing self-tests continue to pass unmodified,
which is the strongest automated evidence available that the wizard itself
was not disturbed. Both self-test suites pass and both projects build with
zero warnings in Debug and Release; `dotnet format --verify-no-changes`
passes for all three projects (a pre-existing, unrelated whitespace-only
formatting drift in `OpenVrInput.cs` - a file untouched by any phase in this
session - was found and fixed mechanically while verifying this). None of
this proves the Settings tab actually renders correctly in the headset, that
a laser click lands on the control it visually appears to, or that the
wizard's *behaviour* (not just its unchanged source code) still matches -
only the manual steps below do that.

### Preparation

SteamVR running, both Vive controllers on and tracked, SteamVR2Bot running
with at least one saved shortcut and the chat/notification settings from
Phase 4 already exercised. Open the SteamVR dashboard → SteamVR2Bot.

| # | Step | Expected | Result |
|---|---|---|---|
| 1 | Look at the **Shortcuts** and **Settings** tabs at the top of the List page | Both render clearly, current tab highlighted; clicking **Settings** switches pages | not run |
| 2 | On the List page, confirm the shortcut list and "Create a new shortcut" bar | Renders and behaves exactly as before (allowing for one fewer visible row before "+N more on the desktop") | not run |
| 3 | Click through the entire wizard: create a shortcut, edit it, delete it | Every page (gesture type, tolerance, record input, action picker, review) behaves exactly as before Phase 4b, with no visual or functional change | not run |
| 4 | On the Settings page, toggle **Chat** off, then back on | Chat window disappears immediately; turning it back on makes it reappear immediately, without leaving the Settings page or a worker restart | not run |
| 5 | Set the Chat anchor mode to **Headset**, then back to **Controller** | Chat window moves immediately each time, matching the desktop's anchor behaviour | not run |
| 6 | With Chat anchor on **Controller**, switch the hand segmented control between **Left**/**Right** | Chat window follows the chosen hand immediately; the hand control is greyed out and non-interactive when **Headset** is selected | not run |
| 7 | Click along the Chat **Opacity** slider at several points | Chat window's gazed-at brightness changes immediately and visibly, matching where you clicked | not run |
| 8 | Click along the Chat **Size** slider at several points | Chat window's size changes immediately and visibly | not run |
| 9 | Cycle the **Gaze sensitivity** control through Relaxed/Normal/Tight | Chat window's gaze-scale trigger angle noticeably widens/narrows - Relaxed grows the window even when not looked at directly, Tight requires looking more directly at it | not run |
| 10 | Repeat steps 4-8 for **Notifications** (toggle, anchor mode, anchor hand, opacity, size), triggering a test notification after each change | Each change is visible on the next notification; toggling off suppresses notifications entirely, toggling back on resumes them | not run |
| 11 | While a Streamer.bot anchor override is active on Chat (send `{"target":"control","command":"anchor","surface":"chat","mode":"head"}`), change the Chat anchor from the VR settings page to **Controller** | The VR edit wins immediately; sending `{"target":"control","command":"reset","surface":"chat"}` afterward returns to **Controller**, not back to Headset | not run |
| 12 | Close the SteamVR dashboard, then reopen it and go to Settings | Every value shown matches what was last set, including anything changed in step 11 | not run |
| 13 | Change a value on the Settings page, then check the desktop app's Settings tab (no restart) | The desktop control shows the new value | not run |
| 14 | Restart SteamVR2Bot entirely | All Phase 4b settings survive - both anchor mode/hand, opacity, size and gaze sensitivity for both surfaces | not run |
| 15 | Close the dashboard and fire an existing shortcut | Streamer.bot action fires exactly once, no duplicates - the product contract, unaffected by this phase | not run |

### Frame-timing impact

Not measured, for the same reason as Phases 2-4: no capture of a running
game's frame times with the Settings page open and being interacted with has
been taken.
