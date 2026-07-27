# SVR Bridge — next phase plan

## Goal

Turn the validated proof of concept into a small Windows application that can
run every day without a console window or manual JSON editing.

The proven architecture remains:

```text
SteamVR logical actions
    -> local chord detector
    -> Streamer.bot WebSocket DoAction
```

SISR, keyboard emulation, OpenVR2Key, and a rendered VR overlay are not part of
this phase.

## Proven baseline

- SteamVR shell: 20/20 gestures, 20 acknowledgements, 0 duplicates.
- GERONIMO with the dashboard closed: 20/20 gestures, 20 acknowledgements,
  0 duplicates.
- Vive default gesture: hold left grip, press right index trigger.
- Streamer.bot action selection supports a GUID.
- The published executable, authenticated mock round trip, and release build
  all pass.

`LIVE_TEST_RESULTS.md` is the evidence record for this checkpoint.

## Phase 1 — daily-use tray application

### 1. Separate the proven engine from the user interface

- Move OpenVR input, chord detection, configuration, and Streamer.bot delivery
  into a reusable core project.
- Keep the current console entry point as a diagnostic tool.
- Add automated tests around configuration loading, reconnect behavior, and
  input-to-action routing.

Exit criteria:

- Existing self-tests still pass.
- The console diagnostic produces the same live behavior as this baseline.

### 2. Add a tray-first Windows interface

- Use a small native Windows tray application.
- Show one clear status: SteamVR, controller input, and Streamer.bot connection.
- Provide Start, Stop, Test Action, Settings, Open Logs, and Exit commands.
- Keep the main window optional; normal operation should stay in the tray.

Exit criteria:

- A nontechnical user can start and stop the bridge without a terminal.
- A failed connection is visible and actionable.
- Closing the settings window does not stop the bridge.

### 3. Replace manual JSON editing with settings

Settings should cover:

- Streamer.bot WebSocket address.
- Optional authentication stored with Windows-protected storage, never in a
  tracked or plain-text project file.
- Streamer.bot action selection by discovered action name and stored GUID.
- Controller gesture, cooldown, and test mode.
- Start automatically with SteamVR.

Exit criteria:

- First-run setup can be completed from the application.
- The user can test the selected action before saving.
- Secrets never appear in logs or exported diagnostics.

### 4. Make runtime behavior production-safe

- Maintain the current one-request/one-acknowledgement contract.
- Replace the single retry with bounded reconnect and backoff for long-running
  use.
- Keep unique request IDs and structured local logs.
- Add explicit states for disconnected, connecting, ready, SteamVR unavailable,
  and binding unavailable.
- Preserve release latching so a held control cannot repeat accidentally.

Exit criteria:

- Streamer.bot and SteamVR can each be restarted without restarting the tray
  application.
- A temporary network loss produces no duplicate action.
- Logs contain enough information to distinguish input, chord, and delivery
  failures without exposing credentials.

### 5. Automate SteamVR setup

- Register the application manifest from the tray application's first-run flow.
- Ship the validated Vive binding.
- Detect the active controller type and explain when no binding is available.
- Add startup registration only after the user enables it.

Exit criteria:

- A clean installation appears in SteamVR bindings without terminal commands.
- The bridge starts with SteamVR when enabled and can be disabled cleanly.

## Phase 2 — multiple commands and feedback

After the daily-use tray application passes:

- Support multiple named gestures, each mapped to a Streamer.bot action GUID.
- Add import/export that excludes credentials.
- Add optional controller haptic acknowledgement after Streamer.bot confirms
  execution.
- Add default bindings for additional controllers only when the hardware is
  available for live testing.

Required test matrix for every supported controller:

| Environment | Attempts | Required result |
|---|---:|---|
| SteamVR shell | 20 | 20 actions, 0 misses, 0 duplicates |
| VR game 1 | 20 | 20 actions, 0 misses, 0 duplicates |
| VR game 2, when available | 20 | 20 actions, 0 misses, 0 duplicates |
| Streamer.bot restart | 5 | reconnects, 5 actions, 0 duplicates |

