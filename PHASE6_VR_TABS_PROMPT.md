# Claude Code prompt — Phase 6: three VR tabs, and a dashboard repaint throttle

Model: **Sonnet.** UI restructure on ground you have already walked twice. No
interop, no new dependencies. The risk is regression in the shortcut wizard, not
subtlety in the change.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` §4, and read `VrDashboardLayout`'s comments
before touching it — its whole design rationale is that the renderer and the
click handler read the same rectangles, so a laser click can never land on a
different control than the one being pointed at. That property must survive.

## Scope

1. Replace the current two tabs (**Shortcuts**, **Settings**) with three:
   **Shortcuts**, **Chat**, **Notifications**.
2. Split the existing Settings page's controls across the two new tabs by which
   surface they belong to.
3. Add a repaint throttle to the dashboard.

Do **not** add notification colour or layout customisation — that is the next
phase. This one moves what exists and adds the throttle.

## B1. The tab split

`VrDashboardLayout.Tabs` currently holds two rectangles at x=60 and x=380, both
300 wide, with `TabStripHeight = 76` and `TabStripY = 16`. A third at x=700, same
size, fits the 1400px panel without relayout. Verify that against the current
values rather than trusting these numbers.

`DashboardPage` currently has a single `Settings` member, documented as existing
only so the one-page-at-a-time model can represent the Settings tab. It becomes
two pages — one per new tab — following that same pattern and keeping that
comment's reasoning intact.

**Read the current Settings page and split its controls by owning surface.** Do
not invent new settings here. Roughly:

- **Chat** — chat on/off, its anchor mode and hand, size, opacity, and the
  reset-placement control from Phase 5.
- **Notifications** — notifications on/off, anchor mode, size, opacity.

If a control turns out to be genuinely global rather than per-surface, put it on
whichever tab it most affects and note the choice in a comment. Do not add a
fourth tab for it.

**No settings migration is needed.** The stored model is unchanged — only its
presentation splits. Do not write a migration for this; do not change
`UserSettings`' shape unless a control genuinely has nowhere to live.

## B2. The shortcut wizard must not regress

This is the third change to this navigation and the main risk each time.

The tab strip is **peer navigation sitting above** the wizard's page stack, not
part of it. Every wizard page — `List`, `GestureType`, `Tolerance`,
`ActionPicker`, `RecordInput`, `Review` — and its Cancel/Back/Next bar must
behave exactly as it does today. Creating, editing and deleting a shortcut is in
the test matrix end to end for that reason.

`ListRowsStartY` and the 5-row list count exist because the tab strip pushed the
list down; a third tab does not change the strip's height, so they should not
need to move. If they do, say so rather than adjusting silently.

## B3. Dashboard repaint throttle

The dashboard has no repaint throttle. The chat window does. Every dashboard
interaction is currently a texture write, and the dashboard is the one surface
still on the CPU upload path — a `CreateDashboardOverlay` handle accepts
`SetOverlayTexture` and never displays it, which is recorded in
`LIVE_TEST_RESULTS.md`. So dashboard texture writes still blink, and the new tabs
are slider-heavy.

Add coalescing, with one critical property:

**Leading edge, not trailing.** The first update in a burst must render
*immediately*; subsequent updates within the throttle window are coalesced into
one deferred render. A trailing-edge throttle would delay every single click and
make the whole UI feel laggy — worse than the blink it is meant to reduce.

- Around 15–20 Hz is the target. Tune it in the headset against rapid
  Tolerance-slider clicking, which is the original reproduction case.
- A coalesced update must never be **dropped**. The last state always reaches the
  screen, or a setting will appear not to have applied.
- This reduces blink frequency. It does not eliminate it. Do not describe it as
  a fix in any commit message or doc.

## Hard constraints

1. No new NuGet packages. Vortice.Windows is already present; add nothing else.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break** the wizard, chat, notifications, or shortcut delivery.
4. Do not change the visual design beyond what the tab split requires.
5. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests:** every tab and control rectangle is declared in
   `VrDashboardLayout` and hit-tests to the control it is drawn as — extend the
   existing layout tests; the wizard's page stack and bottom-bar hit testing are
   unchanged; each setting still persists and applies from its new tab.
   **Throttle specifically:** a burst of updates within the window produces the
   first render immediately and exactly one more afterwards, and the final state
   is the one rendered — assert the render *count* and the final state, not just
   that a render happened. The bug this catches is a per-click render, which
   looks correct and defeats the point.
2. All existing tests pass. Build clean, no new warnings.
3. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, its convention
   being that an unrun row is recorded as "not run" and never assumed passing:
   - All three tabs open and render; switching between them is immediate.
   - Every moved setting still works from its new home and still applies live.
   - **The full shortcut wizard works end to end: create, edit, delete.**
   - Rapid Tolerance-slider clicking blinks **less** than before — and the
     slider still tracks clicks without feeling delayed or dropping the final
     value.
   - Settings survive an app restart.
   - **An existing shortcut still fires exactly once.**

## When you are done

Summarise what changed, confirm the self-tests pass, list the manual headset
steps, and state plainly whether any wizard page changed behaviour — if one did,
that is a regression, not a feature.
