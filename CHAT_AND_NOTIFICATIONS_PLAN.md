# Chat window and event notifications — architecture plan

Status: planning. No code has been written.

Decisions taken up front:

- Overlays are rendered **in-process** by SteamVR2Bot. No second app to install,
  and no Unity — see §4.
- The chat window is **wrist-anchored**, permanently present, and **scales up and
  fades in on gaze** rather than being hidden until summoned.
- Rendering is **WPF `RenderTargetBitmap`**, delivered to SteamVR through a
  **persistent Direct3D 11 texture via `SetOverlayTexture`**. *Revised
  2026-07-29 — see §3 Risk 2.* This supersedes the original `SetOverlayRaw`
  decision and the original zero-NuGet constraint: **Vortice.Windows is an
  accepted dependency.**
- **Streamer.bot owns all platform integration and filtering.** This app holds no
  Twitch/YouTube/Kick knowledge and subscribes to a single event,
  `General.Custom`. See §5c.
- **Flat panel, no stereo.** 3D-looking content is fine, stereoscopic depth is
  not worth its cost.
- v1 is **read-only**. Buttons are a later phase, structured for but not built.
- BOLL7708's and Hotrian's projects are **reference only**. No GPL code is
  linked; credit goes in the README.
- The focused-input objective is considered closed. This is the next build phase.

---

## 1. The two paths are one subsystem

Chat display and event notifications look like separate features, but they need
the same thing that does not exist yet: a **non-dashboard overlay** that
SteamVR2Bot owns, positions itself, and repaints frequently.

Everything currently rendered in VR goes through `CreateDashboardOverlay` and is
only visible while the SteamVR dashboard is open. A chat window that only appears
when you open the dashboard is not a chat window. So both features sit on one new
layer, and that layer is the bulk of the work.

```
Streamer.bot ──WS──▶ event stream ──▶ chat model ──────┐
                                  └─▶ notification queue ┴─▶ overlay surface ─▶ SteamVR
```

Build the shared layer once, then the two consumers are small.

---

## 2. What already exists and helps

The codebase is in better shape for this than it first appears:

| Need | Already present |
|---|---|
| Hand-rolled `IVROverlay` FnTable access | `OpenVrInput.TryGetOverlayTable`, 10 validated indices |
| Left/right controller device index | `GetControllerRoleForTrackedDeviceIndex` (line ~298) |
| Head and hand poses each tick | `SampleMotion` / `MotionSample` / `BodyFrame` |
| GDI+ text and card rendering | `VrDashboardRenderer` (1047 lines of it) |
| Streamer.bot WebSocket with auth | `StreamerBotClient` |
| Settings persistence and migration | `UserSettings` / `UserSettingsStore` |
| A self-test harness that runs at startup | `TraySelfTests` |

`MotionSampling.cs` in particular was added as "body-frame motion sampling
groundwork" and is exactly what the wrist-summon gesture needs.

---

## 3. The four real risks

These are the things that will cost days if they are discovered late.

### Risk 1 — do NOT put the event subscription on `StreamerBotClient`

`StreamerBotClient`'s request/response shape assumes the next frame it receives
is the response to what it just sent:

```csharp
await SendJsonAsync(socket, new { request = "GetActions", id }, ct);
using var response = await ReceiveJsonAsync(socket, ct);   // assumes next frame is mine
```

Add a `Subscribe` to this client and that assumption breaks — an unsolicited
chat frame lands between a `DoAction` send and its response, and the chat message
is returned as the action result. That would break the validated delivery path,
which is the thing the whole app exists to do.

**The fix is not to refactor this class. It is to not touch it.**

Two facts make that the correct answer rather than a shortcut:

1. `StreamerBotClient` is constructed **fresh per delivery and disposed
   immediately** — see `BridgeEngine` (lines ~140, ~362), `TrayApplicationContext`
   (~493) and every `SelfTests` case. It is ephemeral by design.
2. Streamer.bot subscriptions are **per-connection**, and events are only sent to
   connections that subscribed.

So a client that never subscribes can never receive an event. The risk is
eliminated by construction, not by careful concurrency work.

The event stream therefore becomes a **new, separate type** —
`StreamerBotEventStream` — owning its own long-lived socket. It needs the
opposite lifetime anyway: permanently connected, reconnecting forever, where a
missed event is harmless. Nothing about the existing client's conservative
"three bounded attempts, never blindly resend" behaviour applies to it, and
nothing about it should be allowed to affect action delivery.

