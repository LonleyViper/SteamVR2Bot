# SteamVR2Bot

SteamVR2Bot runs Streamer.bot actions from safe SteamVR controller shortcuts.
It uses SteamVR actions directly—there is no keyboard emulation, browser overlay,
or OpenVR2Key layer.

The validated Vive shortcut is:

> Hold **Left Grip**, then press **Right Trigger**

The left grip is a safety button, so pulling the trigger by itself does nothing.

## The desktop window

![The SteamVR2Bot desktop window on the Shortcuts tab, listing two saved controller shortcuts and the Streamer.bot action each one runs](docs/images/desktop-ui.png)

Every shortcut is one row: the gesture, the controller inputs it listens for,
and the Streamer.bot action it runs. The banner above the tabs reports the
current state — captured here before SteamVR was started.

## Everyday setup

Build the ready-to-run folder:

```powershell
.\scripts\Publish-Poc.ps1
```

The published app includes its required .NET runtime, so no separate .NET
installation is needed for normal use.

Then open:

```text
artifacts\publish\SteamVR2Bot.exe
```

In the SteamVR2Bot window:

1. Enter the Streamer.bot WebSocket address.
2. Enter the connection password only if Streamer.bot requires one.
3. On **Connection & setup**, choose **Refresh Streamer.bot actions**.
4. On **Shortcuts**, choose **Add shortcut**.
5. Give it a clear name, choose its Streamer.bot action, then choose
   **Record controller inputs**.
6. In VR, release the buttons, hold the safety input, then press the action
   input.
7. Save the shortcut and use **Test action**.

The Shortcuts page lists every gesture and the action it runs. Select a row to
edit, test, enable or disable, or remove it. Existing single-action settings
are migrated automatically as the first shortcut. Every change saves
automatically and is applied to the running bridge.

SteamVR2Bot registers itself with SteamVR and starts automatically whenever the
app is open. There is no separate Save or Start step.

Closing the window keeps SteamVR2Bot running in the Windows notification area.
Use its tray menu to open the app or SteamVR dashboard, test an action, view
logs, or exit.

## SteamVR lifecycle

SteamVR2Bot adds itself to SteamVR's startup list, so SteamVR launches it, and
it closes again when SteamVR shuts down. Nothing is left running in the tray
with no SteamVR to talk to.

Opening SteamVR2Bot from the desktop does not start SteamVR. It waits instead,
retrying after 1, 2, 5, 10, then 30 seconds, so Streamer.bot settings and
shortcuts stay editable without a headset.

Only one copy runs at a time. Launching it again brings the existing window
forward rather than starting a second tray icon, and registration removes any
SteamVR entry left behind by an older install — otherwise SteamVR would launch
a copy from each registered folder.

## In-VR setup

While SteamVR2Bot is open, its tab is always available in the SteamVR dashboard.
Choose the tab in SteamVR, or use **Open SteamVR dashboard** on the Connection &
setup page. It shows the currently saved shortcuts and what each one runs.

![The SteamVR2Bot dashboard tab in VR, listing saved shortcuts with edit and delete buttons and a Create a new shortcut button](docs/images/vr-shortcut-list.png)

To create one without leaving VR:

1. Select **Create a new shortcut**.
2. Choose **Single Button**, **Button Combo**, **Double Press**, or **Long
   Hold**.
3. For Double Press or Long Hold, set the detection tolerance with the slider.
4. Choose the physical input from the grouped Left and Right controller lists.
   A Button Combo selects two different inputs. For live recording, press the
   controller's System button once to close the SteamVR menu, then press the
   desired input. SteamVR2Bot reopens directly on the review page. The lists
   remain available when the menu owns controller focus.
5. Review the exact recorded input names, choose the Streamer.bot action, then
   select **Save shortcut**.

![The in-VR gesture picker, offering Single Button, Button Combo, Double Press and Long Hold](docs/images/vr-gesture-picker.png)

![The in-VR review page, showing the chosen gesture, the recorded controller input, and the Streamer.bot action before saving](docs/images/vr-review.png)

The input picker and review page make the left/right controller and physical
input explicit before anything is saved. The shortcut appears in the desktop
list and becomes active without restarting the SteamVR dashboard.

Each saved row has a pencil button to change its action, input, or gesture, and
an X button to delete it. Both changes save and activate automatically.

## Controller inputs

