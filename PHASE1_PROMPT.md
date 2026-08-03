# Claude Code prompt — housekeeping + Phase 1

Model: **Opus.**

Phase 0 was self-contained greenfield with a clear contract — Sonnet territory.
Phase 1 is the opposite: low volume, high subtlety. It derives raw vtable indices
from a large C++ header where an error is an access violation inside SteamVR
rather than a failing assert, it marshals structs by reference and raw pixel
buffers across the interop boundary, and it modifies `OpenVrInput.cs`, the most
safety-critical file in the repository. Use Opus.

Paste everything below the line.

---

Read `CHAT_AND_NOTIFICATIONS_PLAN.md` in the repository root first. It is the
agreed design. Sections §3 (Risks 2 and 3), §4 and §7 govern this task.

There are two separate pieces of work here. **Do part A first and commit it on
its own.** Do not mix it into the Phase 1 commit.

---

# Part A — housekeeping before anything else

## A1. Normalise line endings (do this first)

The Phase 0 files were written with LF while the rest of the repository is CRLF.
`git status` currently reports 19 modified files when only 6 actually changed —
`OpenVrInput.cs` alone shows over 3000 changed lines that are pure line-ending
noise. Committing in this state would touch every file in the repository and
destroy `git blame` history across the whole codebase.

1. Create `.gitattributes` at the repository root:

   ```
   * text=auto eol=crlf
   ```

2. Run `git add --renormalize .`
3. Confirm with `git diff --stat` that only genuinely changed files appear.
4. Commit this on its own, e.g. `chore: normalise line endings to CRLF`.

## A2. Three small corrections to the Phase 0 code

Commit separately from A1.

- **`_state` in `StreamerBotEventStream` should be `volatile`.** It is written by
  the pump thread and read by the `State` property from the UI thread.
- **The narrow `JsonDocument` leak in `SendRequestAsync`.** If a response arrives
  between the timeout firing and the `finally` block reading
  `IsCompletedSuccessfully`, the document is neither handed off nor disposed.
  Close the window — for example by having `Dispatch` hand ownership over only
  once via a flag on the completion, or by disposing from whichever side loses
  the race deterministically.
- **Chat contents are being written to disk.** `ConsumeEventStreamAsync` logs
  every payload at `Info`, and the activity log persists as structured JSON under
  `%LOCALAPPDATA%\SteamVR2Bot\Logs` for 14 days. Viewer messages at rest is a new
  category of data the README does not currently account for. Drop payload
  *contents* to `BridgeLogLevel.Debug` and keep only a non-identifying counter or
  summary at `Info`. Then update the README's logging section to state what is
  and is not retained.

---

# Part B — Phase 1: the overlay substrate

## Scope

Build the ability to create, position and paint a **non-dashboard** overlay, and
prove it works with a static test overlay attached to the left controller.

Do **not** build the chat window, the message model, notifications, WPF
rendering, gaze detection or any interaction. Those are Phases 2–5. This task
ends with one hard-coded test overlay visible in VR and a passing self-test.

## Why this is delicate

`OpenVrInput.cs` reaches the OpenVR API through a hand-rolled function table:
`GetTableDelegate<T>(pointer, index)` against `FnTable:IVROverlay_028`. A wrong
index does not fail cleanly — it calls a different function with mismatched
arguments, which is an access violation at best and silent memory corruption at
worst.

Ten indices are currently in use and are known good, validated against real
hardware:

| Method | Index |
|---|---|
| `SetOverlayFlag` | 11 |
| `SetOverlayWidthInMeters` | 22 |
| `PollNextOverlayEvent` | 48 |
| `SetOverlayInputMethod` | 50 |
| `SetOverlayMouseScale` | 52 |
| `SetOverlayFromFile` | 63 |
| `CreateDashboardOverlay` | 67 |
| `IsDashboardVisible` | 68 |
| `IsActiveDashboardOverlay` | 69 |
| `ShowDashboard` | 72 |

## B1. Derive the new indices — method matters more than the answer

**Do not guess indices individually, and do not trust any index you cannot
justify.**

1. Obtain the `openvr.h` whose `IVROverlay` interface version string is
   `IVROverlay_028`. Confirm the version matches before reading anything from it.
2. Enumerate the `IVROverlay` virtual method declarations **in declaration
   order**, in one pass. That order is the vtable order.
3. **Cross-check against the ten anchors above.** If all ten land exactly where
   your enumeration predicts, the enumeration is trustworthy and the new indices
   derived from it are too. If even one does not match, stop — you have the wrong
   header version. Report this rather than working around it.