The new type still needs a proper receive pump internally — a background read
loop, a `ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>>` for
its own request/response pairs such as `Subscribe`, and a
`Channel<StreamerBotEvent>` for inbound events — but it is greenfield code with
no existing behaviour to preserve.

**Constraint for Phase 0: `StreamerBotClient.cs` is not modified.** The password
hashing in `BuildAuthentication` and the connection/auth handshake should be
extracted or duplicated rather than refactored in place, so the existing
`SelfTests` cases keep passing unchanged.

### Risk 2 — PNG-to-disk rendering will not survive chat

`UpdateDashboard` renders a `Bitmap`, writes a PNG to disk, then calls
`SetOverlayFromFile`. There is already `_imageSequence` and `_oldImagesCleaned`
cleanup machinery, which is a hint that this is under pressure at page-change
rates.

Chat arrives in bursts. A disk write plus PNG encode plus a SteamVR file decode
per repaint is not viable.

**Add `SetOverlayRaw(handle, void* buffer, uint width, uint height, uint bytesPerPixel)`**
and pass `BitmapData.Scan0` from a `LockBits` on the existing GDI+ bitmap
straight through. No file, no encode, no cleanup.

### Revision, 2026-07-29 — `SetOverlayRaw` was not far enough

`SetOverlayRaw` removed the disk write and PNG decode, but **both CPU upload
paths visibly blink on every texture write** — chat on `SetOverlayRaw`, the
dashboard on `SetOverlayFromFile`. An investigation recorded in
`LIVE_TEST_RESULTS.md` established this was not application slowness (updates
measure 15–32 ms) and not introduced by any phase of this work — the untouched
Tolerance slider reproduces it — and concluded it was inherent SteamVR
behaviour.

**That conclusion was too broad, and a spike disproved it.** Both paths tested
were the same mechanism: a CPU buffer handed to SteamVR, which allocates and
uploads. Note that `SetOverlayRaw` takes *dimensions on every call*, implying
SteamVR treats each write as a new texture. The untested family was
`SetOverlayTexture` with a GPU texture, which desktop-mirror and video overlays
use at video rates without blinking.

The spike confirmed it: **a persistent Direct3D 11 texture written in place and
delivered via `SetOverlayTexture` removes the blink.**

Consequences:

- **Vortice.Windows is now an accepted NuGet dependency.** The zero-package
  state was a convention inferred from the csproj files, not a requirement, and
  the blink was judged to cost more than the dependency.
- WPF and GDI+ still do all the drawing. Only the delivery changed.
- `SetOverlayRaw` remains as the fallback path when no D3D11 device is
  available.

### Risk 3 — vtable indices are unforgiving

The overlay table is accessed by raw index: `GetTableDelegate<T>(pointer, 63)`.
Ten indices are validated against `FnTable:IVROverlay_028`. This module needs
roughly eight more:

`CreateOverlay`, `DestroyOverlay`, `ShowOverlay`, `HideOverlay`,
`SetOverlayRaw`, `SetOverlayAlpha`, `SetOverlaySortOrder`,
`SetOverlayTransformTrackedDeviceRelative`, and optionally `SetOverlayCurvature`.

A wrong index is not a clean error. It is an access violation inside SteamVR, or
worse, silent stack corruption.

Mitigation:

1. Derive **every** index in one pass over the `IVROverlay` declaration order in
   the `openvr.h` matching interface version 028 — do not guess individually.
2. Cross-check that the ten known-good anchors land where that pass predicts. If
   they do, the new ones are trustworthy. If they do not, the header version is
   wrong.
3. Add a `TraySelfTests` case: `CreateOverlay` a throwaway key → `FindOverlay`
   round-trips to the same handle → `DestroyOverlay`. A bad index then fails
   loudly at startup rather than mid-stream in front of an audience.

### Risk 4 — do not repaint on the input poll loop

`BridgeEngine` polls input at `PollIntervalMs = 10` (100 Hz). The chat texture
must not be rebuilt at that rate.

- Chat repaints only when the message list actually changed, throttled to ~10 Hz,
  coalescing bursts into one repaint.
- Notifications need a real animation tick, but only while one is on screen.
  Idle cost is zero.

---

## 4. The chat window

### Anchoring