## Phase 3 — packaging

- Produce a signed, versioned Windows installer.
- Include install, update, repair, and uninstall behavior.
- Remove SteamVR registration and autostart entries during uninstall.
- Add a diagnostics export containing versions, configuration without secrets,
  and recent logs.

Exit criteria:

- Install and uninstall require no manual file cleanup.
- Upgrade preserves user settings and protected credentials.
- A release build can be reproduced from a clean checkout.

## Explicitly deferred

These are separate projects or later phases:

- Native VR chat overlay.
- OBS/browser overlay rendering.
- Cloud accounts or remote configuration.
- Keyboard emulation.
- SISR or virtual-controller integration.
- Automatic execution of destructive Streamer.bot actions.

## Recommended first implementation slice

Build Phase 1 steps 1–3 as one vertical slice:

1. Extract the proven engine.
2. Add the tray application and status model.
3. Add Streamer.bot connection/action settings.
4. Run the existing simulation test and the two 20-attempt live tests.

Do not add multiple gestures, haptics, an installer, or chat rendering until
that slice passes.

## Implementation checkpoint

Phase 1 steps 1–3 are now implemented on `codex/tray-app`:

- The proven OpenVR, chord, and Streamer.bot code is in `SvrBridge.Core`.
- The original console application remains available as a diagnostic tool.
- `SvrBridge.Tray` provides friendly status, Start, Stop, Test, SteamVR setup,
  recent activity, and a notification-area menu.
- Streamer.bot actions are discovered and shown by name; the stable action ID is
  stored behind the scenes.
- The optional password is protected for the current Windows user.
- Existing console self-tests and the protected-settings tray self-test pass.

The live regression status through the tray app is:

1. **Pass:** **Find actions** returned 248 enabled actions and matched the
   intended action by stable ID.
2. **Pass:** **Test Streamer.bot** produced one acknowledged action.
3. **Pass:** In-app SteamVR setup and **Save and Start** reached the ready state.
4. **Pass:** SteamVR shell physical run produced 20/20 actions with no duplicate.
5. **Pass:** GERONIMO dashboard-closed physical run produced 21/21 actions with
   no miss or duplicate.

The first implementation slice is accepted. Continue with Phase 1 step 4:
long-running reconnect/backoff, restart recovery, structured local logs, and
binding-unavailable detection.

## Runtime-hardening checkpoint

Implemented on `codex/runtime-hardening`:

- OpenVR polling runs in a disposable child worker so SteamVR can terminate its
  client during shutdown without terminating the persistent tray host.
- SteamVR sessions reconnect with bounded 1/2/5/10/30-second backoff while the
  tray app remains running.
- Streamer.bot connections receive three bounded attempts before a gesture is
  reported as unconfirmed.
- An action that may already have crossed the network is never blindly resent,
  preventing a reconnect from duplicating a command.
- Daily structured JSONL logs are kept for 14 days under the current Windows
  user's local app data, with credential-value redaction.
- The active controller family and both logical action bindings are inspected
  through public OpenVR interfaces.
- The app opens SteamVR's official per-controller binding page directly.
- Gesture behavior can be modifier-first or simultaneous.
- Vive remains the only shipped validated default; other controller families
  are detected and guided through custom binding setup.

Automated checks now cover delayed Streamer.bot availability, authenticated
delivery, protected settings, redacted structured logs, and the rule that an
unconfirmed request is not retried.

The tray also remained open across SteamVR and Streamer.bot restarts, then
started cleanly and delivered 18/18 controller commands without a duplicate.
This confirms post-restart startup, but the shortcut was stopped during the
restarts.

Remaining live gates:

1. Retest an actual SteamVR restart with the new child-worker build and confirm
   the tray remains open and returns to Ready.
2. Restart Streamer.bot, confirm the first uncertain command is not duplicated,
   then confirm the next command reconnects.
3. Repeat the shell and GERONIMO 20-attempt matrices after the recovery tests.
4. Before adding a packaged default for Index, Touch, WMR, Cosmos, or another
   family, run that hardware through the same matrix.

