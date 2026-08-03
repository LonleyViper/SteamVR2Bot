# Claude Code prompt — fix: a fresh install never appears in SteamVR

Model: **Sonnet.** The diagnosis is complete and the fix is small. The care needed
is in not weakening delivery validation while relaxing startup validation.

Paste everything below the line.

---

## The bugs — three, independent

**Every new user is blocked.** A fresh install never gets a dashboard tile or
any in-VR UI, and restarting SteamVR does not help.

Three separate defects were found, each confirmed against the code:

1. **The runtime never starts without a shortcut**, so the only automatic route
   to a dashboard never runs. This is what the reported log shows.
2. **Registration is never retried** when SteamVR appears after app start.
3. **The documented repair script has a broken path** and has never worked.

Fix all three. They fail in different ways and none of them covers the others.

## Bug 1 — root cause of the reported log

`TrayApplicationContext.RestartRuntimeLockedAsync`:

```csharp
try { UserSettingsStore.Validate(_settings); }
catch (InvalidDataException exception) {
    OnStatusChanged(new BridgeStatus(BridgeState.Error, "Finish this setting", exception.Message));
    return;                                   // bridge never starts
}
_bridgeTask = _engine.RunAsync(...);          // never reached
```

`UserSettingsStore.Validate` throws `"Add at least one controller shortcut."`
when the shortcut list is empty. The bridge is what opens the OpenVR session and
creates the dashboard overlay, so with no shortcuts there is no session and no
dashboard.

**This is a chicken-and-egg trap.** The in-VR shortcut wizard exists so a user
can create their first shortcut without leaving VR — but it lives on the
dashboard that never appears because they have no shortcut. There is no way out
from inside VR.

### Why no other path saves them

`ShowDashboardAsync` *can* create its own session without the bridge, so this was
checked. It has three callers:

- `OpenVrDashboard()` — the manual **Open SteamVR dashboard** button.
- `RefreshStreamerBotActionsAsync(..., refreshDashboard: true)` — reached **only**
  from the manual **Refresh Streamer.bot actions** button.
- `status.State == BridgeState.Ready` — requires the bridge to be running.

`InitializeAsync` calls the action refresh with `refreshDashboard: false`. So on
an ordinary launch the only automatic route to a dashboard is `BridgeState.Ready`,
which Bug 1 blocks. The reported log contains no `openvr.dashboard` entry across
nine launches, consistent with this.

A user could stumble into a dashboard by clicking either button, but nothing
prompts them to and it would not survive a restart.

## The fix

**Separate "can we connect to SteamVR" from "can we deliver an action".** They
are different questions and only the second needs a shortcut.

- The bridge must start, open its OpenVR session, and show the dashboard **with
  no shortcuts configured and with Streamer.bot unreachable.** Both are normal
  states for a first run.
- "Add at least one controller shortcut" becomes **informational guidance**, not
  an error that halts startup. The desktop status area and the VR dashboard
  should both invite the user to add one.
- Keep `Validate`'s existing rules for the paths that genuinely need them —
  saving, and delivering an action. Do not weaken `ValidateForSave` or per-
  shortcut `Validate`. A shortcut that is malformed must still be rejected on
  save.
- Check `ValidateConnection` similarly: a missing or malformed Streamer.bot
  address should not prevent the SteamVR session either. It should surface as
  guidance and block delivery, not block VR entirely.

Look for any other early `return` in the runtime start path that could strand a
first-run user the same way.

## Bug 2 — registration is never retried when SteamVR appears

`RegisterSteamVrAsync` is called once from `InitializeAsync`, and otherwise only
from the manual **Repair SteamVR setup** button. Nothing retries it when the
OpenVR runtime later becomes available.

Bootstrap failure:

1. User installs and opens SteamVR2Bot with SteamVR closed — the likely order
   right after installing.
2. Registration fails; the app says it will retry next launch.
3. User starts SteamVR.
4. Nothing retries. No manifest, no dashboard entry, for the whole session.