`CreateOverlay` (not dashboard) plus `SetOverlayTransformTrackedDeviceRelative`
against the left controller's tracked device index, with a fixed offset matrix
tilting it like a watch face.

**Device index stability is a real hazard.** Tracked device indices are not
stable across controller sleep, reconnect, or battery swap, and roles can come
back unassigned. OpenVRTwitchChat hit this hard enough to write its own device
manager rather than trust the SteamVR plugin's. Bind the transform once to an
index that later goes stale and the chat window silently detaches with no error.
The overlay transform must be re-resolved whenever a device activates or a role
changes, not cached at creation.

### Visibility — gaze-scale, not show/hide

Do **not** hide the window until summoned. OpenVRTwitchChat keeps it permanently
present behind the left controller and *scales it up and fades it in when looked
at*, shrinking back when not. That is the better model: the window stays
peripherally visible so the user always knows where it is, and only becomes
readable — and only occupies real estate — on gaze. It has years of real use
behind it.

Detection is a dot product between the head's forward vector and the
head-to-overlay vector, with hysteresis so it does not flicker at the threshold.
`MotionSample` already provides head and both controller poses in a head-relative
body frame, so nothing new is needed to compute it.

Animate via `SetOverlayAlpha` and `SetOverlayWidthInMeters` — no texture repaint
is involved in the gaze animation, so it can run at frame rate for free while
the chat repaint stays throttled at 10 Hz.

### Optionally, later: bind visibility to a gesture

More explicit, but it requires a schema change. Every `ShortcutConfig` today
terminates in a Streamer.bot action (`ActionName` / `ActionId`). A gesture that
toggles the chat window is a *built-in* target, so `ShortcutConfig` needs a
target-kind discriminator plus a settings migration for existing saved shortcuts.
That is a migration on validated saved data and should not be spent until the
gaze model has been tried and found wanting.

### Rendering — use WPF `RenderTargetBitmap`, not GDI+

An OpenVR overlay is a texture; there is no text primitive in the API. The only
question is what rasterises it.

`VrDashboardRenderer` is GDI+ and has **never wrapped text** — every call site is
`DrawEllipsizedText` with `StringFormatFlags.NoWrap`. Chat is nothing but
wrapping: variable-length messages, a coloured username inline, mixed-width
glyphs, and eventually inline emote images. Hand-rolling that against
`MeasureString` is the fiddliest work in this whole plan, and GDI+ has no
colour-emoji support, so unicode emoji render as tofu.

Three options were considered:

| | Text layout | Emoji | New dependency |
|---|---|---|---|
| GDI+ | hand-rolled | no | none |
| **WPF `RenderTargetBitmap`** | **toolkit** | **yes** | **none** |
| SkiaSharp | toolkit + HarfBuzz | yes | first NuGet dep, ~10 MB native |

**Chosen: WPF.** It ships in the Windows SDK, so `<UseWPF>true</UseWPF>` next to
the existing `<UseWindowsForms>true</UseWindowsForms>` costs no package
reference — and all three projects currently have zero, which looks deliberate.
It gives real `TextWrapping`, inline `Run` elements for coloured usernames,
inline `Image` for emotes later, and `CopyPixels` writes straight into
`SetOverlayRaw`.

Cost: the render must happen on an STA thread with a dispatcher, and it is
slower than Skia. At a throttled 10 Hz that does not matter.

Layout: a ring buffer of the last N messages (≈40 fits a 512×768 texture at
readable size), newest at the bottom. `SetOverlayCurvature` slightly, which
measurably helps readability at wrist distance.

Username colour and badge text come **from the payload** (§5c), not from
platform detection — the renderer draws whatever string and hex colour it is
given and has no opinion about where they came from.

Emotes are out of scope for v1 — render `:emoteName:` as text. Note that
OpenVRTwitchChat marks emote support "experimental" even with Unity's full
texture atlasing available, which is a fair warning about the effort involved.
When it is tackled, the images should arrive as URLs or base64 in the payload so
that emote resolution also stays a Streamer.bot concern.

### Explicitly not in v1 — but structure for it

No `SetOverlayInputMethod`, no laser, no scrolling, no replying. v1 is
read-only, which sidesteps input focus entirely.

Buttons are a **later phase, not a later rewrite** — the interaction system
already exists and is hardware-validated. `VrDashboardLayout` is a shared
rectangle table read by both the renderer and the click handler (its own comment
explains this is so a laser click can never land on a different button than the
one being pointed at), driven by `SetOverlayInputMethod`, `SetOverlayMouseScale`
and `PollNextOverlayEvent`. All of that works on a regular overlay, not just a
dashboard one.

