# Claude Code prompt — fix SteamVR registration retry and the repair script

Model: **Sonnet.** Two small, well-specified bugs. The retry has an existing
pattern to follow; the script fix is one line plus a verification run.

Context: a separate fix has already landed for the first-run blocker where the
runtime refused to start without a shortcut, so a fresh install now reaches the
VR dashboard. These are two **different** defects in the same area, found by a
second review, and neither is addressed by that fix.

Paste everything below the line.

---

## Bug 1 — registration is never retried when SteamVR appears

`RegisterSteamVrAsync` is called once from `InitializeAsync`, and otherwise only
from the manual **Repair SteamVR setup** button. Nothing retries it when the
OpenVR runtime becomes available later in the session.

That produces a bootstrap failure in the most likely post-install order:

1. User installs and opens SteamVR2Bot with SteamVR closed.
2. Registration fails. The app reports that it will retry next launch.
3. User starts SteamVR.
4. Nothing retries. No manifest registration and no dashboard entry for the
   rest of the session, even though SteamVR is now running.

### Fix

Retry registration when the runtime becomes available, not only at process
start.

- **Follow an existing retry pattern rather than inventing a third.**
  `BridgeEngine` already has a 1/2/5/10/30-second SteamVR availability ladder,
  and `StreamerBotEventStream` has a capped indefinite reconnect. Reuse whichever
  fits; the SteamVR ladder is the closer match.
- Stop retrying once registration succeeds. Do not leave a timer running for the
  life of the process.
- Log the eventual success, so a user watching the activity log sees the state
  change rather than silence.
- Do not spam the log while SteamVR stays closed — the event stream's
  "log the first failure, then drop to debug" approach is the precedent.

### Note on scope

This is **not** what caused the reported test-user log. Registration succeeded on
all nine launches there. This is a separate path that will affect other testers,
so do not expect fixing it to change anything about that particular log.

## Bug 2 — the documented repair script has never worked

`scripts\Register-SteamVrApp.ps1`, line 18:

```powershell
$bridge = Join-Path $PublishDirectory "diagnosticsSteamVR2Bot.Diagnostics.exe"
```

Missing a directory separator, so it looks for
`artifacts\publish\diagnosticsSteamVR2Bot.Diagnostics.exe`, fails its own
`Test-Path`, and throws. The README points users at this script as the manual
repair path.

```powershell
$bridge = Join-Path $PublishDirectory "diagnostics\SteamVR2Bot.Diagnostics.exe"
```

**The one-line fix is not the whole job.** Because that `Test-Path` throws on
line 19, *nothing after it has ever executed*. There may well be further
mistakes sitting behind it that no one has ever reached.

- Read the whole script and check for the same class of error.
- **Run it end to end against a real publish** and confirm it completes. That is
  the actual deliverable here, not the edit.

## While you are in there — verify two items from the previous fix

The earlier prompt included these. Confirm they actually landed rather than
assuming:

1. **The error 104 message.** It tells the user to restart SteamVR when
   auto-launch fails with `UnknownApplication`. That is correct for the
   manifest-not-yet-indexed condition it describes, but it must not imply the
   restart is what makes the dashboard tile appear — with the first-run fix in
   place, the tile is already there.
2. **Stale manifest registrations.** The test user's log shows manifests
   registered from three locations as they moved the folder:
   `Desktop\`, `Desktop\SteamVR2Bot\`, `Desktop\Streaming\SteamVR2Bot\`. The
   README claims registration removes entries left by an older install. Two of
   those paths no longer exist — verify stale entries are actually removed,
   including when the file they point at is already gone, and log which were
   removed.

## Hard constraints

1. No new NuGet packages.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not re-tighten the first-run validation** that was just relaxed. The
   runtime must still start with no shortcuts and no Streamer.bot.
4. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests:** registration retries after an initial failure and succeeds
   once the runtime is available, without a relaunch; retrying stops after
   success; repeated failures do not flood the activity log.
2. All existing tests pass. Build clean, no new warnings.
3. **`scripts\Register-SteamVrApp.ps1` runs end to end** against a real publish
   and completes without throwing.
4. **Manual test of the bootstrap order that fails today:**
   - Close SteamVR completely.
   - Launch SteamVR2Bot. Registration fails as expected.
   - Start SteamVR, leaving SteamVR2Bot running and untouched.
   - **Registration succeeds on its own**, with no relaunch and no button press.
   - Restart SteamVR once, then confirm the dashboard tile and auto-launch are
     both in place.
5. Append the result to `LIVE_TEST_RESULTS.md` following its conventions,
   recording unrun rows as "not run".

## When you are done

Summarise what changed, state whether the repair script now runs end to end and
whether anything else was wrong behind that first throw, and confirm the two
carried-over items from the previous fix are genuinely in place.