The VR picker presents controller-aware physical input names, such as **Left
Menu Button**, **Left Grip**, and **Right Trigger**. This avoids SteamVR's
dashboard consuming a button while the wizard is trying to observe it. SteamVR
reserves raw controller input while its system dashboard has focus, so live
recording briefly yields that focus and returns to the wizard automatically.
SteamVR2Bot installs its complete Vive input map automatically, so these choices
do not require a separate visit to SteamVR Controller Bindings.

The recorder is validated first for Vive controllers. Controller button layouts
vary by family. For Index, Touch, WMR, Cosmos, or another controller, verify the
recorded names and run the live test matrix before treating it as a packaged
default.

The original SteamVR logical-input route remains available as a compatibility
fallback:

- **Safety Button** — held to prevent an accidental command.
- **Action Button** — pressed to run the selected Streamer.bot action.

Choose **SteamVR input bindings** to open the official binding page directly.
SteamVR keeps a separate binding for each controller family, so changing an
Index binding does not overwrite a Vive or Touch binding.

The packaged Vive preset—Left Grip plus Right Trigger—is live-validated. Other
controller families are detected and can be configured through SteamVR, but no
untested default preset is labelled as validated.

Each shortcut's gesture behavior supports:

- Press one chosen input once.
- Press two chosen inputs together within 300 ms.
- Double press one chosen input with an adjustable 200–1200 ms tolerance.
- Hold one chosen input for an adjustable 0.5–5 seconds.
- Hold the Safety Button, then press the Action Button.

## Streamer.bot address

When Streamer.bot is on the same PC, the usual address is:

```text
ws://127.0.0.1:8080/1
```

When it is on the streaming PC, use that PC's private-network address, for
example:

```text
ws://192.168.1.50:8080/1
```

Streamer.bot's WebSocket server must allow LAN connections, and its port must be
allowed through Windows Firewall on the private network.

SteamVR2Bot displays action names but stores the action's stable ID after it is
selected. Renaming an action in Streamer.bot therefore does not silently point
the shortcut at a different action.

## Password safety

The Streamer.bot password is saved in:

```text
%LOCALAPPDATA%\SteamVR2Bot\settings.json
```

Windows protects it for the current Windows user. It is not stored as readable
text and is never written to the activity log. On first launch, SteamVR2Bot can
import the old gitignored `appsettings.json` and immediately protect its
password.

## SteamVR binding

SteamVR setup is registered automatically when the app opens. Use **Repair
SteamVR setup** only if the dashboard entry or controller binding was removed.
The packaged Vive binding uses:

- **Safety button:** Left Grip
- **Action button:** Right Trigger

To change it, open SteamVR Controller Bindings and select **SteamVR2Bot**. Its
logical controls are:

- **Safety Button (hold)**
- **Action Button (press)**

The bridge has passed 20/20 attempts in the SteamVR shell through both the
diagnostic and tray hosts. With GERONIMO active and the dashboard closed, it
passed 20/20 through the diagnostic host and 21/21 through the tray host. See
`LIVE_TEST_RESULTS.md` for the evidence.

## Status messages

- **Stopped** — the controller shortcut is not running.
- **Starting…** — SteamVR2Bot is connecting to SteamVR.
- **Ready for your shortcut** — controller input is active.
- **Running your action…** — the shortcut was detected.
- **Needs attention** — the status detail explains what to check.

Recent activity shows input and delivery events without exposing credentials.

The same events are written as structured JSON lines under:

```text
%LOCALAPPDATA%\SteamVR2Bot\Logs
```

Use **Open logs** in the app or tray menu. Logs are kept for 14 days and never
contain the Streamer.bot password.

## Restart and network recovery

- SteamVR input runs in a small disposable worker. If that worker stops
  unexpectedly, the tray app remains open and creates a new one after 1, 2, 5,
  10, then 30 seconds. A deliberate SteamVR shutdown is told apart from a lost
  worker, and closes SteamVR2Bot instead of starting that retry cycle.
- If Streamer.bot is unavailable before delivery, the bridge makes three
  bounded connection attempts. The next controller shortcut tries again.
- If the connection is lost after delivery begins, the action is not blindly
  resent. The status says that confirmation was lost; avoiding a possible
  duplicate takes priority.

## Diagnostics

The console diagnostic remains available at:

```text
artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe
```

Useful checks:

```powershell
.\artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe --self-test
.\artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe --simulate
.\artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe --inspect-bindings
.\artifacts\publish\diagnostics\SteamVR2Bot.Diagnostics.exe --open-bindings
.\scripts\Register-SteamVrApp.ps1
```

The normal daily-use path is the tray app; the console is retained for
troubleshooting and regression testing.