Two structural decisions now, both cheap, both expensive to retrofit:

1. **Declare hit rectangles from day one**, even with zero buttons. Follow the
   `VrDashboardLayout` pattern in the chat renderer immediately. Adding buttons
   then means adding table entries, not restructuring the renderer.
2. **`VrOverlaySurface` must be multi-instance, not a singleton.** One handle,
   one texture, one transform per instance, N instances coexisting — chat,
   notifications, and whatever follows. Baking a single overlay into
   `OpenVrInput` the way the dashboard is baked in today would force a real
   refactor later.

**Caveat for when buttons arrive:** an overlay with input enabled competes for
the controller laser, which is the same family of problem this project already
spent considerable effort on. The difference is that this overlay is ours, so
input method can be toggled dynamically — enabled only while the window is gazed
at or a modifier is held, off otherwise. Live-test that toggle early; overlay
focus in this project has repeatedly behaved in ways the documentation did not
predict.

### 3D: decided against — flat panel

An OpenVR overlay is a flat quad with a texture. There is no way to submit
geometry to an overlay as geometry. "3D" could therefore have meant either:

- **Flat panel showing 3D-rendered content** — depth, lighting, animation, but a
  picture of 3D with no stereo separation. One render pass.
- **Side-by-side stereo overlay** — genuine per-eye depth. Doubles render and
  texture cost, needs stereo flags derived from the header, and bad stereo is
  actively uncomfortable rather than merely unimpressive.

**Decision: flat.** No stereo overlay flags, no per-eye rendering.

This keeps the WPF `RenderTargetBitmap` → `SetOverlayRaw` pipeline from §4
intact and avoids a D3D11 device, a graphics binding (Vortice/Silk.NET), shader
authoring and device-lost handling — which stereo or high-frame-rate 3D would
all have forced.

Worth knowing: WPF is not limited to flat 2D drawing. It has animation with
easing, transforms, drop shadows, layering, and an actual `Viewport3D`. A panel
with real depth cues and smooth transitions is achievable without leaving the
chosen pipeline.

**The remaining constraint is frame rate, not dimensionality.** The readout is
throttled to 10 Hz. Anything continuously animated wants 45–90 Hz, and
`RenderTargetBitmap` per frame at that rate is CPU-bound and will not hold up —
that is the point where a GPU texture path becomes unavoidable regardless of
stereo.

Mitigation, which the gaze design already provides for free: **animate only
while gazed at, freeze to a static texture otherwise.** Chat is glanced at for a
few seconds at a time, so full-rate rendering applies to a small fraction of a
session. Cap at 45 Hz rather than headset refresh; UI chrome does not need 90.

Why this matters beyond this app's own performance: the overlay is a separate
process submitting GPU work while a game is trying to hit an 11 ms frame budget.
Work landing at the wrong moment costs the *game* a frame, which the user feels
as judder. In VR that is a comfort issue, not a cosmetic one, and it is the real
reason SteamVR overlays are conventionally static textures updated rarely.

### When Unity would actually be the right answer

Not for buttons, and not for multiple panels. Unity earns its cost only if the
panel needs 3D content, physics, particles, or geometry beyond
`SetOverlayCurvature`.

The reason it is not a smaller decision than it looks: Unity is not a library
that can be referenced from `SvrBridge.Tray` — it *is* the process. Adopting it
means either rewriting all ~10k lines on Mono/IL2CPP (losing the validated
IVRInput binding layer, the chord detector, the settings store, the tray host,
and the live-test results), or shipping a second process over IPC — which is the
same "another app to run" cost already rejected for OpenVROverlayPipe, plus the
obligation to build and maintain it, a ~150 MB publish folder, and a build that
needs Unity Editor licence activation in CI.

Note also that OpenVRTwitchChat is SteamVR Unity Plugin v1 from ~2016 and is
marked for deletion by its author. Unity as a *pure overlay app* — no HMD camera,
no scene render — is a far less trodden path now that Valve has moved toward
OpenXR. That would need verifying before betting a rewrite on it.

---

## 5. Event notifications

### Two implementation routes

