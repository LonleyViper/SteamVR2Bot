# Handoff — Phase 7: notification customisation

Status: **core feature complete and live-tested; one sub-feature (direct
event subscription UI) needs a redesign.** Everything else in
`PHASE7_NOTIFICATION_CUSTOMISATION_PROMPT.md` (§B1, §B3, §B4, §B5) is built,
tested, and confirmed working in the headset. §B2 (direct Streamer.bot event
subscription) is functionally correct and tested, but its desktop UI is not
good enough yet and needs to be rebuilt properly rather than iterated on
piecemeal - see "What still needs work" below before touching it again.

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` §5/§5c and
`PHASE7_NOTIFICATION_CUSTOMISATION_PROMPT.md` first for the original spec.
This document records what actually happened against that spec, including
two live-tested design reversals worth understanding before changing
anything - re-making either mistake would cost another headset session.

## What shipped and is confirmed working live

- **§B1 - positioning frame.** `NotificationOverlay` gained a positioning
  mode (`SetPositioningEnabled`) showing a persistent, grabbable dummy frame,
  reusing `OverlayDrag`/`VrOverlayTransform.InverseRigid`/`OverlayPlacement`
  (the same Core primitives chat's grab-to-place uses) without touching
  `ChatOverlay.cs`. VR dashboard Notifications tab has the toggle + a reset
  button. **Live-tested: grab/drag/reset/restart-persistence/auto-off-on-
  navigate all PASS** - see `LIVE_TEST_RESULTS.md`'s Phase 7 Round 1.
- **§B3 - animations.** `NotificationAnimation.cs` (Core): Fade/Slide/
  ScalePop as closed-form functions of `NotificationPlayer`'s own bounded
  fade timeline (not a new asymptotic ease - important, see "The one hard
  rule" below), gated by `NotificationTransitionAnimator` so overlay calls
  stop entirely once a notification is holding steady. **Live-tested: PASS.**
- **§B4 - colours/duration/opacity/corner radius.** Settings-level
  background/text/accent colour, default duration, background opacity (0-1,
  independent of the panel's own fade), and corner radius, all with
  payload-wins-when-present precedence for duration/accent/image. **Live-
  tested: PASS**, including confirming the precedence rule itself (a
  payload's hardcoded `accent` correctly overriding the settings default -
  this was reported as a "bug" once and is actually correct behaviour; see
  the payload contract note below before changing it).
- **§B5 - PNG template.** `WpfNotificationRenderer` loads/caches/downscales
  a template with `ImageBrush(Stretch=Uniform)` for letterbox fit, defensive
  loading (missing/malformed/truncated → no background, never throws). Per
  live feedback, the text scrim and the accent stripe are now **fully
  hidden** (not just faded) whenever a template is active - a template gets
  exactly the wearer's own background opacity and text colour, nothing this
  renderer adds uninvited. A "Clear" button exists next to the template path
  field. **Live-tested: PASS**, including the missing/corrupt-file fallback.

All of the above have passing self-tests (see `TestNotificationTransitionAnimatorConvergesAndStopsIssuingCalls`
- the single most important one, proving each transition issues **zero**
overlay calls once holding steady - plus template/precedence/PNG-defensive-
loading tests in `TraySelfTests.cs`, and `GetEvents`/Subscribe/template-
resolver tests in `SvrBridge/SelfTests.cs`). Both suites pass, `dotnet format
--verify-no-changes` is clean on both projects, 0 warnings/0 errors.

## Two real bugs this session caught and fixed live

1. **Notification placement/appearance silently reverted on every restart.**
   `TrayApplicationContext.SaveVrSettingsChange` (the method that persists
   whatever the VR dashboard reports back) never copied
   `VrSettingsSnapshot.NotificationPlacement`/`NotificationAppearance` into
   the saved `UserSettings` - every VR-side drag or reset applied live in
   that session but never reached disk. Fixed, and the merge was extracted
   into a pure `internal static UserSettings MergeVrSettingsSnapshot(...)`
   with a self-test (`TestMergeVrSettingsSnapshotPersistsEveryField`) that
   pins every field of a `VrSettingsSnapshot` to a distinct value and
   asserts it survives - specifically so "forgot to copy a field" can't
   silently recur the same way twice.
2. **A desktop UI freeze** in the §B2 events list (search box, and a
   "Toggle group" button) - just fixed, unverified live. Root cause:
   `ApplyEventSearchFilter`/the toggle-group handler mutated `Visible`/
   `Checked` on dozens of `CheckBox` controls inside `AutoSize`
   `FlowLayoutPanel`s with no `SuspendLayout()`/`ResumeLayout()`, so every
   single toggle triggered its own relayout - at 50+ controls per group that
   reads as the whole app freezing. Fixed by wrapping both in suspend/resume
   pairs, with a final `PerformLayout()` to also fix a related "can scroll
   to blank space" symptom (stale `AutoScroll` extents). **Not yet
   re-verified live** - do this first before anything else.

## What still needs work: §B2's UI

### The live-tested, load-bearing finding

Streamer.bot exposes **no way to ask which events currently have an enabled
trigger** - not via any documented WebSocket request, and confirmed live:
toggling every event off in Streamer.bot's own Settings → Events panel did
not stop this app receiving them, and non-alert plumbing (OBS scene
changes - not even shown in that panel's categories) came through
indistinguishable from a real alert.

Two design attempts were made and both are recorded so a third attempt
does not repeat either mistake:

- **First attempt: subscribe to every event `GetEvents` reports,
  automatically.** Rejected live - see above. `NotificationEventSettings`
  briefly dropped its `EnabledEvents` field during this attempt; it is back.
- **Second attempt (current): opt-in, default nothing enabled**, with a
  flat scrollable list rebuilt to loosely mirror Streamer.bot's own Events
  panel (search box, grouped `FlowLayoutPanel`s by source, a "Toggle group"
  button, one `CheckBox` per event). **Rejected live as unusable**: "no
  grouping to indicate which platform", froze on search, froze on toggle
  group, could scroll to blank space. The freeze is fixed (see above, needs
  live re-verification); the usability complaint is not.

### What the user actually asked for, in their own words

> "we need to be able to post alerts to the notifications - e.g. Twitch
> Subscriptions, Twitch Follows, Kick Follows, Youtube Subscriptions etc.
> It would be really nice to have a little platform icon appear with the
> text so its intuitively communicated to the user too."

Read literally, this wants:

1. **Visually unmistakable platform grouping** - not just a text label
   header, something that reads as "this section is Twitch" at a glance
   (colour, icon, or both).
2. **A small icon per platform** next to each event (or at least per
   group) - Twitch/Kick/YouTube/OBS/etc. This app has no image assets
   today; check whether embedding a handful of small platform glyphs
   (as embedded resources, not a network fetch) is acceptable, or whether
   a text/colour badge (e.g. a coloured chip reading "TW"/"KICK"/"YT") is
   an acceptable substitute - ask before spending time on real icon
   assets, since licensing/trademark use of official platform logos may
   need the user's own sign-off.
3. Alert names in plain language ("Twitch Subscriptions") - `GetEvents`
   likely already returns something close to this per-event (the earlier
   screenshot the user shared showed Streamer.bot's own event names are
   already fairly readable: "Subscription", "Gift Subscription", "Channel
   Point Reward Redemption"), so this may already be satisfied once the
   list is legible - confirm against a live `GetEvents` response before
   assuming a translation layer is needed.

### Recommendation before touching this again

Do **not** keep iterating on the current `FlowLayoutPanel`-of-checkboxes
structure - it has now caused two live-reported problems (freeze, unclear
grouping) from the same underlying choice (many small dynamically-built
WinForms controls). Consider instead:

- A `ListView` in a details/grouped mode (WinForms' `ListView` has native
  group headers with less manual layout math, and a `SmallImageList` for
  per-row icons - this may solve the icon requirement and the grouping
  clarity requirement at once, with far fewer individually-laid-out child
  controls than one `CheckBox` per event).
- Or a virtualised/owner-drawn list if `ListView`'s native checkbox column
  proves awkward to combine with per-row icons and group headers.

Whichever approach: build a throwaway prototype and test it with a
realistic event count (60+, matching the real Twitch category size shown
live) for responsiveness before wiring it to settings persistence, given
the freeze already cost one live session.

### Explicit note on hardcoding

`GetEvents`, template resolution, and Subscribe-building all still contain
**zero hardcoded platform/event names** in production code (only in test
fixtures) - confirm this is still true after any UI rework. Icons/grouping
should be driven by whatever `Source` string `GetEvents` actually reports,
not a hardcoded enum of "known" platforms - Kick, for instance, should
group and (if built) get an icon the same way Twitch/YouTube do, with no
special-casing, since a hardcoded platform list is exactly the violation
§5c's governing principle warns against.

## Manual headset matrix

`LIVE_TEST_RESULTS.md`'s "Phase 7 — notification customisation" section has
the full row-by-row history across two test rounds. Rows 7-10 (event
subscription) are the only ones still marked "not run" against the current
(opt-in, freeze-fixed) design - do those first once the UI rework above is
done, not before, since the current list is confirmed unusable even though
the underlying subscribe/notify logic tests pass.

## Standing reminders (already in project memory, worth restating)

- Always `scripts\Publish-Poc.ps1` and run `artifacts\publish\SteamVR2Bot.exe`
  before any headset test - never `dotnet run` from source.
- Kill a running instance blocking a publish directly; don't ask first.
- `dotnet` is not on `PATH` in this shell - use
  `Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'`, or prepend
  that directory to `$env:PATH` before calling `scripts\Publish-Poc.ps1`
  directly (that script shells out to bare `dotnet`).
- A clean self-test run and a clean build are not evidence a VR-facing
  feature works - this phase alone had two bugs (placement persistence,
  the events UI) that all automated tests passed straight through.
