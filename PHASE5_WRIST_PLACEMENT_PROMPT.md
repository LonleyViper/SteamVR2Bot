# Claude Code prompt — Phase 5: grab-to-place the chat window

Model: **Opus.** This enables overlay laser input for the first time — the thing
this project has deliberately avoided since its founding problem was input focus.
The interop is small; the judgement about what captures input is not.

**Before starting — the working tree must be resolved.** `main` currently has
about twelve uncommitted files containing two unrelated things:

1. The **Part A gaze-convergence fix** — confirmed working. Commit this to `main`
   on its own.
2. The **D3D11 texture spike** — chat only, with `SetOverlayRaw` fallback intact.
   Land it behind a **default-off setting** rather than leaving it uncommitted or
   parking it on a branch. This phase edits the same files, so a branch left
   aside will be painful to merge later. Default-off keeps shipping behaviour
   unchanged while keeping the code compiled and exercised.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` §4 first, particularly the two structural
rules about hit rectangles and `SetOverlayInputMethod`.

## B0. The probe is already answered — read it, do not re-run it

`LIVE_TEST_RESULTS.md` records the result (2026-07-30): **an overlay accepting
laser input does not swallow the trigger from a running VR game.** User-confirmed
in headset with a control row separating this from the known action-set priority
behaviour.

So interactive controls on a persistent overlay are viable, and do not need
gating behind a summon gesture or a dashboard-only mode.

**One thing that result deliberately does not say**, quoting its own scope note:
it confirms the *game* still receives the trigger. It does **not** confirm the
*overlay* also received it. Whether a laser click actually lands on the panel is
untested, and this phase answers it the moment there is a handle to click.

**So make that the first thing you verify**, before building the drag: get a
click to register on the panel at all, and log it. If clicks do not land, stop
and report — everything below depends on it, and the reason will be worth
knowing before more is built on top.

The self-limiting probe (tray → **Chat test harness (developer)**) stays in the
build and can be re-run after a SteamVR update.

## Scope

Let the user grab the chat window with the laser and place it where they want,
relative to whichever anchor is selected. Release saves it.

Buttons that trigger Streamer.bot actions are the **next** increment on this same
plumbing — build the foundation so they are additive, but do not build them here.

## B1. The interaction

Modelled on OVRdrop, which the user specifically wants:

- A **move handle** drawn on the chat panel — a four-way arrow icon.
- Grab it with the laser, drag, and the panel follows.
- Release, and wherever it ended up is saved.
- It stays anchored to whatever anchor mode is selected (controller or head).

Implementation notes:

- The handle's rectangle goes in `ChatOverlayLayout`, which already exists for
  exactly this reason — it was created with no buttons so that adding them later
  would mean adding table entries rather than restructuring the renderer. Both
  the renderer and the hit test read that one table. Preserve that property.
- Overlay mouse events give 2D coordinates **on the overlay surface**, not a
  world-space ray. So: mouse-down on the handle **starts** the drag, the
  controller's pose from `MotionSample` drives the **movement**, and mouse-up
  **ends** it. Overlay input is only the grab/release signal.
- **Saving is nearly free.** `OverlayAnchor.Offset` is already a transform
  relative to the anchor device. During the drag, continuously recompute that
  offset and apply it. Release just means "stop recomputing" — the saved value is
  whatever the offset was at that instant. No world-to-local conversion step.
- **No new vtable index is needed** for controller and head anchors.
  `SetOverlayTransformAbsolute` would only be required for true world-lock, which
  remains deferred.

## B1b. Pointer feedback — two mechanisms, do not conflate them

### The pointer dot: let SteamVR draw it

**Do not draw a cursor into the panel texture.** Drawing it yourself means a WPF
re-render and a texture upload on every `VREvent_MouseMove`. Laser jitter alone
would drive that past 90 updates a second, against a chat repaint deliberately
throttled to 10 Hz and coalesced — so it would be both expensive and visibly
lagging behind the real pointer.

- First, **check whether SteamVR already renders a cursor** on a regular overlay
  with mouse input enabled. It certainly does on the dashboard; whether it does
  for a regular overlay is untested. Answer this in the same headset session as
  the "does a click land at all" check in B0 — both are free once there is a
  handle to point at.
- If it does not, use **`SetOverlayCursor`**, which takes a second small overlay
  and lets SteamVR position it. That keeps cursor tracking in the compositor at
  full frame rate and off your repaint path. It needs a new vtable index —
  derive it with the method documented in `OpenVrInput.TryGetOverlayTable`,
  including the anchor cross-check. Do not guess it.

### Button feedback: repaint on transition, never on movement

This one you do draw yourself, but keyed on **which rectangle the pointer is in,
not where it is**.

- Track the index of the `ChatOverlayLayout` rectangle currently under the
  pointer, updated from `VREvent_MouseMove`.
- **Repaint only when that index changes.** Sweeping across three buttons is
  three repaints, not three hundred. Moving within one button is zero.
- Leaving the panel entirely is a transition to "no rectangle" and repaints once.

Two reasons this is the right shape. It costs almost nothing, and a highlighted
button communicates better than a dot — it tells the wearer *what they would
hit*, not merely where they are aimed.

It also falls out of the rule this file already follows: the hover highlight and
the hit test read the **same** rectangle table, so what lights up is guaranteed
to be what activates. Do not compute hover geometry separately in the renderer.

Note the repaint-throttle interaction: hover transitions must reach the screen
promptly to feel responsive, so they should not be swallowed by the chat message
repaint throttle. Treat a hover change as its own reason to repaint rather than
folding it into the message-version throttle in `RepaintIfOwed`.

## B2. Gaze-gated input

`OverlayAnchor.Offset` is currently a hardcoded readonly constant per mode, and
this phase makes it user-owned. Alongside that:

**Input is only accepted while the window is in its gazed-at (large) state.**

`ChatOverlay` already computes gaze with hysteresis for the scale animation.
Reuse that signal. An accidental grab then requires the user to be *both* looking
at the window *and* pointing at it, which is close enough to intent.

If B0 showed the overlay consumes game input, go further: disable
`SetOverlayInputMethod` entirely outside the gazed state, rather than merely
ignoring events.

## B3. Persistence

- The offset is a **saved setting** per anchor mode, following the existing
  `UserSettings` pattern with migration.
- The current hardcoded `ControllerOffset` and `HeadOffset` become the
  **defaults**, not the only values. They are hardware-proven, so an
  unconfigured install must look exactly as it does today.
- Provide a **reset to default** control in the VR settings tab. A user who
  drags the window somewhere unreachable needs a way back, and reaching for the
  desktop app to fix a VR placement problem is the loop this phase exists to
  remove.
- Phase 4's Streamer.bot control commands set **transient overrides**. An offset
  dragged by hand is an explicit user edit and should clear any active override
  for that setting, consistent with the rule established in Phase 4b.

## Hard constraints

1. **Vortice.Windows** is the only added package; do not add others.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not enable input on the notification overlay.** Notifications are
   transient and must never capture the laser.
4. **Do not break** the dashboard, notifications, chat rendering, or shortcut
   delivery.
5. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests:** the handle rectangle hit-tests to the handle and nothing else;
   a drag from a known pose delta produces the expected offset; release persists
   that offset; reset restores the hardware-proven default exactly; input is
   rejected when not in the gazed state; a hand-dragged offset clears an active
   Streamer.bot override.
   **Hover specifically:** a sequence of mouse-move positions *within* one
   rectangle produces exactly **one** repaint, and crossing into a second
   produces exactly one more — assert the repaint count, not the highlight
   state. Counting is what catches a per-move repaint; asserting the highlight
   looks right would pass either way. This is the same class of bug as the gaze
   animation that never converged, which had correct values and wrong call
   frequency.
2. All existing tests pass. Build clean, no new warnings.
3. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, its convention
   being that an unrun row is recorded as "not run" and never assumed passing:
   - The handle is visible and grabbable when looking at the panel.
   - Dragging is smooth and the panel does not detach or jump.
   - Release saves; the position survives an app restart.
   - Reset returns it to exactly the original placement.
   - Pointing at the panel **without** looking at it does not grab it.
   - **In an actual VR game: normal play is unaffected when not deliberately
     interacting with the window.** This is the row that matters most.
   - Controller sleep/wake still reattaches, at the saved offset.
   - **An existing shortcut still fires exactly once.**

## When you are done

Summarise what changed, state the B0 probe result plainly, confirm the
self-tests pass, and list the manual headset steps. If the probe showed the
overlay consuming game input, say so prominently — it changes the next phase.
