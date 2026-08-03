# Agent instructions for SteamVR2Bot

This is the canonical repository guide for Codex and other coding agents.
`CLAUDE.md` points here so Claude and Codex use the same project contract.

## Start here

1. Read `docs/current-state.md`.
2. Read `docs/project-map.md` and choose the smallest relevant area.
3. Inspect only the files listed for that area and the directly related tests.
4. Read the linked historical/live evidence only when the task needs it.

Do not read the full `LIVE_TEST_RESULTS.md`, every phase prompt, or every
handoff for an ordinary code task. Use `docs/live-tests/index.md` to route to
the relevant evidence.

## Product contract

- Preserve the direct route: SteamVR input -> detector -> Streamer.bot WebSocket.
- Keep the tray host separate from the disposable OpenVR worker.
- Keep all OpenVR calls on the worker's owning thread.
- Keep shortcut saves/deletes hot; do not reboot the worker for configuration.
- Keep desktop and in-VR setup paths.
- Store stable Streamer.bot action IDs, not display names.
- Keep credentials out of source, logs, diagnostics, and command output.
- Do not call an untested controller family hardware-validated.

## Evidence rules

- Automated tests establish regression safety, not headset usability.
- For input misses, trace raw edge -> detector -> WebSocket request -> ack.
- Distinguish dashboard focus, overlay rendering, worker recovery, detector,
  and Streamer.bot delivery failures.
- Do not infer a VR fix from process survival or a clean log alone.
- Preserve unrelated and user-owned changes in the working tree.

## Task routing

Use `scripts/Get-AgentContext.ps1 -Area <area>` to print a small context
manifest. Valid areas are `general`, `input`, `dashboard`, `chat`,
`notifications`, `settings`, and `packaging`.

Before editing, state the smallest file set and the acceptance check. Make the
smallest coherent change, add a deterministic self-test where practical, then
run the relevant build/self-tests. Headset-facing changes still require a
live headset check before being declared complete.

## Historical material

Root-level `PHASE*_PROMPT.md`, `FIX_*_PROMPT.md`, `HANDOFF.md`, and
`PHASE7_HANDOFF.md` are preserved working history. They are not default
context. Prefer the current-state brief and the routing index first.
