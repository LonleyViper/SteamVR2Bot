# Claude Code prompt — fix the WPF native DLL crash in published builds

Model: **Sonnet.** The diagnosis is complete. This is a packaging fix plus a
failure-handling improvement.

Paste everything below the line.

---

## The bug

A test user's published build crashes whenever WPF is first used. From their log:

```
System.DllNotFoundException: Dll was not found.
   at MS.Internal.WindowsBase.NativeMethodsSetLastError.SetWindowLongPtrWndProc(...)
   at MS.Win32.UnsafeNativeMethods.CriticalSetWindowLong(...)
   at MS.Win32.HwndSubclass.HookWindowProc(...)
   at MS.Win32.HwndSubclass.SubclassWndProc(...)
```

The SteamVR input worker dies, the app reconnects, the user retries, it crashes
again. The log shows this cycling at 2s then 5s backoff with no explanation
reaching the user.

**This is not a notification bug**, even though that is how it was reported. The
user has no shortcuts and no chat traffic, so opening notification settings was
simply the first thing that touched WPF. Chat would crash identically on its
first message.

## Root cause

`scripts\Publish-Poc.ps1` publishes with `-p:PublishSingleFile=true`, and the
tray project sets `UseWPF=true`.

**Single-file publishing excludes native libraries by default.** WPF's native
dependencies — `PresentationNative_cor3.dll`, `wpfgfx_cor3.dll`,
`D3DCompiler_47_cor3.dll`, `vcruntime140_cor3.dll` — are therefore left as loose
files beside the exe instead of being bundled into it.

The dev machine works because it runs from `artifacts\publish\` where those loose
files sit. A user who copies `SteamVR2Bot.exe` elsewhere — which is what a
single-file exe invites — takes none of them.

## The fix: stop publishing single-file

**This app was never a single-file app.** It already requires `app.vrmanifest`,
`actions.json`, `bindings_vive_controller.json` and `SteamVR2Bot.png` beside the
executable to function at all. `PublishSingleFile` buys nothing except an
implication of portability that is false and is actively breaking users.

- **Remove `-p:PublishSingleFile=true`** from the tray publish in
  `scripts\Publish-Poc.ps1`, so the whole dependency set — managed and native —
  lands in the output folder together.
- Do the same for the diagnostics publish unless it demonstrably has no WPF or
  other native dependency. Consistency is worth more than one fewer file.
- **Do not** instead reach for `IncludeNativeLibrariesForSelfExtract=true`. It
  would bundle the natives, but it extracts them to a temp directory at runtime,
  which slows first launch and is a well-known trigger for security software.
  For an app that must ship a folder regardless, it adds risk for no benefit.

### Distribution

Since the deliverable is now unambiguously a folder, make that explicit:

- Have the publish script emit a **zip of the publish folder**, named with the
  version, as the artifact to hand testers.
- Update the README's setup section: users extract the folder and run the exe
  from inside it. Say plainly that the exe will not work moved out on its own —
  that is the mistake this bug came from.

## Also fix: the failure is silent and loops

When WPF failed, the worker died and the app entered its normal SteamVR
reconnect cycle. That cycle is designed for SteamVR being unavailable, which was
not the problem. The user saw retry messages about SteamVR and nothing about the
real fault.

- A `DllNotFoundException`, or any failure that will recur identically on the
  next attempt, must **not** be treated as a transient SteamVR dropout.
- Report it distinctly in the status area and activity log — something a user
  can act on, naming the missing component and pointing at running the app from
  its published folder.
- **Do not retry indefinitely on a failure that cannot resolve itself.** The
  existing reconnect ladder exists for SteamVR restarting; a missing DLL will
  never fix itself mid-session.

## Hard constraints

1. No new NuGet packages.
2. **Do not modify** `StreamerBotClient.cs`, the input/gesture/chord/binding
   code, or `MotionSampling`.
3. **Do not break** chat, notifications, the dashboard, or shortcut delivery.
4. Do not change WPF rendering itself — the renderers are correct; only their
   deployment is broken.

## Verification — required

1. **The decisive test, and it must be done off the dev machine's publish
   folder:**
   - Run `scripts\Publish-Poc.ps1`.
   - Copy the **entire** publish folder to a different location — ideally a
     different machine, or at minimum a path with no prior build output.
   - Run the app there and open the notification settings page.
   - **No crash, no `DllNotFoundException`, no worker restart.**
   - Confirm chat renders too, since it uses the same WPF path.
2. **Then verify the failure mode you are fixing still reproduces on the old
   packaging**, so you know the test is meaningful — publish once with
   `PublishSingleFile=true`, copy only the exe out, and confirm the crash.
   Then confirm the new packaging does not crash under the same treatment,
   or fails with the clear message rather than a silent retry loop.
3. All existing tests pass. Build clean, no new warnings.
4. Append the result to `LIVE_TEST_RESULTS.md` following its conventions.

## When you are done

Summarise what changed, confirm the published folder runs correctly from a fresh
location, and state what a tester should now be sent — folder, zip, or both.
