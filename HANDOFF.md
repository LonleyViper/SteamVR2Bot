# Handoff — 2026-07-29, end of Phase 3 session

Supersedes the previous hand-off note (grip-fix / early Phase 2), which is
now committed history — see `LIVE_TEST_RESULTS.md` for that record. This
file is the current-state snapshot; read it before touching chat,
notifications, emotes, or badges. For full technical detail, read the
`develop-svr-bridge` skill's `references/chat-and-emotes.md` first — this
file is the summary, that one is the reference material.

## State: implemented, self-tested, live-confirmed working, committed

Phase 3 is functionally complete and confirmed working live in the headset:
chat text, colour, real Twitch/BetterTTV/FrankerFaceZ/7TV emote images, and
real multi-badge images all render correctly on real chat messages. The full
manual matrix in `LIVE_TEST_RESULTS.md` is user-confirmed `PASS` except rows
10 and 17b (see below) — including row 15, the shortcut-fires-exactly-once
product contract, which was specifically re-verified after a discrepancy
report (also below). Notifications (Phase 2) and the dashboard/shortcut path
were not knowingly broken by any of this.

This session's work is committed as of this handoff. (The grip fix and
Phase 2 from the previous hand-off were already committed before this
session started — `297ea64` and earlier.)

**Untracked planning docs** (`CHAT_AND_NOTIFICATIONS_PLAN.md`,
`PHASE0_PROMPT.md` through `PHASE3_PROMPT.md`, `VR UI Screenshots/`) were
left untracked, matching the pattern already established before this session
(Phase 1/2 plan docs were never committed either). Ask the user whether that
should change before assuming either way.

## What changed this session, roughly in order

1. Phase 2 (notifications) manual test rows recorded and committed
   (`297ea64`) — this one commit landed, everything after it did not.
2. Phase 3 chat window built per the original plan: gaze-scale visibility,
   ring buffer, throttled repaint, WPF text wrapping.
3. Plan revised mid-session (§5c/B6 of `CHAT_AND_NOTIFICATIONS_PLAN.md`):
   direct `Twitch.ChatMessage` subscription added so chat needs no
   hand-written relay action for text/colour/badges — only `General.Custom`
   remains for notifications and custom SB-side alerts.
4. Emote handling: text styling first, then real images once a live
   diagnostic confirmed Streamer.bot's `TwitchGetEmotes` request aggregates
   Twitch + BTTV + FFZ + 7TV in one response.
5. Badge images added the same way, then generalised from "one guessed
   badge" to "every badge on the message" after live testing showed Prime
   and a channel-specific badge being silently dropped.
6. A `badges` array added to the hand-authored `General.Custom` contract, and
   a `!svrtest` C# test-harness action written to exercise burst handling,
   the ring buffer cap, long-message wrapping, multi-badge rendering, an
   unknown emote, and chat/notification coexistence without needing real
   chat activity.

Several real bugs were caught and fixed along the way (integer overflow in
the repaint throttle, an emote-catalog delivery race against SteamVR worker
startup, a badge double-counting interaction) — full detail in
`chat-and-emotes.md`, not repeated here.

## Known platform limitation (not a bug, documented, not fixable here)

Streamer.bot only forwards `Twitch.ChatMessage` over its WebSocket API while
at least one local trigger of that type exists and is enabled in its own
Action system — confirmed live (chat went completely silent for 32 minutes
after the last such trigger was deleted, resumed the moment one existed
again), and confirmed that Streamer.bot's WebSocket API has no request to
create a trigger remotely. Documented in `README.md` under "Known
limitations" and in `LIVE_TEST_RESULTS.md`. A `!svrtest` command trigger
(see below) incidentally satisfies this requirement for anyone who sets it
up.

## Open problem: the `!svrtest` test action does not appear to fire

Written and handed to the user as a way to exercise burst/cap/badge/emote-
fallback/notification scenarios on demand. Live log evidence afterward showed
only sparse, minutes-apart chat arrivals matching manual typing — no 12-
message burst, no 45-message cap run, no `notification payload` line at all.
The action does not appear to have executed for any scenario tried.

