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
