# Current state

Orientation snapshot: 2026-08-02. Verify the repository and logs before
making claims about a new live result.

## Baseline

- Branch at the snapshot: `main`
- Latest known commit: `d399235 feat: add chat gaze calibration`
- Projects: `SvrBridge.Core`, `SvrBridge.Tray`, and the diagnostic `SvrBridge`
- Runtime boundary: persistent tray host plus disposable OpenVR worker
- Delivery route: SteamVR input -> detector -> Streamer.bot WebSocket

## Implemented and validated

- Vive shortcut route and Streamer.bot action delivery
- Tray application, protected settings, reconnect/recovery, and structured logs
- Multi-shortcut manager and in-VR shortcut wizard
- Chat, emotes, badges, notifications, VR settings, anchors, and live apply
- Chat laser placement, persistence, gaze behaviour, and visibility rules

## Streamer.bot 1.0.5 - 1.0.7 compatibility

Audited 2026-08-07 against the published changelogs and event schemas. Code
reviewed and adjusted; **not yet confirmed against a running instance.**

1.0.7 is a hotfix covering 1.0.5 and 1.0.6, and its changelog page is
cumulative - 1.0.6 has no page of its own. The schema pages this work was
audited against were already the 1.0.7 ones (they carry the `broadcaster`
property 1.0.7 says it added), so the mapper covers 1.0.7, not just 1.0.5.

- Twitch chat moved IRC -> EventSub and the legacy `message` wrapper was
  removed. `TwitchChatMessageMapper` already preferred the unwrapped shape, so
  that branch simply stops matching; it is kept for instances still on 1.0.4.
- `cheerEmotes` and the root-level `subscriber` flag are gone from the current
  schema. Both are still read, because a stale instance still sends them.
- `parts` (EventSub fragments) is deliberately not read. `emotes` still carries
  the ranges and image URLs; the derived part types are undocumented.
- Chat field reads are case-insensitive (`ChatPayloadJson`). The schema
  documents `Emote` as `Name`/`StartIndex`/`ImageUrl` while every sibling type
  is camelCase; being wrong would silently drop every emote image.
- Requests are unchanged: `Hello`/`Authenticate`/`Subscribe`/`GetEvents`/
  `GetActions`/`DoAction` all still apply. The Kestrel move is server-side.
- 1.0.7 added `broadcaster` to most Twitch payloads. Additive, and the
  notification template chain deliberately does **not** resolve `broadcaster.*`:
  it names the channel owner rather than whoever triggered the alert, so
  falling through to `"Someone"` is the better wrong answer.
- 1.0.7 made Twitch emote handlers toggleable at runtime. Turning them off
  means an empty `emotes` array, which renders as plain text rather than
  failing - the same path as a message with no emotes.

**Operational, not a code issue:** 1.0.7 changed the Twitch and Kick
authentication handlers and added a Kick scope requiring a re-login. A wearer
who updates without re-authenticating gets no chat events at all, which looks
exactly like this app breaking. Check Streamer.bot's own platform connections
before investigating the bridge.

Open items for a live session, in order:

1. Read the `streamerbot.chat_shape` Debug line - the first chat message per
   connection logs its field names (never its content) - and diff it against
   what the mapper reads.
2. Confirm emote and badge images still render in the wrist overlay. 1.0.7 also
   fixed Twemoji images not always being set, which feeds the same `imageUrl`.
3. Watch for a `streamerbot.events_unreported` warning: 1.0.5-1.0.7 moved Watch
   Streaks, SubCounter and Credit handling to EventSub, and a renamed event
   silently kills a saved alert.

## Important limits

- Vive is the only hardware-validated controller preset.
- Index and Quest/Touch presets are provided but require live hardware matrices.
- SteamVR dashboard focus deactivates action input; live capture closes the
  dashboard and returns to Review. Do not describe this as a detector failure.
- Overlay and input changes require headset validation, not only self-tests.

## Where to look next

Use `docs/project-map.md` for file routing and `docs/live-tests/index.md` for
evidence routing. Treat `README.md`, `NEXT_PHASE_PLAN.md`, and
`LIVE_TEST_RESULTS.md` as detailed references, not default orientation files.
