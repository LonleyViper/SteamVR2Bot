# Focused-dashboard input probe — build, run, and decide

Branch: `codex/shortcut-manager`. This is step 1 of the focused-dashboard
investigation: a **read-only** probe. Nothing about input delivery changed, so
a run that reproduces the existing 2026-07-27 behaviour exactly is the expected
outcome. The probe's job is to tell us *why*.

## What was added

| File | Change |
|---|---|
| `src/SvrBridge.Core/InputProbe.cs` | New. Transition-only logger plus a 1 Hz summary. No OpenVR dependency, so it is unit-tested. |
| `src/SvrBridge.Core/OpenVrInput.cs` | Feeds the probe. Adds `SetInputProbeEnabled`, caches the last `ControllerSetup` for friendly naming, decodes overlay button events. |
| `src/SvrBridge.Tray/VrDashboardController.cs` | Enables the probe only while the page is `RecordInput`. |
| `src/SvrBridge/SelfTests.cs` | `TestInputProbe` covers transition-only behaviour, event decoding, throttling, and the disabled state. |

### Deliberately not done

- **No new IVRSystem vtable offsets.** `IVRSystem.IsInputAvailable` is not
  wired. The master `openvr.h` could not be retrieved in full, so the index for
  `IsInputAvailable` is unverified, and a wrong function pointer can take
  SteamVR down. Wire it only after confirming the index against a complete
  header. Everything below works without it.
- **No action-set priority change.** Still `0x01000000`. Path 2 is a separate,
  later experiment.
- **No new action set, no worker changes, no delivery changes.**

## Constraints preserved

`BridgeEngine.UpdateShortcuts` hot updates, the child OpenVR worker boundary,
release latching, one request/one acknowledgement, stable Streamer.bot action
IDs, `ChordMode` numeric ordering, Vive-first support, credential protection and
log redaction are all untouched. The uncommitted working tree was extended, not
reset.

## Build and validate

```powershell
$svrDotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'

& $svrDotnet build src\SvrBridge.Tray\SvrBridge.Tray.csproj --no-restore
& $svrDotnet build src\SvrBridge\SvrBridge.csproj --no-restore
& $svrDotnet run --project src\SvrBridge\SvrBridge.csproj --no-build -- --self-test
& $svrDotnet run --project src\SvrBridge.Tray\SvrBridge.Tray.csproj --no-build -- --self-test
& $svrDotnet format src\SvrBridge.Tray\SvrBridge.Tray.csproj --no-restore --verify-no-changes
& $svrDotnet format src\SvrBridge\SvrBridge.csproj --no-restore --verify-no-changes
```

Then publish through `scripts\Publish-Poc.ps1` and run both packaged self-tests.

Before replacing the packaged executable, identify processes by exact
`ExecutablePath`. Stop the OpenVR worker before the tray parent. Never kill
broadly by process name.

## Headset procedure

Open the recorder (`RecordInput`). The probe turns itself on there and off on
every other page, so the log is scoped automatically. Press the **same button**
— Right Grip is a good choice, it is what the 17:05 trace captured — in each of
three states:

1. SteamVR dashboard open **and SteamVR2Bot focused**.
2. SteamVR dashboard open, **another dashboard tab focused**.
3. SteamVR dashboard **closed**.

Then correlate against `%LOCALAPPDATA%\SteamVR2Bot\Logs`.

## Log lines to look for

```
input probe: on (recorder page).
input probe dashboard: visible=1 active=1.
input probe action right_grip: err=0 active=1 state=1 changed=1 origin=0x....
input probe legacy mask: L=0x0 R=0x4.
input probe overlay ButtonPress: device=3 button=2 (Right Grip).
input probe overlay event 300: first seen this session.
input probe: dashboard visible=1 active=1 | actions 8/9 active, pressed none | legacy L=0x0 R=0x0 | overlay events 412.
```

The 1 Hz summary is deliberate: an empty log then means "nothing arrived",
not "the probe stopped running". `err` is the raw `EVRInputError` value —
`0` success, `13` `NoData`.

## Decision tree

Read the **dashboard-focused** run first.

| Observation while focused | Reading | Next |
|---|---|---|
| `overlay ButtonPress` lines appear with correct device and button | SteamVR does deliver raw button events to the overlay queue. | **Best case.** Promote path 1: feed these events into `ControllerInputs` alongside the action path. Must not treat the laser trigger-click that operates the dashboard as a recorded input — gate on the click already consumed by `DashboardPointerTracker`. |
| No `ButtonPress`, but `action ... active=1 state=1` appears | Actions do reach us; the recorder is dropping the edge elsewhere. | Bug in `CaptureRecordedInput`/`_recordingArmed`, not a platform limit. Fix in the controller. |
| `action ... active=1` but `state` never goes to 1 | Bindings resolve, SteamVR withholds state under dashboard focus. | Go to path 2: raise overlay-global priority to `0x01FFFFFF`. Check whether SteamVR's **Experimental overlay input overrides** developer setting is on — report before changing it, and confirm the change does not steal input from a running VR game outside recording mode. |
| `action ... active=0` while focused, `active=1` when closed | The action set is deactivated under dashboard focus. | Path 2 first, then path 3 (a `RecordInput`-only action set that releases ownership immediately after capture or cancel, without rebooting worker or dashboard). |
| `err=13` (`NoData`) only while focused | SteamVR is refusing to report. | Path 2, then path 4 (separate background OpenVR client) as an experiment only. |
| Nothing changes in any of the three states, including closed | The probe is not reaching the recorder page. | Check that `input probe: on (recorder page).` was logged at all. |
| Paths 1–4 all fail | Platform constraint. | Document it. Keep the accepted fallback: open recorder → press System once → press desired input → SteamVR2Bot reopens on Review. **Do not** dress up the manual left/right picker as live recording. |

Also compare the **legacy mask**. If `legacy L/R` stays `0x0` in all three
states, the deprecated `GetControllerState` path is dead under the new input
system and can be dropped from `Poll` rather than debugged.

## Acceptance criteria (unchanged)

A fix is complete only when, with the dashboard open and focused: Menu, Grip,
Trigger, and Trackpad presses produce correct friendly left/right inputs, the
exact input appears on Review, the click used to operate the dashboard is not
recorded, the flow repeats without dashboard disappearance or worker restart,
saving applies immediately, and a harmless Streamer.bot action fires once per
gesture with no miss or duplicate. Automated checks alone do not close this.