**Fix:** retry registration when the runtime becomes available, not only at
process start. The app already has a retry-with-backoff pattern for SteamVR
availability (`BridgeEngine`'s 1/2/5/10/30s ladder) and another for the
Streamer.bot event stream — follow one of those rather than inventing a third.

Note this was **not** what caused the reported log — registration succeeded on
all nine launches there. It is a separate path that will affect other testers.

## Bug 3 — the documented repair script cannot run

`scripts\Register-SteamVrApp.ps1` line 18:

```powershell
$bridge = Join-Path $PublishDirectory "diagnosticsSteamVR2Bot.Diagnostics.exe"
```

Missing a directory separator, so it looks for
`artifacts\publish\diagnosticsSteamVR2Bot.Diagnostics.exe` and always throws.
The manual repair path the README points users at has never worked.

```powershell
$bridge = Join-Path $PublishDirectory "diagnostics\SteamVR2Bot.Diagnostics.exe"
```

Check the rest of that script for the same class of mistake while you are in
there, and confirm it runs end to end against a real publish.

## Also fix: the 104 message points the wrong way

`SteamVrApplications` logs a message telling the user to restart SteamVR when
auto-launch fails with error 104 (`UnknownApplication`, the known
manifest-not-yet-indexed condition — ValveSoftware/openvr#1378). That advice is
correct **for that condition** and actively misleading here: the user restarts,
104 clears, and the app still does not appear because the real blocker is
unrelated.

Once the fix above is in, that message is accurate again for the case it
describes. Re-read its wording and make sure it does not imply the restart will
also make the dashboard tile appear, since with the fix the tile will already be
there.

## Also worth handling: stale manifest registrations

The same log shows the app registered a manifest from **three different
locations** as the user moved the folder:

```
C:\Users\roguetr\Desktop\app.vrmanifest
C:\Users\roguetr\Desktop\SteamVR2Bot\app.vrmanifest
C:\Users\roguetr\Desktop\Streaming\SteamVR2Bot\app.vrmanifest
```

The README states registration removes SteamVR entries left by an older install,
"otherwise SteamVR would launch a copy from each registered folder". **Verify
that actually happened here** — three registrations from one machine suggests it
did not, or did not cover this case. Two of those paths no longer exist.

Removing a registration whose file is already gone may be the gap. Handle it, and
log which stale entries were removed.

## Hard constraints

1. No new NuGet packages.
2. **Do not weaken save-time or delivery-time validation.** A malformed shortcut
   must still fail on save; an unreachable Streamer.bot must still fail delivery
   with the existing no-duplicate-retry semantics.
3. **Do not break** chat, notifications, the dashboard, or shortcut delivery for
   users who already have shortcuts.
4. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests:**
   - The runtime starts with an **empty** shortcut list — assert the bridge task
     is created, not short-circuited.
   - It starts with an empty or malformed Streamer.bot address.
   - `ValidateForSave` still rejects a malformed shortcut.
   - Delivery still fails cleanly with no Streamer.bot, preserving the existing
     `DeliveryMayHaveOccurred` behaviour.
   - Registration retries and succeeds when the runtime becomes available after
     an initial failure, without a relaunch.
2. All existing tests pass. Build clean, no new warnings.
3. **Run `scripts\Register-SteamVrApp.ps1` end to end** against a real publish
   and confirm it completes — it has never worked, so nobody knows what else is
   downstream of that first throw.
4. **The decisive manual test — do this on a genuinely clean machine state**, not
   the dev machine, since existing settings are what hid this bug:
   - Delete `%LOCALAPPDATA%\SteamVR2Bot\settings.json`.
   - Launch the app with SteamVR running and **no shortcuts and no Streamer.bot**.
   - **The SteamVR2Bot dashboard tile appears and opens.**
   - Create a shortcut entirely from inside VR, using the wizard that was
     previously unreachable.
   - Confirm it then fires.
4. Append the result to `LIVE_TEST_RESULTS.md` following its conventions.

## When you are done

Summarise what changed, confirm a first run with no settings reaches the VR
dashboard, and state whether stale manifest registrations are now cleaned up.
