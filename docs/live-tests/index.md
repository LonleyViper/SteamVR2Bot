# Live-test evidence index

Use this index to select evidence. The full `LIVE_TEST_RESULTS.md` is a
large historical record and should not be loaded for unrelated work.

| Question | Read first | Then inspect |
|---|---|---|
| Did a gesture reach Streamer.bot? | `GESTURE_TRIGGERS.md` | input/delivery sections of `LIVE_TEST_RESULTS.md` |
| Does dashboard focus block recording? | `FOCUSED_INPUT_PROBE.md` | focused-input sections of `LIVE_TEST_RESULTS.md` |
| Did a dashboard page disappear or blink? | `HANDOFF.md` | dashboard sections of `LIVE_TEST_RESULTS.md` |
| Do overlay laser, placement, or transforms work? | `HANDOFF.md` | Phase 5 sections of `LIVE_TEST_RESULTS.md` |
| Do chat/emotes/badges work? | `CHAT_AND_NOTIFICATIONS_PLAN.md` | chat sections of `LIVE_TEST_RESULTS.md` |
| Do notifications/templates work? | `PHASE7_HANDOFF.md` | notification sections of `LIVE_TEST_RESULTS.md` |
| Is a controller family validated? | `README.md` controller section | hardware matrix in `LIVE_TEST_RESULTS.md` |
| Did packaging or SteamVR registration work? | `README.md` and `scripts/` | packaging sections of `LIVE_TEST_RESULTS.md` |

## Evidence discipline

For an input issue, record the chain:

```text
physical attempt -> raw input edge -> detector fire -> request ID -> acknowledgement
```

For a VR UI issue, distinguish texture load, overlay handle, dashboard
activation, worker identity, input ownership, and renderer state. A clean log
does not prove that the headset interaction worked.
