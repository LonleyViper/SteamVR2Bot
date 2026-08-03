# Claude Code prompt — Phase 7: notification customisation

Model: **Sonnet.** Feature work on a proven substrate — overlays are blink-free,
grab-to-place works, the Notifications tab exists. No interop.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` §5 and §5c first. §5c's governing
principle constrains one of the features below in a way that is easy to violate
by accident.

## Scope

Make notifications configurable: **position**, **which kinds appear**,
**entry/exit animation**, **colours**, and an **optional PNG template**.

The base notification stays what it is today — title and text, rendered once per
notification. Everything here is customisation around that.

Out of scope: animated GIF templates (a follow-on, see B5), layout variants,
stacking multiple notifications at once, and Streamer.bot-triggered animations
(the `target: "control"` channel already exists for that, but it is a separate
piece of work).

## B1. Position — a moveable frame, not sliders

Notifications only exist for a few seconds, so there is nothing to grab in
normal use.

Add a **"Position notifications"** toggle on the Notifications tab that pins a
persistent dummy notification frame. While it is on, the frame is grabbable and
placeable exactly as the chat window is — reuse the Phase 5 grab-to-place path,
do not write a second one. Turning the toggle off dismisses it and keeps the
placement.

- The placement is stored as the notification anchor's offset, the same
  mechanism chat already uses. No new storage concept.
- **The frame must be trivially dismissible**, and the reset-placement control
  must cover it. A user who drags it somewhere unreachable needs a way back
  without removing the headset — this is the same hazard the self-limiting laser
  probe was designed around.
- Show representative text in the dummy frame, not an empty box, so size and
  colour choices can be judged from it.

## B2. Event toggles — driven by `GetEvents`, and subscribe to only what is on

Notifications currently arrive only as hand-authored `General.Custom` payloads.
This phase adds **direct subscription to real Streamer.bot events**, the same way
Phase 3 did for `Twitch.ChatMessage`, so follows and raids work with no
Streamer.bot-side authoring at all.

### The toggle list is fetched, never hardcoded

`GetEvents` returns every event the connected Streamer.bot instance can emit.

- Call it on connect and **populate the Notifications tab's toggle list from that
  live response.** Group by source (Twitch, YouTube, …) as the response does.
- **Subscribe to exactly the enabled events**, and re-subscribe when the
  selection changes. Filtering by not receiving beats receiving and discarding.
- **Do not hardcode a list of event types anywhere.** If you write `"Follow"` or
  `"Raid"` as a literal outside a test, that is the violation — §5c's principle
  is that Streamer.bot owns all platform knowledge. The one exception is
  `General.Custom`, which is this app's own contract and must stay subscribed
  unconditionally.
- Nothing is enabled by default beyond today's behaviour. A user upgrading must
  not suddenly be shown notifications they never asked for.
- `GetEvents` failing must not take down the feed — fall back to the current
  subscription set and log it.

### Text comes from a template, not a per-event mapper

Do **not** hand-write a mapper per event type. Instead, resolve a template
against the event's `data` object:

```
{targetUser.name} just followed!
```

- Dotted paths index into nested objects. A path that is missing or null
  resolves to empty, never an exception.
- A generic default template, overridable per event on the Notifications tab.
- Roughly fifty lines, and it covers every event — including ones that do not
  exist yet.

### Read the schemas; do not guess field names

Every event's page carries its properties, full JSON Schema and a generated
example payload — for example
<https://docs.streamer.bot/api/websocket/events/twitch/follow>. The schemas are
also source files in the `Streamerbot/docs` repository. **Look up the fields for
any event you set a default template for.**

**Nullability is not a matter of caution here, it is in the schema.**
`Twitch.Follow` declares `targetUser` as `oneOf: [null, TwitchBaseUser]`, and
every field inside `TwitchBaseUser` as `["null", "string"]`. Events arrive with
these null. Handle it, and log one dropped notification rather than throwing.

`isTest` is also on the payload. Expose a setting for whether test-fired events
produce a notification; default to showing them, since a user firing a test
wants to see the result.

## B3. Entry and exit animations

Offer a small set of named transitions: **fade** (current behaviour), **slide**
(from a chosen edge), and **scale pop**. Driven by `SetOverlayAlpha`,
`SetOverlayWidthInMeters` and the anchor transform — **no texture repaint is
involved**, which is what makes them cheap.

OpenVROverlayPipe's transition model (a list of transitions with easing curves)
is the design reference; §6 already credits it.

### The one rule that matters

**Every animation must converge and stop.**

This project has already paid for this. `ChatOverlay.AnimateGaze` eased
exponentially toward its target forever, never arrived, and issued around 200
overlay API calls per second permanently — which surfaced as a blink that took
two sessions to trace. See the fix in `9b5dc0c`.

So:

- Snap exactly to the target within an epsilon and mark the animation complete.
- **While complete, issue zero overlay calls.** Not "call with the same value" —
  no call at all.
- **No looping or idle animation.** A gentle bob or float never terminates by
  definition and is exactly the pattern above. If it is ever wanted, it is a
  deliberate decision to accept continuous API traffic, not a free extra.

**A visual check cannot catch this.** A non-converging animation looks perfectly
still — the residual movement is far below a pixel. Only counting calls catches
it, so the test in the verification section is not optional.

## B4. Colours and duration

- Background and text colours as settings.
- **Accent already comes from the payload.** Keep that working.
- A global **default duration**, overridden by the payload's `duration` when
  present.
- **Precedence rule, consistent throughout:** settings are the default, the
  payload overrides per-notification. Same pattern as Phase 4's transient
  overrides. Apply it uniformly — a reader should not have to check each field.

## B5. PNG template — optional

A user-supplied PNG drawn as the panel background, with title and text
composited on top. This is the model OpenVROverlayPipe uses (an image plus
positioned text areas) and it costs nothing per frame: load once, cache, and the
notification still renders exactly once.

- A template path in settings; an optional **`image`** payload field overrides it
  per-notification, per the same precedence rule.
- **Aspect handling:** the panel is 900×260 and user images will not match.
  Fit-with-letterbox rather than stretch. Choose deliberately and comment why.
- **Defensive loading.** These are user files. A malformed, truncated or missing
  image must cost that notification its background and nothing more — it must
  never take down the render thread. Apply the same discipline
  `StreamerBotEventPayload.TryParse` uses for hand-authored payloads.
- **Cap the decoded size.** Downscale on load; a 6000×4000 template cached for a
  900×260 panel is real memory for no benefit.
- Text must stay legible over an arbitrary background. A subtle scrim or shadow
  behind the text is worth it — a template with a light area makes white text
  vanish otherwise.

**Animated GIF is deliberately excluded.** It would mean decoding a frame,
re-compositing text, and uploading a texture repeatedly for the notification's
whole lifetime — turning one render into ~50 for a 5-second notification, and
making this the first thing in the app that renders continuously while a VR game
runs. Revisit it separately, with a frame-rate cap and an actual frame-timing
measurement.

## Payload contract

One new optional field on the `General.Custom` notification payload:
**`image`**. Optional, so this stays backward compatible — **do not bump `v`**,
and a payload without it must behave exactly as it does today.

No `kind` field is needed: B2 replaced that idea with `GetEvents`-driven
subscription, which needs no contract change at all.

Update §5c of `CHAT_AND_NOTIFICATIONS_PLAN.md` to record that direct event
subscription now covers notifications as well as chat, and that `General.Custom`
remains the escape hatch for custom alerts.

## Hard constraints

1. No new NuGet packages.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break** chat, the dashboard, the shortcut wizard, or shortcut
   delivery.
4. Do not enable `SetOverlayInputMethod` on the notification overlay outside the
   positioning frame — notifications are transient and must not capture the
   laser in normal use.
5. Match existing style; XML docs explain *why*.

## Verification — required

1. **Self-tests:**
   - **Each animation converges and then issues zero overlay calls per tick** —
     assert against a counting fake `IVrOverlayApi`. This is the most important
     test in the phase.
   - The toggle list is built from a `GetEvents` response, and a `GetEvents`
     failure falls back without taking the feed down.
   - Only enabled events are included in the `Subscribe` request; changing the
     selection re-subscribes.
   - **Template resolution:** a dotted path resolves; a missing path, a null
     intermediate (`targetUser: null`, per the real schema) and a null leaf each
     resolve to empty rather than throwing.
   - `General.Custom` stays subscribed regardless of the toggles.
   - Precedence: payload beats settings for duration, accent and image; settings
     apply when the payload is silent.
   - A malformed, truncated and missing PNG each degrade to no background
     without throwing.
   - An oversized image is downscaled.
2. All existing tests pass. Build clean, no new warnings.
3. **Manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, its convention
   being that an unrun row is recorded as "not run" and never assumed passing:
   - The positioning frame appears, is grabbable, and placement survives a
     restart; reset returns it to default.
   - Each animation looks right and the panel comes visibly to rest.
   - Toggling a kind off suppresses it; toggling back on restores it.
   - A PNG template renders with legible text over it.
   - **Colours correct on every surface, not channel-swapped** — the recurring
     bug class that reads as a deliberate palette.
   - Chat and the dashboard are unaffected.
   - **An existing shortcut still fires exactly once.**

## When you are done

Summarise what changed, confirm the self-tests pass — **naming the animation
call-count result explicitly** — list the manual headset steps, and confirm the
`kind` list contains no hardcoded platform values.