**(a) `IVRNotifications_002.CreateNotification`** — the real SteamVR toast. Small,
unstyleable, and Valve's support for it has been inconsistent across SteamVR
versions. Needs its own notification overlay handle.

**(b) Own head-anchored overlay with fade-in / hold / fade-out** — reuses the
overlay surface built for chat. Full control of styling, duration and placement.
This is the route OpenVROverlayPipe settled on after starting from (a), which is
a meaningful signal.

**Recommendation: (b).** It is more code, but the code is already being written
for chat, and it does not depend on a Valve API of uncertain standing.

Queue notifications with a channel concept so two simultaneous events stack
side by side rather than on top of each other.

### Event mapping lives in Streamer.bot, not here

See §5c. SteamVR2Bot does not decide which events become notifications, does not
template them, and does not know what a follow or a raid is. Streamer.bot sends a
payload that already says what to display; this app renders it.

---

## 5c. The Streamer.bot contract — this app is a display surface

**Governing principle:** Streamer.bot owns every platform integration, all
filtering, and all message mechanics. SteamVR2Bot displays what Streamer.bot
sends it. This app should contain no Twitch, YouTube or Kick knowledge whatsoever.

### Two routes. Raw subscription is the default.

**Revised 2026-07-29.** An earlier version of this section recommended the relay
route as the *only* path. That was wrong, and the reasoning behind it conflated
two different things:

- **Platform integration** — OAuth, the EventSub/IRC connection, reconnection,
  parsing. Streamer.bot does all of this whichever route is taken.
  `Twitch.ChatMessage` is already the parsed, normalised *output* of that work.
  Subscribing to it does not put any platform integration in this app.
- **Platform filtering** — bot exclusion, command hiding, ignore lists. This does
  live in SB's actions and does *not* reach the raw subscription feed. True, but
  it was never a requirement — it was assumed into the design.

The decisive argument is that **the relay route loses data**. A raw
`Twitch.ChatMessage` carries username colour, badges, sub/mod status,
first-time-chatter status and emote positions. A hand-written relay action
realistically forwards `{user, text}` and drops the rest, so the chat window
would render *worse* through the route that also costs the user setup.

**Route A — raw subscription (default, zero setup).** Subscribe to the chat
event for each platform in use. Map its payload into
`StreamerBotEventPayload` with `target: "chat"`. The mapper is small — read the
display name, message text, colour and badges — and it is the only
platform-shaped code in the app. Adding a platform is one mapper, not an
architecture change.

**Route B — `General.Custom` relay (escape hatch, retained).** Already built in
Phase 0. It stays, because it is the right home for anything the raw feed cannot
express: custom alerts, SB-side filtering, arbitrary control commands, or
payloads assembled from several events. It is opt-in and requires no setup from
users who do not want it.

Both routes converge on the same `StreamerBotEventPayload`, so everything
downstream — the ring buffer, the renderer, the notification queue — is unchanged
and unaware of which route a message came from.

### The relay mechanism (Route B)

Streamer.bot's C# sub-actions `CPH.WebsocketBroadcastJson(string)` and
`CPH.WebsocketBroadcastString(string)` broadcast an arbitrary payload to every
connected WebSocket client, arriving as event source `General`, type `Custom`.

So the data path becomes:

```
platform ─▶ Streamer.bot trigger ─▶ SB action (filters, formats, decides)
                                         │
                                  WebsocketBroadcastJson
                                         │
                          General.Custom ▼
                                   SteamVR2Bot ─▶ renders it
```

Consequences, all of them good for this project:

- This app subscribes to exactly **one** event: `General.Custom`.
- Adding a platform is **zero code changes here** — it is an SB-side change.
- Filtering, bot exclusion, formatting, colours and rate limiting all live in
  Streamer.bot, where the user already works.
- The same channel carries display commands, not just content — show, hide,
  highlight, clear, push a notification — so the window becomes remotely
  controllable from any SB action.

### Payload contract (draft)

SteamVR2Bot defines the shape; SB actions fill it in.

```jsonc
{
  "target": "chat" | "notification" | "control",
  "user":    "",        // chat: display name
  "colour":  "#RRGGBB", // chat: username colour, chosen by SB
  "badge":   "",        // chat: freeform label, e.g. platform or role
  "text":    "",        // chat/notification body
  "title":   "",        // notification heading
  "duration": 5000,     // notification only, ms
  "accent":  "#RRGGBB", // notification only
  "command": ""         // control: show | hide | clear
}
```

