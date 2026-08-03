# Claude Code prompt — Phase 4: anchors, control commands, and an in-app test harness

Model: **Sonnet.** Refactoring and feature work on a substrate that is proven in
the headset. No interop, no new dependencies, no silent-corruption failure modes.

**Before starting:**

1. Commit the Phase 3 working tree. Ten new source files and seventeen modified
   files are uncommitted and live-confirmed working — that is the highest-risk
   state in the project.
2. Walk rows 13–15 of the Phase 3 matrix in `LIVE_TEST_RESULTS.md` and record
   them. Two minutes, no harness needed: trigger a notification with chat
   visible, open the dashboard, fire an existing shortcut. They protect the
   product contract.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` first. Sections §4, §5 and §6 are relevant.

## Scope

Three things, in this order:

1. An in-app chat test harness replacing the abandoned Streamer.bot `!svrtest`
   action.
2. A unified overlay anchor model — controller and head, with world-lock
   deliberately deferred.
3. Control commands, so Streamer.bot actions can drive the overlays.

Do **not** build buttons, laser interaction or `SetOverlayInputMethod`. That is
Phase 5 and it stays off.

## B1. In-app chat test harness (do this first — it tests the rest)

A previous attempt put a test harness in a Streamer.bot C# action (`!svrtest`).
It never fired, and could not be diagnosed from this app's logs, because the
failure was in another program. **Do not retry that approach.** Put the harness
in the app, where it is versioned, visible and debuggable.

Follow the existing precedent: the tray menu already has a developer item,
**Show VR test overlay (developer)**.

Add developer menu items that inject synthetic messages directly into the chat
ring buffer, bypassing the WebSocket entirely:

- **Inject a burst** — around 12 messages at once, to exercise repaint
  coalescing.
- **Fill the ring buffer** — around 45 messages, past the 40-message cap, to
  exercise eviction.
- **Inject a long message** — an unbroken 300-character string with no spaces, to
  exercise wrapping.
- **Inject a multi-badge message** and one referencing an unknown emote, to
  exercise badge rendering and emote fallback.

Keep these behind the same developer gating as the existing test overlay. They
are diagnostics, not features.

This unblocks Phase 3 matrix rows 9, 10, 17c and 18b/18c, which are currently
unrunnable without hours of manual typing.

## B2. Unified overlay anchors

There are currently three unrelated transform paths: the wrist transform for
chat, the head transform for notifications, and none for the test overlay.
Replace them with **one anchor abstraction any surface can use.**

```
OverlayAnchor:
  Mode:   Controller | Head
  Hand:   Left | Right          (Controller mode only)
  Offset: position and rotation relative to the anchor
```

Both reference projects converged on this model independently — OpenVROverlayPipe
uses world/head/left-hand/right-hand, OpenVRTwitchChat uses Screen/Controller/
World with offsets — which is good evidence it is the right shape. See §6.

Requirements:

- Chat and notifications both use it. "Chat on my head" and "notifications on my
  wrist" become the same feature rather than two special cases.
- **Preserve the two transforms already proven in the headset**: the tipped
  wrist transform from Phase 1 and the head transform from Phase 2 must remain
  the defaults and must look identical after the refactor. This is a refactor,
  not a redesign — if the panels move, something is wrong.
- **Do not cache tracked device indices.** Re-resolve on device activation and
  role change. This survived a real sleep/wake cycle in Phase 1 and must not
  regress.
- Desktop settings to choose anchor mode and hand per surface, following the
  existing `UserSettings` pattern with migration for existing saved settings.

### World-lock is deferred — document, do not build

World-lock needs `SetOverlayTransformAbsolute` (a new vtable index — the
tracked-device-relative call cannot express it) *and* a placement mechanism. A
world-locked window with no way to position it is useless, and grab-to-place
needs the overlay input that Phase 5 gates.

Add a short note in the plan recording this as a deliberate deferral and what it
would need, so it is not rediscovered as an oversight.

## B3. Control commands from Streamer.bot

`StreamerBotEventPayload` already parses `target: "control"` with a `command`
field, and nothing consumes it. Wire it up.

Commands to support: `show`, `hide`, `clear` (empty the ring buffer), and an
anchor change — for example `command: "anchor"` with a mode and hand.

**Critical design rule: control commands set transient overrides, never
persisted settings.**

Do not let a Streamer.bot payload write to `settings.json`. Two reasons: a buggy
action would permanently misconfigure the app, and the desktop UI and Streamer.bot
would then be two writers fighting over the same state with no resolution rule.

Instead: the desktop UI sets the persisted default; a control command overrides
it in memory until explicitly cleared or the app restarts. That is also the
better behaviour — "put chat on my head for this fight," reverting on its own,
is what a user actually wants.

Add a `reset` command that drops any override and returns to the saved settings.

Unknown commands are logged and ignored, never fatal — the payload is
hand-authored on the Streamer.bot side and will be wrong sometimes.

### Worth noting in the docs

This closes a useful loop that needs no new code: a controller gesture already
fires a Streamer.bot action, and a Streamer.bot action can now drive the
overlays. So gesture → SB action → chat window moves, **with no overlay input
handling and no laser contention.** Document this as the recommended way to
control the chat window from VR, because it sidesteps the input-focus problem
entirely.

## B4. Documentation

**Do not document any Streamer.bot chat-trigger requirement.** An earlier
hand-off claimed that Streamer.bot only forwards `Twitch.ChatMessage` over its
WebSocket while an enabled trigger of that type exists in its own Action list,
based on one observation of chat going quiet for 32 minutes. The user has since
confirmed chat arriving with no such action present, so the claim as stated is
not supported and must not be written into the README.

If it is revisited later, the test is to disable every action with a Twitch Chat
Message trigger and check whether chat still arrives — and the distinction that
matters is between a *relay action* (not required) and *any chat-triggered
action at all* (unverified). Do not assert either without a controlled test.

## Hard constraints

1. **No NuGet packages.**
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break** the dashboard, notifications, chat, or shortcut delivery.
4. **Do not enable `SetOverlayInputMethod`.**
5. Match existing style: file-scoped namespaces, `sealed` by default, nullable
   enabled, records for data, XML docs explaining *why*.

## Verification — required

1. **Self-tests, no headset needed:** anchor transform composition produces
   matrices identical to the current wrist and head transforms (guard the
   refactor); each control command applies the expected override; `reset`
   restores saved settings; an unknown command is ignored without throwing; a
   control payload never mutates persisted settings; the injector produces the
   expected ring-buffer state for burst, cap and long-message cases.
2. All existing tests still pass.
3. Build clean, no new warnings.
4. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, following that
   file's convention that an unrun row is recorded as "not run" and never assumed
   to have passed. Cover: chat and notifications appear unchanged after the
   anchor refactor; switching chat to head anchor works and reverts on `reset`;
   the injector's burst and cap runs behave; controller sleep/wake still
   reattaches; the dashboard still opens; **an existing shortcut still fires
   exactly once**.

## When you are done

Summarise what changed, confirm the self-tests pass, list the manual headset
steps, and state which previously-unrun Phase 3 matrix rows the new injector now
makes testable.
