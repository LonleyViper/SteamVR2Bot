# Claude Code prompt — Phase 4b: VR settings, with tabs

Model: **Sonnet.** UI work on a proven, hardware-tested dashboard. The one
delicate part is the navigation-model change, which is structural rather than
subtle.

**Prerequisite: Phase 4 must be landed and committed first.** This phase surfaces
the settings model Phase 4 creates. Building it against a model that is still
moving means building it twice.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` first, then read the settings model as it
actually exists in `UserSettings.cs` after Phase 4 — **do not assume its shape
from this prompt.** This phase adds a VR surface to whatever Phase 4 landed; it
does not define it.

## Scope

Put the settings that can only be judged while wearing the headset into the VR
dashboard, reached by tabs.

Do **not** add `SetOverlayInputMethod` to the chat or notification overlays. This
is the *dashboard* overlay, which already has laser input. Phase 5 still gates
interaction on the other surfaces.

## B1. Why only some settings belong in VR

Anchor mode, size and opacity cannot be judged outside the headset. Changing them
on the desktop, putting the headset on, taking it off to adjust again is the loop
this phase exists to remove.

**Put in VR** — the things tuned by looking:

- Anchor mode (controller / head) and which hand, per surface
- Panel size and opacity
- Chat on/off, notifications on/off
- Gaze sensitivity, if it can be presented as a small number of named choices
  rather than a raw number

**Keep on desktop only:**

- WebSocket address and password — typing in VR is punishment
- Anything already on the desktop that is not in the list above

Do not mirror the whole desktop settings surface. A smaller VR page that covers
what the headset is needed for is the goal.

## B2. The navigation-model change — the delicate part

The VR dashboard is currently a **wizard**, not a tabbed application:

- `VrDashboardController` drives a page stack over a private `DashboardPage`
  enum: `List`, `GestureType`, `Tolerance`, `ActionPicker`, `RecordInput`,
  `Review`.
- `VrDashboardLayout` holds a bottom bar at `BarY = 800` with per-page rectangle
  rows — `Tolerance`, `RecordInput`, `ActionPicker`, `Review` — each meaning
  Cancel / Back / Next or similar, hit-tested by `IndexAt`.
- `VrDashboardRenderer` has one `Render*` entry point per page.

Tabs are **peer navigation**, which is a different model from a page stack. Adding
them means:

1. A tab strip as a new rectangle row in `VrDashboardLayout`, at the top rather
   than at `BarY`.
2. A notion in `VrDashboardController` of which tab is active, sitting *above*
   the existing page stack rather than replacing it — the shortcut wizard must
   keep working exactly as it does now, with Back/Next/Cancel intact inside its
   tab.
3. A renderer entry point for the settings page, following the existing
   `Render*` convention.

**`VrDashboardLayout`'s whole design rationale is that the renderer and the click
handler read the same rectangles, so a laser click can never land on a different
button than the one being pointed at.** Read its comments before changing it and
preserve that property. Every new tab or control must have its rectangle declared
in that shared table, never computed independently in the renderer.

Mirror the desktop's tab names where they correspond, so the two surfaces teach
each other.

## B3. Renderer choice — stay on GDI+, deliberately

`VrDashboardRenderer` is GDI+ (~1000 lines). All the Phase 2/3 chat and
notification rendering is WPF. This phase adds more GDI+.

That is the intended choice, not an oversight. The dashboard is proven and
hardware-tested, settings pages are simple toggles and choices with no text
wrapping, and rewriting working VR UI to chase renderer consistency is a bad
trade right now.

Record this in the plan as a deliberate decision, with "migrate the dashboard to
WPF" noted as possible later cleanup — so the two renderers are a known state
rather than something that happened by accident.

## B4. Controls — reuse the tolerance slider pattern

Keep the control vocabulary small and laser-friendly. Big targets, generous
spacing, **no dragging**.

- Toggles for on/off settings.
- Segmented choices (two to four adjacent buttons, current one highlighted) for
  anchor mode and hand — these are genuinely discrete.
- **For opacity and size, reuse the existing tolerance slider.** Do not invent
  named steps, and do not build +/- buttons.

The tolerance picker already solves continuous values under a laser, and it is
hardware-proven. Study `VrDashboardController.HandleToleranceClick` (around line
167) and the track rendering in `VrDashboardRenderer.RenderTolerancePicker`
before writing anything new. Two properties make it work and both must be
carried over:

1. **Click-to-position, never drag.** The value comes from the click's x ratio
   across the track. There is no press-and-hold state to track.
2. **A generous hit region and snapped values.** The region is roughly 220 pixels
   tall, so vertical aim barely matters, and the result snaps to sensible
   increments (100 ms there) so laser jitter cannot produce a silly value.

Apply the same shape: wide track, tall hit region, snap opacity and size to
increments coarse enough that no one needs a steady hand. One click reaches any
value, which beats both named steps and +/- buttons for this input method.

Every change applies immediately and saves automatically, matching the existing
behaviour — the README's promise is that there is no separate Save step. The user
must be able to see the effect of an anchor or size change without leaving the
settings page.

## B5. Interaction with Phase 4's transient overrides

Phase 4 introduced control commands from Streamer.bot that set **transient
overrides** over the saved settings. The VR settings page edits the **saved**
settings, the same as the desktop.

Decide and document what happens when a user edits a setting in VR while a
Streamer.bot override is active for it. The recommendation is that an explicit
user edit clears the override for that setting — a person actively changing a
control should not have their change appear to do nothing. Show the state
plainly, so an active override is never mistaken for a broken control.

## Hard constraints

1. **No NuGet packages.**
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break the existing shortcut wizard.** Every page, button and flow in
   it must behave exactly as before. This is the main regression risk.
4. **Do not break** chat, notifications, or shortcut delivery.
5. Match existing style: file-scoped namespaces, `sealed` by default, nullable
   enabled, records for data, XML docs explaining *why*.

## Verification — required

1. **Self-tests, no headset needed:** every tab and control rectangle is declared
   in `VrDashboardLayout` and hit-tests to the control it is drawn as — extend
   the existing `TestDashboardBottomBarLayout` pattern; the existing wizard's
   page stack and bottom-bar hit testing are unchanged; a settings change
   persists and applies without a restart; editing a setting with an active
   override resolves as documented in B5.
2. All existing tests still pass.
3. Build clean, no new warnings.
4. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, following that
   file's convention that an unrun row is recorded as "not run" and never assumed
   to have passed. Cover: each tab opens and renders; switching chat between
   controller and head anchor from VR takes effect immediately and visibly;
   size and opacity changes are visible without leaving the page; **the entire
   shortcut wizard still works end to end — create, edit and delete**; the
   settings survive an app restart; **an existing shortcut still fires exactly
   once**.

## When you are done

Summarise what changed, confirm the self-tests pass, list the manual headset
steps, and state explicitly whether any existing wizard page changed behaviour —
if one did, that is a regression, not a feature.
