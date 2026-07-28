# Motion gesture triggers — design

Goal: fire Streamer.bot actions from physical motion, not just button edges.
Worked examples: flapping both arms with the triggers held; sweeping a hand
sideways to skip a track.

The delivery route does not change:

```text
SteamVR
  -> SteamVR2Bot detector
  -> Streamer.bot WebSocket DoAction
```

No keyboard emulation, OpenVR2Key, SISR, browser overlay, or controller driver.

## 1. Where the data comes from

Motion uses `IVRSystem.GetDeviceToAbsoluteTrackingPose`, **not** IVRInput pose
actions. Three reasons, in order of importance:

1. **Poses are a tracking query, not input.** The focused-dashboard
   investigation established that SteamVR withholds *input* while its system
   dashboard owns focus. Pose actions run through the same action system that
   starves, so they would inherit that failure. Device poses do not.
2. **No manifest or binding changes.** Pose actions would need new entries in
   `actions.json`, new `poses` entries in every controller binding file, and a
   re-bind by anyone with a saved custom layout. The system query needs none.
3. **One call returns everything.** Head and both controllers arrive in a
   single array, already carrying driver-computed velocity and angular
   velocity.

### Vtable safety

The earlier `IsInputAvailable` work stalled because its index sits *past* the
last empirically validated entry in `VrSystemFunctions`. This one does not.
`GetDeviceToAbsoluteTrackingPose` is index 12;
`GetStringTrackedDeviceProperty` is index 28 and is proven correct every time
the app displays a controller family. Everything between is inside that
validated prefix. Same argument, opposite conclusion — which is why one was
safe to wire and the other was not.

### Velocity

`TrackedDevicePose_t` carries `vVelocity` and `vAngularVelocity` from the
driver. Use them. Differencing successive positions in a `Task.Delay` loop
would fold the poll loop's jitter — Windows timer granularity is about 15.6 ms
unless raised — straight into the signal a recognizer depends on. Every sample
also carries its own timestamp rather than assuming a fixed interval.

Prediction is passed as `0`. A predicted pose is smoothed towards where the
device is expected to be, which blunts exactly the direction changes a
recognizer looks for.

## 2. Body frame

Raw poses are in play-space coordinates. "Sweep right" has to mean right
relative to the wearer, not relative to the room, or the gesture breaks the
moment they turn around.

`BodyFrame` re-expresses hands relative to the head: **+X right, +Y up, +Z
ahead**, origin at the headset.

The important subtlety is that it uses **yaw only**. If pitch and roll were
included, glancing down mid-gesture would rotate the gesture space and a level
sweep would read as a diagonal one. Looking straight up or down leaves no yaw
in the forward axis at all, so the frame falls back to the head's own up axis,
which still carries the facing. Both cases are covered by `TestBodyFrame`.

`HeadHeightMeters` rides along on every sample so thresholds can scale to the
wearer instead of being tuned for one person's arms.

## 3. Segmentation — the part that decides whether this works

Continuous recognition without an explicit start and end is where gesture
systems die. The recognizer has no idea whether an arm movement was a command
or the user reaching for a drink, so it either misses commands or fires during
gameplay. On a live stream the payload is a scene change, so a false positive
costs far more than a miss.

**Every gesture is armed by an input.** Hold a button, move, release. This is
already how the worked example is phrased — "flapping both arms *with triggers
pulled*" — and it gives three things at once:

- Segmentation is free. Start and end are unambiguous.
- False positives during gameplay drop to roughly zero.
- It reuses the existing binding vocabulary, so a gesture is just a shortcut
  with a motion requirement attached.

The arming input still flows through the action system, so it is subject to the
focused-dashboard limitation. In practice that does not bite: nobody performs
an arm gesture while pointing a laser at a dashboard menu.

An always-on mode stays possible later. The ring buffer and recognizers are
designed to run continuously; only the trigger condition would change.

## 4. Recognizers

### First slice: hand-tuned predicates

State machines over body-frame velocity. Explainable, cheap, debuggable, no
training data, and they cover both worked examples.

**Oscillation (flap).** Band-limit each hand's vertical velocity, count
zero crossings whose peak amplitude clears a threshold, and require N crossings
inside a window. Both hands may be required in phase (flapping) or anti-phase
(running). Parameters: hands, minimum cycles, minimum peak speed, minimum
amplitude, window.

**Directional swipe (media control).** Peak speed above a threshold,
displacement above a threshold, and a dominant-axis ratio — for a rightward
sweep, `|Δx|` greater than roughly twice `max(|Δy|, |Δz|)` — with total
duration inside a range. The axis ratio is what stops a general arm wave from
registering as a swipe. Parameters: hand, axis, direction, minimum
displacement, minimum peak speed, duration range.

**Hold still.** Speed below a threshold for N milliseconds. Useful on its own
and as a terminator for other gestures.

### Later: user-recordable templates

`$P` point-cloud or Protractor matching over a resampled, scale-normalised
path projected onto the body frame's right/up plane. Far more flexible, but it
needs normalisation, confidence thresholds, and an in-VR recording flow before
anything works at all. It slots behind the same config shape, so predicates
shipping first costs nothing later.

## 5. Configuration shape

