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

## Streamer.bot 1.0.5 compatibility

Audited 2026-08-07 against the published v1.0.5 changelog and schema. Code
reviewed and adjusted; **not yet confirmed against a running 1.0.5 instance.**

- Twitch chat moved IRC -> EventSub and the legacy `message` wrapper was
  removed. `TwitchChatMessageMapper` already preferred the unwrapped shape, so
  that branch simply stops matching; it is kept for instances still on 1.0.4.
- `cheerEmotes` and the root-level `subscriber` flag are gone from the 1.0.5
  schema. Both are still read, because a stale instance still sends them.
- `parts` (EventSub fragments) is deliberately not read. `emotes` still carries
  the ranges and image URLs; the derived part types are undocumented.
- Chat field reads are now case-insensitive (`ChatPayloadJson`). The 1.0.5
  schema documents `Emote` as `Name`/`StartIndex`/`ImageUrl` while every
  sibling type is camelCase; being wrong would silently drop every emote image.
- Requests are unchanged: `Hello`/`Authenticate`/`Subscribe`/`GetEvents`/
  `GetActions`/`DoAction` all still apply. The Kestrel move is server-side.

Open items for a live 1.0.5 session, in order:

1. Read the `streamerbot.chat_shape` Debug line - the first chat message per
   connection logs its field names (never its content) - and diff it against
   what the mapper reads.
2. Confirm emote and badge images still render in the wrist overlay.
3. Watch for a `streamerbot.events_unreported` warning: 1.0.5 moved Twitch
   Watch Streaks to EventSub, and a renamed event silently kills a saved alert.

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
