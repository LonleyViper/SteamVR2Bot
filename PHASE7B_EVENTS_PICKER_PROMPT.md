# Claude Code prompt — commit Phase 6/7, then rebuild the events picker

Model: **Opus**, as insurance — two previous attempts at this UI were rejected
live. The design is now specified rather than left open, which was the actual
gap, but the WinForms performance characteristics are subtle enough to be worth
the headroom.

Paste everything below the line.

---

Read `PHASE7_HANDOFF.md` first. It records two live-tested design reversals, and
re-making either would cost a headset session.

# Part A — commit first, before touching any code

The working tree currently holds **two phases**: the Phase 6 VR tab restructure
(declared done, never committed) and all of Phase 7. Eighteen modified files. If
anything breaks now, there is no way to tell which phase caused it.

1. **Split them if the files separate cleanly** — Phase 6 is the
   `VrDashboard*` tab work, Phase 7 is notifications. If Phase 7 built on the
   Notifications tab too heavily to untangle, commit as one and say so in the
   message. Do not spend an hour on archaeology.
2. **Commit the working Phase 7 features regardless.** §B1 positioning frame,
   §B3 animations, §B4 colours/duration/opacity, §B5 PNG template are all built
   and live-tested. They should not stay hostage to the picker.
3. **Hide the current events picker behind a developer flag or disable it**
   before committing, so nothing ships in the state the user rejected as
   unusable.
4. Re-verify the `SuspendLayout` freeze fix only if the current picker survives
   Part B. If Part B replaces that UI wholesale, the fix becomes moot — do not
   spend a live session verifying code you are about to delete.

# Part B — rebuild the picker, inverted

## The problem with both previous attempts

Both showed the **available** events. Twitch alone exposes around 187. Asking
someone to browse that to find "Follow" is bad UI regardless of whether it is
built from `FlowLayoutPanel` checkboxes or a `ListView` with group headers, and
the freeze was a symptom of building that many live controls.

**Invert it. Show what is enabled; search to add.**

A user has perhaps four alerts on. That short list is what they look at day to
day. Adding one is a search, not a browse — and search results are capped, so
the control count never grows past a couple of dozen. The freeze cannot recur by
construction rather than by remembering to suspend layout.

## The approved design

Two stacked sections in the Notifications settings:

**1. "Alerts shown in the headset"** — the enabled list. One row per enabled
event: a platform chip or icon, the event's name, and an X to remove it. Empty
state invites the user to search below rather than apologising.

**2. "Add an alert"** — a search box over a results list. Each result row: the
same platform chip or icon, the event name, and an **Add** button. Rows already
enabled show "added" instead of a button, greyed, rather than vanishing —
otherwise searching "follow" after adding Twitch Follow looks like it failed.

Below the results, a count: `4 of 187 events match. Keep typing to narrow.`

### Performance rules, non-negotiable

- **Cap rendered results** — around 20 rows. The count line tells the user to
  narrow rather than silently truncating.
- **Filter the data, then render.** Never build a control per event and toggle
  `Visible`. That is what froze.
- **Debounce the search** by ~150 ms so a fast typist does not trigger a rebuild
  per keystroke.
- Prototype against a realistic 187-event list and confirm responsiveness
  **before** wiring persistence.

## B0. Log one real `GetEvents` response first

`StreamerBotEventCatalog`'s doc comment records that `GetEvents` has no
documentation on Streamer.bot's site — its reference and events pages are both
marked "Documentation Needed" — so the parsed shape (`{"events": {"Twitch":
["Follow", ...]}}`) was **inferred** from the `Subscribe` request's documented
argument, not observed. The parser hedges by also reading `name`/`type`
properties in case the real shape is richer.

Nobody has looked at a live response. Before building anything:

1. Log one complete raw `GetEvents` response to the activity log or a scratch
   file.
2. **Confirm the inferred shape is correct.** If the real response is richer, the
   parser is currently discarding fields.
3. **Check specifically whether it carries any icon, image or display-name
   fields.** It almost certainly does not — platform logos are Streamer.bot's own
   embedded UI assets, not API data — but confirming costs nothing and settles
   where the icons in the picker have to come from.
4. Record the observed shape in the parser's doc comment, replacing the inference
   with an observation and noting the Streamer.bot version it came from.

This also de-risks everything below: the picker is built on that response.

## Platform icons

The user has approved platform icons — Streamer.bot uses them in its own chat
and event viewer, so there is precedent.

**Assume they must be embedded resources**, unless B0's live response proves
otherwise. Streamer.bot's API vends event data, not its own UI chrome. Note the
contrast with chat, where image references genuinely do arrive in the payload —
Twitch badges as URLs, emotes via `TwitchGetEmotes` — which is why
`ChatImageCache` exists. That pattern covers message *content*, not platform
identity, so do not go looking for a URL to reuse here.

**But the lookup must not become a hardcoded platform list**, which is precisely
what §5c's principle warns against. Required shape:

- Try to resolve an icon for the `Source` string that `GetEvents` actually
  reports.
- **Fall back to a coloured text chip** carrying the source name when there is no
  icon. A source nobody anticipated must render correctly and group correctly,
  with no special-casing and no crash.
- Icons are **embedded resources, never a network fetch.** This app has no image
  assets today, so this is a new pattern — keep them small.
- Colour is assigned deterministically from the source string via a small fixed
  palette, so an unknown source still gets a stable, readable chip.

Test the fallback with a fabricated source name that has no icon. That is the
test that proves no hardcoded list crept in.

## Event names

Check a live `GetEvents` response before building any translation layer. The
handoff notes Streamer.bot's own names are already close to readable
("Subscription", "Gift Subscription", "Channel Point Reward Redemption"). If
they are, display them as-is — a mapping table would be both unnecessary work
and a hardcoded platform list in disguise.

## Hard constraints

1. No new NuGet packages.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break** the working Phase 7 features, chat, the dashboard, the
   shortcut wizard, or shortcut delivery.
4. `General.Custom` stays subscribed unconditionally — it is this app's own
   contract, not a platform event, and Phase 2's notifications depend on it.
5. Nothing enabled by default. A user upgrading must not suddenly receive alerts
   they never chose.
6. Match existing style; XML docs explain *why*.

## Verification

1. **Self-tests:** search filters correctly and caps its result set; the enabled
   list persists and round-trips; adding and removing rebuilds the `Subscribe`
   request; an unknown source resolves to a chip rather than throwing; the
   enabled list survives a `GetEvents` response that no longer contains a
   previously enabled event — it must not be silently dropped.
2. All existing tests pass. Build clean, no new warnings.
3. **Desktop check before any headset time:** load a realistic 187-event list,
   type in the search box, add and remove several events. No freeze, no blank
   scroll region.
4. **Then the headset rows 7–10** from the Phase 7 matrix in
   `LIVE_TEST_RESULTS.md`, which are still "not run" — an enabled event produces
   a notification, a disabled one does not, and the selection survives a
   restart.

## When you are done

State what was committed and how it was split, confirm the picker holds up at
187 events, and confirm no hardcoded platform or event names exist in production
code — only in test fixtures.
