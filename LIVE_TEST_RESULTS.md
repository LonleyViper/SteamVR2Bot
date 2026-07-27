# SVR Bridge POC — live test results

Date: 2026-07-27  
Machine: Windows gaming PC  
Status: **complete — direct SteamVR action architecture is viable**

## Scope and secret handling

The supplied continuation handoff was used because `HANDOFF.md` is not present
in this repository. The proof retains the requested architecture:

```text
SteamVR logical actions -> SVR Bridge chord detector
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
.\artifacts\publish\SvrBridge.exe --self-test
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
.\artifacts\publish\SvrBridge.exe --simulate
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
