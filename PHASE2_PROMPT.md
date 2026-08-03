# Claude Code prompt — Phase 2: head-anchored notifications

Model: **Sonnet.** The substrate is proven and there is no interop archaeology
left. This is a render pipeline, a queue and a timed animation — real work, but
no silent-corruption failure modes.

**Before starting:** run steps 12–14 of the Phase 1 manual matrix in
`LIVE_TEST_RESULTS.md` and record the result. They take fifteen seconds and are
the only check that a fixed overlay key is released rather than leaked — the
self-test uses a fresh GUID each run and structurally cannot catch it. Every
surface from here on uses a fixed key.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` first. Sections §4 (rendering), §5
(notifications) and §5c (the Streamer.bot contract) govern this task.

## Scope

Display a head-anchored notification when a Streamer.bot payload with
`target: "notification"` arrives on the event stream built in Phase 0.

Do **not** build the chat window, the wrist anchor, gaze detection, message
history or any interaction. Those are Phases 3–5.

## Why notifications come before chat

They are the smaller half, they exercise the whole substrate end to end, and
they do not depend on how the wrist-gaze heuristic tunes out. They are also the
right place to introduce WPF rendering, because the content is a title and a
short line of text — if the pipeline has a problem, you find it here rather than
while also fighting text wrapping.

## B1. Introduce WPF rendering

Per §4, rendering is WPF `RenderTargetBitmap`, not GDI+ and not SkiaSharp. Add
`<UseWPF>true</UseWPF>` alongside the existing `<UseWindowsForms>true</UseWindowsForms>`
in `SvrBridge.Tray.csproj`. **This adds no NuGet package** — WPF ships in the
Windows SDK, and all projects must still have zero `PackageReference`.

Three things that will bite:

1. **`RenderTargetBitmap` requires an STA thread with a `Dispatcher`.** Do not
   render on the WinForms UI thread — a repaint would block the window. Create a
   dedicated STA render thread with its own dispatcher, own it explicitly, and
   shut it down cleanly.
2. **`RenderTargetBitmap` produces `Pbgra32` — premultiplied alpha.** OpenVR
   expects straight (non-premultiplied) alpha. Un-premultiply when converting, or
   the notification will look correct at full opacity and wrong during the fade,
   which is the hardest version of this bug to spot.
3. **The existing `SetOverlayRaw` path already performs a BGRA→RGBA channel
   swap.** Step 3 of the Phase 1 matrix exists purely to catch that swap being
   inverted, because wrong colours otherwise read as a deliberate palette. Reuse
   the existing conversion rather than writing a second one, and keep a check for
   it in the manual matrix.

Structure the renderer behind a small interface so Phase 3's chat renderer is a
second implementation rather than a fork of this one.

## B2. One persistent surface, not one per notification

**Requirement:** the notification overlay is a single long-lived
`VrOverlaySurface`, created once, hidden when idle, with its texture replaced per
notification. Do **not** create and destroy a surface per notification.

This is deliberate. `VrOverlaySurface` has no finalizer — correctly, since
calling into OpenVR from the finalizer thread against a possibly-disposed session
is worse than the leak it would prevent. That makes ownership discipline the only
protection, and a create/destroy cycle per notification is the pattern most
likely to leak a handle under load. One owner, one surface, one dispose.

## B3. Notification behaviour

- Anchored to the head: `SetOverlayTransformTrackedDeviceRelative` against the
  HMD's tracked device index (0), with a fixed offset placing it in comfortable
  view — in front and slightly below centre. Make the offset a constant with a
  comment explaining the values, not a magic matrix.
- Fade in → hold → fade out. Duration comes from the payload's `DurationMs`,
  which `StreamerBotEventPayload` already clamps to 500–60000 ms.
- **Animate with `SetOverlayAlpha` only.** The texture is rendered once per
  notification and never repainted during the animation. Alpha is a separate API
  call, so the fade costs nothing per frame. This matters — §4 explains why
  continuous GPU work from an overlay process costs the running game frames.
- A queue, so two events arriving together play in sequence rather than
  overwriting each other. Bound it, and drop oldest when full — a backlog of
  stale notifications is worse than missing some.
- The animation timer runs only while something is on screen and stops when idle.
  **Do not attach it to the input poll loop**, which runs at 10 ms.

## B4. Wiring

Consume the existing `Channel<StreamerBotEvent>` from Phase 0. Payloads with
`Target == Notification` drive the display. Leave `Chat` and `Control` payloads
going to the activity log as they do now.

Gate it behind a setting, following the `EventStreamEnabled` pattern already in
`UserSettings`, defaulting to off.

## Hard constraints

1. **No NuGet packages.** `UseWPF` is an SDK toggle, not a package.
2. **Do not modify** `StreamerBotClient.cs`, the input, gesture, chord or binding
   code, or `MotionSampling`.
3. **Do not break the dashboard or shortcut delivery.** Both were re-proven in
   Phase 1 and both must be re-checked here.
4. Match the existing style: file-scoped namespaces, `sealed` by default,
   nullable enabled, records for data, and XML doc comments that explain *why*
   rather than *what*.

## Verification — required

1. **Self-tests that need no headset**, in the existing style: queue ordering,
   bounded-queue drop-oldest behaviour, duration clamping applied end to end,
   the alpha curve producing 0 at the start and end and 1 during hold, and the
   render thread starting and shutting down cleanly without deadlock.
2. **A pixel-format test.** Render a known solid colour through the full WPF →
   un-premultiply → channel-swap path and assert the resulting bytes. This is the
   one bug class that looks plausible on screen and is invisible in a log.
3. All existing tests still pass.
4. Build clean, no new warnings.
5. **A manual headset matrix appended to `LIVE_TEST_RESULTS.md`**, following that
   file's existing table convention — including its discipline that an unrun step
   is recorded as "not run" and is never assumed to have passed. Cover at
   minimum: a notification appears in comfortable view; colours are correct, not
   channel-swapped; the fade is smooth at both ends; two notifications queue
   rather than overlap; the dashboard still opens and renders; an existing
   shortcut still fires.

## When you are done

Summarise what changed, confirm the self-tests pass, and list the manual headset
steps for the user to run. State explicitly whether the running game's frame
timing was affected, or that it was not measured.