Unknown fields are ignored; unknown `target` values are dropped with a log line.
Forward compatibility matters here because the SB side will evolve faster than
the app.

### Setup cost

Route A has none — turn the setting on and chat appears. That is the point of
making it the default, and it is consistent with how this app already treats
setup: the SteamVR binding, dashboard registration and settings migration all
happen without the user being asked.

Route B costs one action in Streamer.bot, paid only by users who want what it
offers. An importable action set can still ship later as a convenience for
common alert patterns, but it is no longer on the critical path to seeing chat.

### Revised 2026-07-31 — direct subscription now covers notifications too

Phase 7 (`PHASE7_NOTIFICATION_CUSTOMISATION_PROMPT.md`) extended Route A from
chat to notifications: the Notifications tab's toggle list is populated from a
live `GetEvents` response and the app subscribes to exactly the events the
wearer switched on, alongside `General.Custom` and `Twitch.ChatMessage`, which
stay subscribed unconditionally. Turning an event on requires no Streamer.bot
action of any kind — the same zero-setup property Route A already gave chat.

The mapper-per-event-type approach Route A used for chat (`TwitchChatMessageMapper`)
was deliberately **not** repeated for notifications. Instead, a generic dotted-path
template (`StreamerBotEventTemplate`) resolves against whatever `data` object an
event carries, so one ~50-line resolver covers every event Streamer.bot can emit,
including ones that do not exist yet — a mapper per event type would have meant a
mapper per platform update, forever.

`General.Custom` remains the escape hatch for anything the raw feed cannot
express: custom alerts, SB-side filtering or formatting, or a payload assembled
from several events. Nothing about it changed — a hand-authored payload still
carries an optional `image` field (§B5) that behaves exactly as it did before
when absent, and both routes still converge on the same
`StreamerBotEventPayload`, so the renderer and the notification queue remain
unaware of which route a given notification came from.

---

## 5b. A limitation to document, not debug

Some games bypass the SteamVR compositor and draw directly to the headset. In
those, **every** SteamVR overlay is invisible — chat, notifications, and the
existing dashboard alike. OpenVRTwitchChat documented this for Rift users in
2016 and it was never fixable from the overlay side.

This is a property of the platform, not a bug in this app. It belongs in the
README under a "known limitations" heading so it is not rediscovered as a
mystery bug during a stream.

`IsDashboardVisible` and the display mirror are the two diagnostics: if overlays
appear in the SteamVR display mirror but not in the headset, the game is
bypassing the compositor.

---

## 6. What to take from BOLL7708 and Hotrian — and what not to

Both repositories are **GPL-3.0**, and both are **archived** (OpenVR2WS and
OpenVROverlayPipe were archived in May 2026, with features "to be transferred" to
BVRTK). BVRTK itself is 7 commits, has no releases, describes itself as "early
days", and its own README states the texture backend work "has not started yet"
and JSON-RPC support does not work yet.

So even setting licensing aside, **none of it is a viable dependency today.**

Worth borrowing as design, written from scratch:

- **The anchor model.** `anchorType` of 0 world / 1 head / 2 left hand /
  3 right hand, plus `attachToAnchor` and independent `ignoreAnchorYaw` /
  `Pitch` / `Roll`. That is a well-shaped API and maps directly onto what both
  features need.
- **The follow behaviour.** Reposition the overlay when the user looks away past
  a trigger angle, easing over a duration. Good for notifications.
- **The transition and tween list model** for appear/disappear animation.

Explicitly not taken: EasyOpenVR as a dependency. It would make SteamVR2Bot
GPL-3.0 and would replace a hand-rolled binding layer that is already validated
against real hardware.

### Hotrian/OpenVRTwitchChat

A **Unity** project — "a stripped down version of the SteamVR Unity Plugin with a
custom Overlay script". Unity's text system renders to a RenderTexture whose
native GPU pointer goes to `SetOverlayTexture`. That path is unavailable without
embedding Unity, so none of its rendering code transfers. The repo is also marked
for merging and deletion by its author.

What it contributes is *validated product decisions*, taken as design only:

- **Gaze-scale rather than show/hide** (adopted, see §4).
- **Screen / Controller / World attachment with a snap-to-base-position and
  independent positional and rotational offsets.** Independently the same three
  anchors OpenVROverlayPipe arrived at — two unrelated projects converging is
  good evidence the model is right.