`ChordMode`'s numeric ordering is a hard constraint —
`Simultaneous=0, Modifier=1, LongPress=2, DoublePress=3, SinglePress=4`.
**Do not add motion kinds to that enum.** Inserting a value would renumber
saved shortcuts; appending would work but conflates two orthogonal ideas.

Instead `ShortcutConfig` gains an optional, independent field:

```csharp
public MotionConfig? Motion { get; init; }
```

A shortcut with `Motion == null` behaves exactly as today. A shortcut with one
set fires only when the chord *and* the motion both satisfy. Existing saved
settings deserialise unchanged.

```csharp
public sealed record MotionConfig
{
    public MotionKind Kind { get; init; }          // Oscillate | Swipe | HoldStill
    public MotionHands Hands { get; init; }        // Left | Right | Either | Both
    public MotionAxis Axis { get; init; }          // Right | Up | Forward
    public int Direction { get; init; }            // -1, 0 (either), +1
    public float MinAmplitudeMeters { get; init; }
    public float MinPeakSpeed { get; init; }       // metres per second
    public int MinCycles { get; init; }
    public int WindowMs { get; init; }
    public int MinDurationMs { get; init; }
    public int MaxDurationMs { get; init; }
}
```

## 6. Where the code runs

**Recognition must live inside the OpenVR worker process.** The worker sends
snapshots to the tray over stdout only when they change; poses change on every
poll, so routing raw motion through that pipe would push roughly 90 messages a
second at the parent and drown the command channel.

The worker samples, buffers, recognises, and emits a single
`gestureRecognized` message. The child-worker boundary, the one-request /
one-acknowledgement contract, release latching, and stable Streamer.bot action
IDs are all unaffected.

```text
GetDeviceToAbsoluteTrackingPose
  -> BodyFrame                      (yaw-only, head-relative)
  -> MotionSample ring buffer       (~3 s, fixed size, no per-sample allocation)
  -> arming input edge from the action system
  -> predicate recognizer
  -> gestureRecognized over stdout
  -> existing shortcut dispatch -> Streamer.bot DoAction
```

## 7. Tuning without living in the headset

This is the difference between a week of work and a month. Thresholds cannot be
tuned by putting the headset on for every change.

**Capture traces.** While armed, the worker buffers samples anyway. On release
it emits the whole window as one message — roughly 200 samples of about 60
bytes, so about 12 KB, which the stdout pipe handles comfortably as a single
payload. The tray writes it to the existing structured JSONL log directory.

**Replay offline.** Self-tests feed recorded traces through the recognizer and
assert what should and should not fire. Tuning becomes a deterministic edit-run
loop, and every past false positive becomes a permanent regression test.

Collect traces for the obvious negatives too: reaching for a drink, adjusting
the headset, gesticulating while talking, and normal gameplay in a fast game.

## 8. Validation

Per gesture, in the headset:

| Test | Attempts | Required result |
|---|---:|---|
| Deliberate gesture, standing still | 20 | 20 fires, 0 misses, 0 duplicates |
| Deliberate gesture, mid-game | 20 | 20 fires, 0 misses, 0 duplicates |
| **False-positive soak** — play normally, gestures armed | 30 min | 0 spurious fires |

The soak is the one that actually matters and the one easiest to skip. A
recognizer that scores 20/20 and fires twice an hour unprompted is worse than
no recognizer, because it goes out live.

Also confirm: tracking loss mid-gesture aborts rather than fires; a gesture
fires once per performance, not once per frame while the predicate holds; and
the cooldown behaves like the existing chord cooldown.

## 9. Open questions

- **Haptic confirmation.** `TriggerHapticVibrationAction` sits at index 23 in
  `VrInputFunctions`, inside the prefix validated by working `OpenBindingUi`.
  A short buzz on recognition would transform the feel, since a gesture
  otherwise gives no feedback until the action lands. Worth doing early.
- **Arming input on Vive wands.** Holding a trigger while flapping is awkward.
  Grip may be the better default for whole-arm gestures; trigger is fine for
  hand-scale sweeps.
- **In-phase versus anti-phase flapping.** Both are natural. Probably a
  per-gesture option rather than a guess.
- **Fatigue.** Gestures should not become the path for frequently used actions.
  Worth saying in the UI rather than discovering later.
- **Recording UX.** Motion recording requires the dashboard closed, which
  sidesteps the focused-dashboard problem entirely — a countdown plus a haptic
  marking start and stop is likely enough.

## 10. Status

Implemented now (probe slice):

- `MotionSampling.cs` — `BodyFrame`, `MotionSample`, `MotionTracking`. Pure
  maths, covered by `TestBodyFrame`.
- `OpenVrInput` — pose sampling through `GetDeviceToAbsoluteTrackingPose`,
  cached controller indices with cheap revalidation, blittable pose array so
  the 90 Hz path pins instead of copying.
- `InputProbe.ObserveMotion` — tracking-validity transitions immediately, hand
  positions and speeds on the once-per-second summary.

Not built yet: the ring buffer, arming, recognizers, trace capture, config
shape, and UI. Next step is a headset run confirming the summary line reports
sane body-frame numbers — hands roughly ±0.3 m either side, ~0.4 m ahead when
resting, and speeds near zero when still — before anything is built on top.