**Not yet root-caused.** SVR Bridge's own log can only show what actually
arrives over the WebSocket, not why a Streamer.bot action produced nothing.
Next step is checking Streamer.bot's own action execution log/history for
`SVR Bridge Test Harness` (or whatever it was named) for a compile error, a
trigger that never fired, or a silent exception — none of which SVR Bridge
can see from its own side. The action's source is in this session's
transcript; it was not committed to any repo file. If it needs rewriting,
the earlier lesson about `CPH.WebsocketBroadcastJson` requiring a
pre-serialized JSON string (not a raw object) on this Streamer.bot version
still applies.

## Outstanding manual matrix rows

The full matrix is now clear except two rows — everything else in
`LIVE_TEST_RESULTS.md`'s Phase 3 table is user-confirmed `PASS`, including
row 15 (shortcut fires exactly once — the product contract). Still "not
run":

- **Row 10** — more than 40 messages in one session (ring buffer cap and
  eviction). Needs sustained volume; the `!svrtest cap` command was meant to
  cover this but the action isn't confirmed working yet (see below).
- **Row 17b** — an emote sent within the first second or two of connecting,
  before the catalog fetch/first image download can have finished, to prove
  the styled-text→real-image upgrade path specifically (not just the
  steady-state case, which is confirmed). Timing-sensitive; also a natural
  fit for the test harness once it works.

### Row 15 — a real discrepancy investigated and resolved

The user triggered a shortcut twice; Streamer.bot reported 22 executions of
the target action. SVR Bridge's own log showed exactly two
`streamerbot.action_confirmed` lines, one per physical press, each within
milliseconds of its `controller.input` press event — no duplicate or extra
`DoAction` request on this app's side, which is architecturally consistent
with the one-request/one-acknowledgement design (there is no code path that
sends a request without logging a matching confirmation). The other 20
executions were attributed to the target action having other triggers
configured in Streamer.bot, unrelated to this shortcut - not independently
verified via Streamer.bot's own execution history, but the SVR Bridge side
of the evidence is unambiguous. If this surfaces again, check Streamer.bot's
per-execution trigger source before suspecting this app.

## Recommended next steps, in order

1. Diagnose the `!svrtest` action via Streamer.bot's own execution log — it
   never appeared to fire for any scenario tried. Fixing it closes rows 10
   and 17b (and the untracked planning docs / commit-scope question below is
   already resolved, see above).
2. Close rows 10 and 17b once the harness works.
3. Decide whether the untracked planning docs (`CHAT_AND_NOTIFICATIONS_PLAN.md`,
   `PHASE0_PROMPT.md`–`PHASE3_PROMPT.md`, `VR UI Screenshots/`) should be
   committed too, or stay out of the repo as before.
4. Only after that: decide whether Phase 4 (desktop appearance/placement
   settings, the importable Streamer.bot action-set for setup) is still the
   right next phase, or whether the `!svrtest`-style harness suggests
   something else is more valuable first.

## Environment notes carried forward

- `dotnet` is not reliably on `PATH` in every session — locate it under
  `%USERPROFILE%\.dotnet\dotnet.exe` (this session installed the SDK there)
  or `C:\Program Files\dotnet` (runtime-only muxer, no SDK) if a fresh
  environment needs it again.
- Debug build: `src\SvrBridge.Tray\bin\Debug\net10.0-windows\SteamVR2Bot.exe`.
  Published: `artifacts\publish\SteamVR2Bot.exe` — this is the one SteamVR
  auto-launches, and the one every live test in this session actually ran.
- Logs: `%LOCALAPPDATA%\SteamVR2Bot\Logs\svr-bridge-YYYYMMDD.jsonl`.
- Before any comparative test, confirm zero stray instances are running
  (`taskkill /F /IM SteamVR2Bot.exe` or the PowerShell equivalent) — a
  locked DLL from a leftover process will fail the next build.
