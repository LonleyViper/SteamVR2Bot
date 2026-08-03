# Claude Code prompt — default bindings for Index and Quest controllers

Model: **Sonnet.** SteamVR binding JSON plus a per-family input table. The
subtleties are in the binding format and in what each controller actually has,
not in the code.

Paste everything below the line.

---

## Scope

Ship working default bindings for **Valve Index** (`knuckles`) and **Meta Quest /
Touch** (`oculus_touch`) controllers, alongside the existing validated Vive
preset.

Read the README's "Controller inputs" section first — it sets a deliberate stance
about not labelling untested presets as validated, and that stance must survive
this change.

## B1. Fix the existing bug first — the picker offers inputs that cannot work

`ControllerInputs.AvailableInputs` returns `[1, 2, 33, 32, 7, 34]` for any
non-Vive controller: menu, grip, trigger, thumbstick/trackpad, **A/X (7)** and
**B/Y (34)**.

`actions.json` contains **no actions for buttons 7 or 34.** It has only
`button_one`, `button_two`, and menu/grip/trigger/trackpad per hand.

So on an Index or Quest today, the VR picker lists the A and B buttons, nothing
is bound to them, pressing them sets no bit, and live recording waits forever.
`FriendlyName` already names them for both conventions — the actions were never
added.

**Add the missing boolean actions** — A/X and B/Y per hand — and make sure the
bitmask they feed uses the button numbers `FriendlyName` and `AvailableInputs`
already expect (7 and 34). Do not renumber; the existing naming logic is correct.

## B2. Per-family input lists

The current split is binary — Vive gets four buttons, everything else gets six.
That is wrong in both directions:

| Family | Controller type | Inputs that actually exist |
|---|---|---|
| Vive wand | `vive_controller` | menu (1), grip (2), trigger (33), trackpad (32) |
| Index | `knuckles` | grip (2), trigger (33), thumbstick (32), A (7), B (34) — **no menu** |
| Quest / Touch, left | `oculus_touch` | menu (1), grip (2), trigger (33), thumbstick (32), X (7), Y (34) |
| Quest / Touch, right | `oculus_touch` | grip (2), trigger (33), thumbstick (32), A (7), B (34) — **no menu** |

Index has no menu button; A and B replace it. On Touch, only the left controller
has an application menu — the right controller's equivalent is the reserved
Oculus/system button and must not be offered.

`AvailableInputs` already takes `hand`, so the Touch asymmetry is expressible
without changing its signature. Never offer an input the connected controller
does not physically have.

An unrecognised controller type should fall back to a conservative set that is
likely to exist everywhere — grip, trigger, thumbstick — rather than the current
six.

## B3. The binding files

Add `bindings_index_controller.json` and `bindings_oculus_touch.json` alongside
the existing Vive file, and register both in `actions.json`'s `default_bindings`
(currently `vive_controller` only). Copy the Vive file's structure rather than
inventing one — it is known to load.

Keep the validated preset shape: **`button_one` = left grip, `button_two` =
right trigger**, so the documented "hold left grip, then press right trigger"
gesture behaves identically across families.

### The one that will silently not work: analog grips

**The Vive grip is a physical button. Index and Touch grips are analog.** A
boolean action bound to an analog source needs a click threshold in the binding
JSON, or it will never fire and there will be nothing in any log to explain why.

- **Index** exposes grip force and value. Use whichever your binding structure
  supports with a threshold, and pick a value that a firm squeeze reaches
  comfortably — too high and users cannot trigger it, too low and it fires while
  merely holding the controller.
- **Touch** exposes an analog grip value. Same treatment.

Check the trigger too: on both families it is analog with a click at full pull,
so bind the click rather than thresholding the axis if a click source exists.

Also copy the binding files to the output the same way
`bindings_vive_controller.json` is copied — check the `.csproj` `None Include`
entries and add the new files, or they will be missing from the published build
and nothing will work outside a dev run.

## B4. Do not claim these are validated

The README currently states that the Vive preset is live-validated and that other
families are detected but carry no tested default. That honesty is deliberate.

- Update the README to say Index and Quest defaults are now **provided**, and
  state plainly that they are **not yet hardware-validated**.
- Keep the existing advice to verify recorded input names and run the live matrix
  before treating either as proven.
- Do not describe them as tested or validated anywhere — commit messages
  included — until a live matrix says so.

## Hard constraints

1. No new NuGet packages.
2. **Do not change the Vive binding or its behaviour.** It is the only
   hardware-validated preset and it must be byte-for-byte unaffected.
3. **Do not modify** `StreamerBotClient.cs`, the overlay/render code, or
   `MotionSampling`.
4. **Do not break** shortcut delivery, chat, notifications, or the dashboard.
5. Match existing style; XML docs explain *why*.

## Verification

Neither controller can be tested without the hardware, so be explicit about what
is and is not proven.

1. **Self-tests:**
   - `AvailableInputs` returns the correct set for `vive_controller`, `knuckles`,
     and `oculus_touch` on **each hand** — including that menu is absent for
     Index and for Touch's right hand.
   - An unknown controller type falls back to the conservative set.
   - `FriendlyName` gives Index A/B and Touch X/Y the right labels per hand.
   - Every action in `actions.json` has a binding entry in each of the three
     binding files, and every binding references an action that exists — a
     mismatch here is the silent failure mode.
2. Both binding files parse as valid JSON and load without SteamVR reporting a
   manifest error.
3. All existing tests pass. Build clean, no new warnings.
4. **Confirm the new binding files are present in `artifacts\publish`** after
   `scripts\Publish-Poc.ps1`. A missing copy step is the most likely way this
   ships broken.
5. **Vive regression on real hardware:** the existing preset still records and
   still fires exactly once.
6. Add an **unrun** matrix to `LIVE_TEST_RESULTS.md` for Index and Quest, marked
   "not run", listing what a tester with that hardware needs to check: each
   offered input records correctly, the grip threshold is reachable but not
   accidental, and the left-grip-plus-right-trigger preset fires once.

## When you are done

Summarise what changed, state the grip thresholds chosen and why, confirm the
binding files reach the published output, and confirm nothing anywhere describes
the new presets as validated.