- **Don't trust controller role auto-identification** (adopted as the device
  index hazard in §4).
- **The compositor-bypass limitation** (adopted as §5b).
- **Emote support is hard** — marked "experimental" there even with Unity's
  texture atlasing available. Reinforces deferring it.
- A notification sound on message received. Cheap to add via `SoundPlayer`, and
  genuinely useful when the window is out of view.

### Credits

Add a Credits section to the README acknowledging:

- **OpenVROverlayPipe** (BOLL7708) — reference for the anchor and transition
  model.
- **OpenVRTwitchChat** (Hotrian) — reference for the gaze-scale interaction and
  the attachment model.

Neither contributes code. Both deserve the acknowledgement.

### Separately: there is no LICENSE file

The repository has none, which by default means all rights reserved. That may be
intentional, but it should be a decision rather than an omission — particularly
now that the README will credit GPL projects, which invites the assumption that
this project is also GPL.

---

## 7. Phasing

Notifications deliberately come before chat. They are the smaller half, they
exercise the entire new substrate end to end, and they keep working regardless of
how the wrist-summon heuristic tunes out.

**Phase 0 — the event stream.** A new `StreamerBotEventStream` type with its own
long-lived socket, subscribing to `General.Custom` and exposing a
`Channel<StreamerBotEvent>`. `StreamerBotClient` is **not modified**. Verifiable
entirely on the desktop with no headset: surface received payloads in the
activity log and watch chat arrive. *Doing the data path first means the VR work
in Phase 1 starts against a feed already known to be good.*

**Phase 1 — overlay substrate.** New vtable indices with the derivation
cross-check and the create/find/destroy self-test. A `VrOverlaySurface` type
wrapping create, raw texture upload, transform, show/hide, alpha, destroy. Prove
it with a static hello-world overlay pinned to the left controller.

**Phase 2 — notifications.** Head-anchored, one hard-coded event type with a
fixed template. Then the queue, then the animation, then configurability.

**Phase 3 — chat window.** Wrist-look summon with hysteresis, message ring
buffer, throttled repaint.

**Phase 4 — configuration and the SB action set.** Appearance and placement on
the desktop tab (font size, opacity, anchor offsets, gaze thresholds), then
mirrored into the VR dashboard where it fits. No event-mapping picker — that is
an SB-side concern now. Author and test the importable Streamer.bot action set
that feeds `General.Custom`, and document the payload contract.

**Phase 5 (later) — interaction.** Buttons on the chat window using the existing
`VrDashboardLayout` hit-rectangle pattern plus `SetOverlayInputMethod`, toggled
dynamically so the laser is only captured when wanted. Additive if phases 1–3
followed the two structural rules in §4.

---

## 8. Resolved — previously open questions

All four are now decided, and each removes work rather than adding it.

**Which platforms?** None. This app has no platform knowledge. Streamer.bot owns
every integration; the app subscribes to `General.Custom` and renders whatever
arrives. See §5c. *Removes: per-platform badge logic, payload shape handling,
`Subscribe` category management.*

**Filtering and chat mechanics?** Streamer.bot's, entirely. No exclusion list, no
bot detection, no command stripping in this app. *Removes: a whole configuration
surface and its settings migration.*

**Scrollback across a SteamVR restart?** Not needed. The window starts empty.
*Removes: serialisation, disk persistence, and a restore path.*

**Suppress notifications while the dashboard is open?** No. Notifications show
regardless. *Removes: an `IsDashboardVisible` check and a state interaction.*

### What this does to the phase plan

Phase 4 shrinks considerably. There is no event-mapping picker to build, because
mapping is an SB-side concern. Desktop configuration reduces to appearance and
placement — font size, opacity, anchor offsets, gaze thresholds — plus the
connection settings that already exist.

The work that replaces it is **authoring and testing the importable Streamer.bot
action set**, which is a different and smaller job.

### Still genuinely open

- Whether to also support raw event subscription as a no-setup fallback. It would
  give chat with nothing to import, but it is the one mode where platform
  knowledge re-enters the app. Recommendation: do not build it. If setup friction
  proves to be a problem, the answer is a better import experience, not platform
  code here.
- The payload contract in §5c is a draft and should be treated as versioned from
  the first release — add a `"v": 1` field before shipping, not after.

---

## 9. Phase 4 — anchors and control commands

