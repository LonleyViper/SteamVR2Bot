# Claude Code prompt — Phase 0

Model: **Sonnet**. This is self-contained greenfield code with a clear contract.
Switch to Opus for Phase 1, which is OpenVR vtable interop where a wrong index is
an access violation rather than a test failure.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` in the repository root first. It is the
agreed design for this work. Sections §3 (Risk 1) and §5c are the ones that
govern this task.

## Scope

Implement **Phase 0 only**: a Streamer.bot event stream that receives chat and
event payloads and surfaces them in the desktop activity log.

Do **not** implement any VR overlay work, any rendering, any notification
display, or anything from Phases 1–5. Do not add overlay vtable indices. This
task ends with data visible in the desktop app's activity log, and nothing in VR.

## Background

SteamVR2Bot runs Streamer.bot actions from SteamVR controller shortcuts. It is
being extended to also *display* chat and events from Streamer.bot inside VR.
Phase 0 builds only the inbound data path.

The governing architectural principle (§5c): **Streamer.bot owns all platform
integration and filtering. This app has no Twitch, YouTube or Kick knowledge
whatsoever.** It subscribes to exactly one event — `General.Custom` — which
Streamer.bot actions emit via `CPH.WebsocketBroadcastJson(...)`. Do not add
platform-specific event subscriptions or payload parsing.

## Hard constraints

1. **`src/SvrBridge.Core/StreamerBotClient.cs` must not be modified.** It is the
   validated action-delivery path. It is constructed fresh per delivery and
   disposed immediately (see `BridgeEngine` ~140 and ~362,
   `TrayApplicationContext` ~493). Its request/response design is safe precisely
   because it never subscribes to events. Leave it alone.
   - If you need its auth handshake or `BuildAuthentication` logic, call the
     existing public static method or duplicate the handshake in the new type.
     Do not refactor shared pieces out of it — the existing `SelfTests` cases at
     `src/SvrBridge/SelfTests.cs` lines ~430–600 must keep passing untouched.
2. **No NuGet packages.** All three `.csproj` files currently have zero
   `PackageReference` entries and that is deliberate. Target frameworks are
   `net10.0` (Core) and `net10.0-windows` (Tray).
3. **Do not touch any OpenVR, input, gesture, or binding code.** Nothing in
   `OpenVrInput.cs`, `BridgeEngine.cs`, `ChordDetector.cs`, `InputProbe.cs`,
   `ControllerInputs.cs` or `MotionSampling.cs` should change.
4. **Never log the Streamer.bot password**, consistent with existing behaviour.

## What to build

### `src/SvrBridge.Core/StreamerBotEventStream.cs`

A new long-lived client, separate from `StreamerBotClient`, with the opposite
lifetime: permanently connected, reconnecting indefinitely, where a missed event
is harmless.

- Owns its own `ClientWebSocket` to the same address the action client uses.
- Performs the same authentication handshake when a password is configured.
- On connect, sends `{"request":"Subscribe","id":"...","events":{"General":["Custom"]}}`.
- One background receive loop. Frames are dispatched by shape:
  - a frame whose `id` matches a pending request → complete that request's
    `TaskCompletionSource`
  - a frame with an `event` object → write to a `Channel<StreamerBotEvent>`
  - anything else → log at debug level and drop
- Use a `ConcurrentDictionary<string, TaskCompletionSource<JsonDocument>>` for
  pending requests. Ensure entries are always removed, including on timeout,
  cancellation and socket failure — no leaks.
- Reconnect with backoff, capped and indefinite (do not reuse the action
  client's deliberately-bounded 3-attempt policy — that policy exists to avoid
  duplicate action delivery and is wrong here).
- Expose connection state so the UI can show it.
- Correct `IAsyncDisposable`: cancel the pump, complete the channel, close the
  socket, and do not throw on double dispose.

### `src/SvrBridge.Core/StreamerBotEventPayload.cs`

The payload contract from §5c. `General.Custom` carries a user-defined JSON body,
so parse defensively — this data is authored by the user in Streamer.bot and will
be malformed sometimes.

```jsonc
{
  "v": 1,
  "target": "chat" | "notification" | "control",
  "user": "", "colour": "#RRGGBB", "badge": "",
  "text": "", "title": "",
  "duration": 5000, "accent": "#RRGGBB",
  "command": ""
}
```

- Unknown fields ignored. Unknown or missing `target` → drop with a log line.
- Malformed JSON must never take down the receive loop.
- Include the `"v"` field now; treat a missing or unrecognised version as
  version 1.

### Wiring

- A `UserSettings` toggle for whether the event stream is enabled, defaulting to
  off, following the existing settings and migration patterns in
  `src/SvrBridge.Tray/UserSettings.cs`.
- Consume the channel in the tray host and write received payloads to the
  existing activity log so they are visible in the desktop window.
- Lifecycle should follow the existing app lifecycle, and must not interfere
  with shortcut delivery in any way.

## Verification — required, not optional

1. Add self-tests in the existing style covering, without a live Streamer.bot:
   - a `General.Custom` frame is parsed into a payload correctly
   - malformed JSON, an empty body, and an unknown `target` are each dropped
     without throwing and without stopping the pump
   - a response frame and an event frame interleaved on the same socket are
     routed to the right destinations
   - pending-request entries do not leak on cancellation or socket failure
2. Confirm every existing test in `src/SvrBridge/SelfTests.cs` and
   `src/SvrBridge.Tray/TraySelfTests.cs` still passes.
3. Build clean with no new warnings.

## Style

Match the existing codebase: file-scoped namespaces, `sealed` by default,
nullable enabled, records for data. Follow the existing XML doc comment
convention — the codebase explains *why* a decision was made, not what the code
does (see `VrDashboardLayout` and `MotionSampling` for the house style). Keep
that up; it is a real strength of this repo.

## When you are done

Summarise what changed, confirm the existing self-tests pass, and state exactly
what a user must do in Streamer.bot to see a payload arrive — that instruction
becomes the basis for the importable action set in Phase 4.
