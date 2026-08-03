# Claude Code prompt — Phase 3: the wrist chat window

Model: **Sonnet.** The largest phase so far, but every risky piece is already
retired: the overlay substrate is hardware-proven, the WPF pipeline works, the
wrist transform was confirmed in Phase 1, and device-index re-resolution survived
a real sleep/wake cycle. This is composition on proven parts.

**Before starting:** record the outstanding Phase 2 manual rows in
`LIVE_TEST_RESULTS.md` (steps 4, 7, 11, 12, 13) and commit Phase 2. Step 12 —
shortcut delivery with the dashboard closed — is the product contract and the
one row that must not be left unrecorded.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` first. Sections §4 and §5c govern this
task.

## Scope

A chat window anchored to the left controller, showing messages from
Streamer.bot payloads with `target: "chat"`.

Do **not** build buttons, laser interaction, scrolling, replying, or emote
images. Phase 5 covers interaction; emotes are explicitly deferred.

## B1. Visibility — gaze-scale, not show/hide

The window is **permanently present** behind the left controller. It scales up
and fades in when looked at, and shrinks and fades back when not.

This is deliberate and comes from OpenVRTwitchChat, which shipped it to a lot of
users: the window stays peripherally visible so the wearer always knows where it
is, and only becomes readable — and only occupies real estate — on gaze. Do not
implement hide-until-summoned.

- Detect with a dot product between the head's forward vector and the
  head-to-overlay vector. `MotionSample` already provides head and both
  controller poses in a head-relative body frame.
- **Hysteresis is required**, not optional. A single threshold makes the window
  flicker when the wearer's gaze sits near the boundary, which is both ugly and
  nauseating. Use separate enter and exit angles.
- Animate with `SetOverlayAlpha` and `SetOverlayWidthInMeters` only. **No texture
  repaint is involved in the gaze animation** — that is what makes it free, and
  it can run at frame rate while the chat repaint stays throttled.

## B2. Repaint throttling — the performance rule

Chat arrives in bursts. Repainting per message would put the render thread and a
full texture upload on the critical path during exactly the moments chat is
busiest.

- Repaint only when the message list actually changed.
- Throttle to roughly 10 Hz, **coalescing a burst into one repaint** rather than
  queueing one per message.
- Never attach the repaint to the input poll loop, which runs at 10 ms.

§4 explains why this matters beyond this app: the overlay is a separate process
sharing a GPU with a game trying to hit an 11 ms frame budget, and work landing
at the wrong moment costs the game a frame, which the wearer feels as judder.

## B3. Rendering

A second `IVrPanelRenderer` implementation alongside the notification renderer —
not a fork of it. Reuse `WpfRenderThread` and `WpfOverlayPixelPipeline`; the
un-premultiply and channel-swap path is already proven and must not be
reimplemented.

- A ring buffer of the last N messages, newest at the bottom. Roughly 40 fits a
  512×768 texture at readable size.
- **Text wrapping is the whole reason this phase uses WPF.** Messages are
  arbitrary length, usernames sit inline in a different colour, and a long URL
  with no spaces must not overflow or be silently clipped. Use real
  `TextWrapping` with inline `Run` elements, not hand-measured layout.
- Username colour and badge text come **from the payload**, not from platform
  detection. The renderer draws whatever string and hex colour it is handed and
  has no opinion about where they came from — see §5c. `Colour` is already
  normalised to `#RRGGBB` or empty; pick a sensible default for empty.
- Apply a little `SetOverlayCurvature`, which measurably helps readability at
  wrist distance.
- Emotes: render `:emoteName:` as plain text. Do not fetch images.

## B4. Two structural rules — cheap now, expensive later

These are from §4 and are requirements, not suggestions.

1. **Declare hit rectangles from day one, even with zero buttons.** Follow the
   `VrDashboardLayout` pattern — a shared rectangle table read by both the
   renderer and (eventually) the click handler, so a laser click can never land
   on a different button than the one being pointed at. Adding buttons in Phase 5
   should mean adding table entries, not restructuring the renderer.
2. **Do not enable `SetOverlayInputMethod`.** v1 is read-only. Enabling input
   makes the overlay compete for the controller laser, which is the same class of
   problem this project spent its life on. Structure for it; do not turn it on.