Future preset discovery can borrow the user-friendly pattern demonstrated by
[SteamInputDB](https://www.steaminputdb.com/): show only layouts relevant to
connected controllers and use recognizable names. SteamInputDB stores standard
Steam Input configurations, not SteamVR/OpenVR action bindings, so it is a
design reference rather than a binding source. Keep live OpenVR inspection and
SteamVR's binding UI as the authority. Do not reuse its AGPL implementation
without a deliberate licensing decision.

## Shortcut-manager checkpoint

Implemented on `codex/shortcut-manager`:

- Settings now hold several named shortcuts, and the old single-action settings
  migrate automatically to the first row.
- Every shortcut has its own enabled state, friendly controller gesture, and
  stable Streamer.bot action ID.
- Runtime routing maintains an independent chord detector per shortcut and
  invokes only the action attached to the gesture that fired.
- The desktop Shortcuts page supports add, record, edit, test, enable/disable,
  and remove.
- The SteamVR dashboard lists the saved gestures and actions. Its guided wizard
  begins with Single Button, Button Combo, Double Press, and Long Hold. Double
  Press and Long Hold expose a tolerance slider. The input screen groups every
  available physical input under Left and Right controller columns, so SteamVR
  dashboard focus cannot block setup. Opportunistic live capture remains
  available, and the selected names appear on a review screen before Save. The
  action browser opens from that review screen and retains controller scrolling
  plus large Previous/Next controls.
- Each saved row has direct pencil/edit and X/delete controls. Saves and deletes
  update the active shortcut detectors in place instead of rebooting the
  SteamVR worker or dashboard.
- Wizard pages replace the active overlay texture without repeatedly
  re-activating the SteamVR dashboard. Ready-state updates no longer recreate
  the wizard, and save/delete redraw only once. Page-render failures are logged
  and contained inside the worker instead of terminating it.
- Direct physical input selection is Vive-first. Other controller families retain
  the official SteamVR binding fallback until they pass the hardware matrix.
- Vive physical inputs are backed by explicit SteamVR actions rather than the
  deprecated legacy controller-state API. Startup selects the packaged complete
  Vive input map so old saved SteamVR bindings cannot silently disable the
  in-app button choices.
- The dashboard remains inside the disposable OpenVR worker, preserving the
  tray application's SteamVR-restart isolation.

Automated checks cover legacy migration, protected multi-shortcut persistence,
physical left/right button-mask matching, double-press and other chord behavior, authenticated
delivery, reconnect behavior, SteamVR worker recovery, and duplicate
prevention.

Live gates for this checkpoint:

1. Confirm the SteamVR dashboard pointer, group expansion, and controller
   scrolling in the headset.
2. Record a second Vive shortcut in VR and confirm it becomes active without a
   worker or dashboard restart.
3. Repeat the 20-attempt shell and GERONIMO matrices for both shortcuts.
4. Test each additional controller family before adding a named preset.

## Long-press and scroll stability correction

- Single physical inputs support one press, adjustable double press, or an
  adjustable long hold and fire only once until released.
- Button combinations record two named physical inputs and trigger when both
  are pressed together.
- Scroll bursts are limited to one page redraw per 500 ms.
- Dashboard renders rotate across image files so SteamVR never reads a PNG
  while the next redraw overwrites it.

## Always-on UX correction

- The app now registers itself with SteamVR and starts the shortcut runtime
  automatically whenever it is open.
- Its dashboard overlay is created automatically, so it remains available as a
  SteamVR dashboard tab without first pressing a desktop button.
- Desktop changes still rebuild connection state when necessary. In-VR shortcut
  saves and deletes update active detectors in place without closing the
  dashboard.
- Manual Save, Start, Stop, and start-on-open controls were removed.
- Dashboard clicks follow Valve's reference event pattern: the latest
  `VREvent_MouseMove` position is retained and used when
  `VREvent_MouseButtonDown` arrives.