4. Record the derivation in a comment next to the table so the next person can
   re-verify it without repeating the work. Say which header version it came
   from.

Methods needed: `FindOverlay`, `CreateOverlay`, `DestroyOverlay`, `ShowOverlay`,
`HideOverlay`, `IsOverlayVisible`, `SetOverlayRaw`, `SetOverlayAlpha`,
`SetOverlaySortOrder`, `SetOverlayCurvature`, and
`SetOverlayTransformTrackedDeviceRelative`.

Note that `VrOverlayFunctions` is currently a positional record with ten members.
Adding eleven more makes a twenty-one-member positional record where every call
site depends on argument order — convert it to named properties or an object
initialiser rather than extending the positional list. Changing this type touches
the working dashboard path, so the existing dashboard must be re-tested.

## B2. `SetOverlayRaw`, not `SetOverlayFromFile`

The dashboard writes a PNG to disk and hands SteamVR a path. Do not extend that
pattern. `SetOverlayRaw` takes a pixel buffer directly:

```
SetOverlayRaw(ulong overlayHandle, IntPtr buffer, uint width, uint height, uint bytesPerPixel)
```

The buffer must stay alive and pinned for the duration of the call. Do not leave
the existing dashboard path broken — it can keep using `SetOverlayFromFile`.

## B3. `VrOverlaySurface`

A new type owning exactly one overlay. **It must be multi-instance** — several
surfaces will coexist in later phases (chat, notifications, and more). Do not
bake a single overlay into `OpenVrInput` the way the dashboard currently is.

Responsibilities: create and destroy, upload a raw texture, set width in metres,
show and hide, set alpha, set sort order, set curvature, and set a transform
relative to a tracked device.

Deterministic disposal: an overlay handle that outlives its owner stays in
SteamVR until the process exits.

## B4. Device index re-resolution — a known hazard

Tracked device indices are **not stable** across controller sleep, reconnect or
battery change, and roles can come back unassigned. Bind a transform to an index
that later goes stale and the overlay silently detaches with no error. Hotrian's
OpenVRTwitchChat hit this hard enough to replace SteamVR's own device manager.

Do **not** resolve the left controller's index once and cache it. Re-resolve when
a device activates or a role changes. `GetControllerRoleForTrackedDeviceIndex`
already exists in `OpenVrInput.cs` (around line 298) — reuse it. Handle the case
where no left controller is present at all without throwing.

## B5. Prove it

A hard-coded test overlay: a solid colour with a little text is enough. Generate
the pixels however is simplest — GDI+ or a hand-filled `byte[]`. **Do not
introduce WPF in this phase**; the renderer choice belongs to Phase 3.

Put it behind a setting or a debug menu item that defaults to off. It is a
development aid, not a feature.

## Hard constraints

1. **No NuGet packages.** All projects have zero `PackageReference` and that is
   deliberate.
2. **Do not modify `StreamerBotClient.cs`** or the Phase 0 event stream beyond
   the A2 corrections.
3. **Do not break the existing dashboard.** It shares the overlay function table
   you are modifying.
4. **Do not touch** input, gesture, chord, binding or `MotionSampling` logic.

## Verification — required

1. **A self-test that catches a bad index loudly at startup:** `CreateOverlay`
   with a throwaway key → `FindOverlay` returns the same handle → `DestroyOverlay`
   → `FindOverlay` no longer finds it. Add it to `TraySelfTests` in the existing
   style. This is the single most important deliverable in Phase 1 — it converts
   a silent memory-corruption failure mode into a loud startup failure.
2. All existing tests in `src/SvrBridge/SelfTests.cs` and
   `src/SvrBridge.Tray/TraySelfTests.cs` still pass.
3. Build clean, no new warnings.
4. **A manual hardware test, since this cannot be verified without a headset.**
   Write the steps out for the user to run, covering: the test overlay appears
   attached to the left controller; it stays attached through a controller sleep
   and wake; the existing SteamVR2Bot dashboard still opens and its buttons still
   work; and existing shortcuts still fire. Append the results to
   `LIVE_TEST_RESULTS.md` following that file's existing convention.

## Style

Match the codebase: file-scoped namespaces, `sealed` by default, nullable
enabled, records for data. Follow the existing XML doc convention of explaining
*why* rather than *what* — see `VrDashboardLayout` and `MotionSampling`. The
index derivation in B1 especially needs its reasoning written down.

## When you are done

Summarise what changed, state which `openvr.h` version the indices came from and
that the ten anchors cross-checked, confirm the self-tests pass, and list the
manual hardware steps for the user to run.