## B5. Anchoring

Reuse the wrist transform proven in Phase 1 — tipped towards the wearer like a
watch face, confirmed in headset at steps 2–4. Do not invent a new one.

Re-resolve the left controller's tracked device index on device activation and
role change. **Do not cache it.** This survived a real sleep/wake cycle in Phase
1 and that behaviour must not regress. Handle no-left-controller-present without
throwing.

Use one persistent surface with a fixed key, created once and disposed by exactly
one owner — the same discipline as the notification surface, for the same reason
(`VrOverlaySurface` has no finalizer, correctly).

## B6. Wiring — including a change to how chat arrives

Consume `Target == Chat` payloads from the existing event stream channel. The
ring buffer is written by the channel consumer and read by the render thread —
**make that thread-safe**; they are different threads.

Gate behind a setting following the existing `EventStreamEnabled` pattern,
defaulting to off.

### Subscribe to the platform chat event directly (this is new)

§5c of the plan was **revised** — read it again if you read an earlier version.
Phase 0 subscribes only to `General.Custom`, which requires the user to build a
relay action in Streamer.bot before any chat appears. That is no longer the
default route.

Add a **raw subscription** alongside it:

- Subscribe to the platform chat event — `Twitch.ChatMessage` at minimum — in
  the same `Subscribe` request that already asks for `General.Custom`.
- Map its payload to a `StreamerBotEventPayload` with `Target = Chat`: display
  name → `User`, message text → `Text`, the user's colour → `Colour` (normalise
  through the existing `#RRGGBB` handling), and a badge or role label → `Badge`.
- **Confirm the actual field names before writing the mapper.** Do not guess
  them. Read
  <https://docs.streamer.bot/api/websocket/events/twitch/chat-message> or the
  TypeScript definitions in the `@streamerbot/client` package, and log one real
  received payload to verify. The event schema has changed across Streamer.bot
  versions.
- Parse defensively, exactly as `StreamerBotEventPayload.TryParse` already does.
  A field that is missing or a different type must cost one message, never the
  feed.
- Keep `General.Custom` working unchanged. It is the escape hatch for custom
  alerts and SB-side filtering, and Phase 2's notifications depend on it.

Why this matters, since it reverses an earlier decision: Streamer.bot still owns
the entire platform integration either way — `Twitch.ChatMessage` is the parsed
output of its OAuth, connection and reconnection work, so nothing platform-*side*
enters this app. And the raw event carries colour, badges and role, all of which
a hand-written relay action would realistically drop. The relay route was both
more setup for the user and a worse-looking chat window.

Keep the mapper isolated in one small file per platform so adding YouTube or
Kick later is an additional mapper rather than a change to anything else.

Chat history does not survive a restart (§8) — the window starts empty. Do not
add persistence.

## Hard constraints

1. **No NuGet packages.**
2. **Do not modify** `StreamerBotClient.cs`, the event stream beyond consuming
   it, the input/gesture/chord/binding code, or `MotionSampling`.
3. **Do not break** the dashboard, notifications, or shortcut delivery.
4. Match existing style: file-scoped namespaces, `sealed` by default, nullable
   enabled, records for data, XML docs that explain *why*.

## Verification — required

1. **Self-tests needing no headset:** ring buffer eviction order; a burst of N
   messages producing one repaint rather than N; hysteresis not oscillating when
   the gaze angle sits exactly at the boundary; wrapping of a long unbroken
   string; empty/missing username and colour handled; thread-safe append while
   rendering.
2. All existing tests still pass.
3. Build clean, no new warnings.
4. **A manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, following that
   file's convention that an unrun step is recorded as "not run" and never
   assumed to have passed. Cover at minimum: the window sits behind the left
   controller and is legible on gaze; it scales and fades smoothly with no
   flicker when the gaze sits near the threshold; a burst of chat does not stall
   or drop; long messages wrap rather than clip; it reattaches after a controller
   sleep/wake; notifications still work alongside it; the dashboard still opens;
   **and an existing shortcut still fires exactly once**.

## When you are done

Summarise what changed, confirm the self-tests pass, list the manual headset
steps, and state whether frame-timing impact was measured or not — do not report
it as "no impact" if it was not measured.