Landed per `PHASE4_PROMPT.md`: a shared `OverlayAnchor` (Controller-with-hand
or Head) that both chat and notifications now use instead of a hardcoded
transform each, desktop settings to choose an anchor per surface, and
Streamer.bot `target: "control"` commands (`show`/`hide`/`clear`/`anchor`/
`reset`) that set **transient, in-memory overrides** over the saved default —
never the saved settings themselves. See `LIVE_TEST_RESULTS.md`'s Phase 4
section for what is proven and what still needs the headset.

### World-lock is deferred

`OverlayAnchor` has two modes - Controller and Head - not three. A third,
world-locked mode (the panel placed once in the room, tracked to nothing) was
considered and deliberately deferred, not forgotten:

- It needs `SetOverlayTransformAbsolute`, a different OpenVR call from the
  tracked-device-relative one every current anchor uses - a new vtable index
  with its own derivation and live cross-check, the same rigor §3's Risk 3
  already demanded for the ten indices this app depends on.
- It needs a placement mechanism. A world-locked window with no way to move
  it is worse than a controller- or head-anchored one, and grab-to-place
  needs `SetOverlayInputMethod` plus laser contention handling, which Phase 5
  gates deliberately.

So world-lock waits for Phase 5's interaction work, at which point placing a
panel and pinning it become the same feature rather than two.

---

## 10. Phase 4b — a VR settings tab, and a second renderer, deliberately

Landed per `PHASE4B_PROMPT.md` (plan: `smooth-finding-creek.md`): a
**Settings** tab in the SteamVR dashboard, reached by peer navigation
alongside the existing shortcut wizard, covering everything that can only be
judged while wearing the headset - anchor mode/hand, panel opacity and size,
on/off, and gaze sensitivity. Introduced the settings themselves too
(`ChatOpacity`, `ChatSizeScale`, `GazeSensitivity`, `NotificationOpacity`,
`NotificationSizeScale`), since Phase 4's actual scope had not shipped them
yet. See `LIVE_TEST_RESULTS.md`'s Phase 4b section for what is proven and
what still needs the headset.

### Two renderers is a decision, not an accident

`VrDashboardRenderer` (and now its new Settings page) stays GDI+. Chat and
notifications are WPF. This project now deliberately contains both, for the
same reasoning §4 gave when WPF was chosen for chat over GDI+ in the first
place: pick the renderer that fits the content, not the renderer already in
the file.

- The dashboard is a proven, hardware-tested ~1700 lines of GDI+ across three
  phases. Its pages are toggles, segmented choices and sliders - none of
  which need real text wrapping, inline coloured runs, or embedded images,
  the exact capabilities that made WPF the right call for chat.
- Rewriting a working, live-validated surface to chase renderer consistency,
  with no functional gain, is a bad trade against the phase's actual goal (a
  settings page) and its actual risk (not breaking the wizard).

**Migrating the dashboard to WPF is possible later cleanup**, worth
considering if the dashboard ever needs WPF-shaped content (rich text,
images, animation) for its own reasons - not something owed for its own
sake. Recorded here so the two renderers read as a decision made twice, once
for chat and once for the dashboard, rather than drift no one chose.

### Tabs are peer navigation, not a wizard page

The tab strip (`Shortcuts` / `Settings`) is drawn only on the wizard's entry
point (`List`) and the new `Settings` page. The five wizard sub-pages
(`GestureType`, `Tolerance`, `ActionPicker`, `RecordInput`, `Review`) were
not touched at all - not their rendering, not their click handling, not
`VrDashboardLayout`'s existing bottom-bar rows. This was the deliberate
scope boundary that kept "add a settings tab" from becoming "restructure the
proven wizard": tabs sit above the wizard's existing page stack as a
completely separate concept, rather than being woven into it.

### Live-apply reuses the shortcut-save pattern, not a new mechanism

A VR settings-page edit applies to the running `ChatOverlay`/
`NotificationOverlay` immediately - no IPC needed, since
`VrDashboardController` already runs in the OpenVR worker process on the
overlay's own thread. Persistence back to `settings.json` reuses the exact
`OpenVrWorkerMessage` → drain-queue → `BridgeEngine` event →
`TrayApplicationContext` save pattern already proven for shortcuts created
from the dashboard, deliberately without ever calling `RestartRuntimeAsync`
- the same reasoning shortcut saves already established: restarting would
tear down the surface the wearer is actively looking at.
